using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;
using RepairPlanner.Services;

namespace RepairPlanner;

// Primary constructor - parameters become fields (like Python's __init__)
public sealed class RepairPlannerAgent(
    AIProjectClient projectClient,
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    string modelDeploymentName,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";
    private const string AgentInstructions = """
You are a Repair Planner Agent for tire manufacturing equipment.
Generate a repair plan with tasks, timeline, and resource allocation.
Return the response as valid JSON matching the WorkOrder schema.

Output JSON with these fields:
- workOrderNumber, machineId, title, description
- type: "corrective" | "preventive" | "emergency"
- priority: "critical" | "high" | "medium" | "low"
- status, assignedTo (technician id or null), notes
- estimatedDuration: integer (minutes, e.g. 60 not "60 minutes")
- partsUsed: [{ partId, partNumber, quantity }]
- tasks: [{ sequence, title, description, estimatedDurationMinutes (integer), requiredSkills, safetyNotes }]

IMPORTANT: All duration fields must be integers representing minutes (e.g. 90), not strings.

Rules:
- Assign the most qualified available technician
- Include only relevant parts; empty array if none needed
- Tasks must be ordered and actionable
""";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Creating agent '{AgentName}' with model '{ModelName}'.", AgentName, modelDeploymentName);
        var definition = new PromptAgentDefinition(model: modelDeploymentName)
        {
            Instructions = AgentInstructions,
        };

        var created = await projectClient.Agents.CreateAgentVersionAsync(
            AgentName,
            new AgentVersionCreationOptions(definition),
            ct);

        var versionId = created.Value?.Id ?? "unknown";
        logger.LogInformation("Agent version: {AgentVersion}.", versionId);
    }

    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(DiagnosedFault fault, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fault);

        logger.LogInformation(
            "Planning repair for {MachineId}, fault={FaultType}.",
            fault.MachineId,
            fault.FaultType);

        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredParts = faultMapping.GetRequiredParts(fault.FaultType);

        var technicians = await cosmosDb.GetAvailableTechniciansWithSkillsAsync(requiredSkills, ct);
        var partsInventory = await cosmosDb.GetPartsInventoryAsync(requiredParts, ct);

        var bestTechnician = SelectBestTechnician(technicians, requiredSkills);
        var prompt = BuildPrompt(fault, requiredSkills, requiredParts, technicians, partsInventory, bestTechnician);

        var agent = projectClient.GetAIAgent(name: AgentName);
        logger.LogInformation("Invoking agent '{AgentName}'.", AgentName);
        var response = await agent.RunAsync(prompt, thread: null, options: null);
        var json = ExtractJson(response.Text ?? string.Empty);

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Agent returned an empty response.");
        }

        WorkOrder workOrder;
        try
        {
            workOrder = JsonSerializer.Deserialize<WorkOrder>(json, JsonOptions)
                        ?? throw new InvalidOperationException("Agent returned invalid JSON.");
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse work order JSON from agent.");
            throw;
        }

        ApplyDefaults(workOrder, fault, bestTechnician);
        var saved = await cosmosDb.CreateWorkOrderAsync(workOrder, ct);

        logger.LogInformation("Work order created with id {WorkOrderId}.", saved.Id);
        return saved;
    }

    private static Technician? SelectBestTechnician(
        IReadOnlyList<Technician> technicians,
        IReadOnlyList<string> requiredSkills)
    {
        if (technicians.Count == 0)
        {
            return null;
        }

        var required = new HashSet<string>(requiredSkills, StringComparer.OrdinalIgnoreCase);

        return technicians
            .OrderByDescending(t => t.Skills.Count(skill => required.Contains(skill)))
            .ThenBy(t => t.Name)
            .FirstOrDefault();
    }

    private static string BuildPrompt(
        DiagnosedFault fault,
        IReadOnlyList<string> requiredSkills,
        IReadOnlyList<string> requiredParts,
        IReadOnlyList<Technician> technicians,
        IReadOnlyList<Part> partsInventory,
        Technician? bestTechnician)
    {
        var technicianSummary = technicians.Count == 0
            ? "No technicians available."
            : string.Join("\n", technicians.Select(t =>
                $"- id: {t.Id}, name: {t.Name}, skills: [{string.Join(", ", t.Skills)}], available: {t.IsAvailable}"));

        var partsSummary = partsInventory.Count == 0
            ? "No parts found."
            : string.Join("\n", partsInventory.Select(p =>
                $"- id: {p.Id}, partNumber: {p.PartNumber}, name: {p.Name}, qty: {p.QuantityAvailable}"));

        var preferredTechnician = bestTechnician is null
            ? "None"
            : $"{bestTechnician.Id} ({bestTechnician.Name})";

        return $"""
Diagnosed fault:
- machineId: {fault.MachineId}
- faultType: {fault.FaultType}
- severity: {fault.Severity}
- description: {fault.Description}
- detectedAtUtc: {fault.DetectedAtUtc}
- confidence: {fault.Confidence}

Required skills: [{string.Join(", ", requiredSkills)}]
Required parts (part numbers): [{string.Join(", ", requiredParts)}]

Available technicians:
{technicianSummary}

Parts inventory:
{partsSummary}

Preferred technician (most qualified available): {preferredTechnician}

Generate a work order JSON that matches the schema in your instructions.
""";
    }

    private static void ApplyDefaults(WorkOrder workOrder, DiagnosedFault fault, Technician? bestTechnician)
    {
        if (string.IsNullOrWhiteSpace(workOrder.Id))
        {
            workOrder.Id = Guid.NewGuid().ToString("n");
        }

        if (string.IsNullOrWhiteSpace(workOrder.WorkOrderNumber))
        {
            workOrder.WorkOrderNumber = $"WO-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        }

        if (string.IsNullOrWhiteSpace(workOrder.MachineId))
        {
            workOrder.MachineId = fault.MachineId;
        }

        if (string.IsNullOrWhiteSpace(workOrder.Type))
        {
            workOrder.Type = "corrective";
        }

        if (string.IsNullOrWhiteSpace(workOrder.Priority))
        {
            workOrder.Priority = "medium";
        }

        if (string.IsNullOrWhiteSpace(workOrder.Status))
        {
            workOrder.Status = "new";
        }

        workOrder.AssignedTo ??= bestTechnician?.Id; // ??= means "assign if null" (like Python's: x = x or default_value)
        workOrder.PartsUsed ??= new List<WorkOrderPartUsage>();
        workOrder.Tasks ??= new List<RepairTask>();

        if (workOrder.CreatedAtUtc is null)
        {
            workOrder.CreatedAtUtc = DateTimeOffset.UtcNow;
        }

        workOrder.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static string ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstFenceEnd = trimmed.IndexOf('\n');
            if (firstFenceEnd >= 0)
            {
                trimmed = trimmed[(firstFenceEnd + 1)..];
            }

            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
            {
                trimmed = trimmed[..lastFence];
            }
        }

        trimmed = trimmed.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : string.Empty;
    }
}
