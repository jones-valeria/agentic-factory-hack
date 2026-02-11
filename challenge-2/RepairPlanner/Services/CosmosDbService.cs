using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;

namespace RepairPlanner.Services;

public sealed class CosmosDbService
{
    private const string TechniciansContainerName = "Technicians";
    private const string PartsContainerName = "PartsInventory";
    private const string WorkOrdersContainerName = "WorkOrders";

    private readonly CosmosClient _client;
    private readonly Container _techniciansContainer;
    private readonly Container _partsContainer;
    private readonly Container _workOrdersContainer;
    private readonly ILogger<CosmosDbService> _logger;

    public CosmosDbService(CosmosDbOptions options, ILogger<CosmosDbService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new ArgumentException("Cosmos endpoint is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Key))
        {
            throw new ArgumentException("Cosmos key is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.DatabaseName))
        {
            throw new ArgumentException("Cosmos database name is required.", nameof(options));
        }

        _logger = logger;
        _client = new CosmosClient(options.Endpoint, options.Key);
        var database = _client.GetDatabase(options.DatabaseName);

        _techniciansContainer = database.GetContainer(TechniciansContainerName);
        _partsContainer = database.GetContainer(PartsContainerName);
        _workOrdersContainer = database.GetContainer(WorkOrdersContainerName);
    }

    public async Task<IReadOnlyList<Technician>> GetAvailableTechniciansWithSkillsAsync(
        IReadOnlyList<string> requiredSkills,
        CancellationToken ct = default)
    {
        try
        {
            var normalizedSkills = requiredSkills
                .Where(skill => !string.IsNullOrWhiteSpace(skill))
                .Select(skill => skill.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var queryText = "SELECT * FROM c WHERE c.isAvailable = true";
            var queryDefinition = new QueryDefinition(queryText);

            if (normalizedSkills.Count > 0)
            {
                var skillFilters = new List<string>();
                for (var i = 0; i < normalizedSkills.Count; i++)
                {
                    var paramName = $"@skill{i}";
                    skillFilters.Add($"ARRAY_CONTAINS(c.skills, {paramName})");
                    queryDefinition.WithParameter(paramName, normalizedSkills[i]);
                }

                queryText = $"SELECT * FROM c WHERE c.isAvailable = true AND {string.Join(" AND ", skillFilters)}";
                queryDefinition = new QueryDefinition(queryText);
                for (var i = 0; i < normalizedSkills.Count; i++)
                {
                    queryDefinition.WithParameter($"@skill{i}", normalizedSkills[i]);
                }
            }

            var technicians = new List<Technician>();
            using var iterator = _techniciansContainer.GetItemQueryIterator<Technician>(queryDefinition);

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(ct);
                technicians.AddRange(response);
            }

            _logger.LogInformation(
                "Found {TechnicianCount} available technicians matching skills.",
                technicians.Count);

            return technicians;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to query technicians from Cosmos DB.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while querying technicians.");
            throw;
        }
    }

    public async Task<IReadOnlyList<Part>> GetPartsInventoryAsync(
        IReadOnlyList<string> partNumbers,
        CancellationToken ct = default)
    {
        if (partNumbers.Count == 0)
        {
            return Array.Empty<Part>();
        }

        try
        {
            var normalizedParts = partNumbers
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedParts.Count == 0)
            {
                return Array.Empty<Part>();
            }

            var parameterNames = new List<string>();
            var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.partNumber IN ({0})");

            for (var i = 0; i < normalizedParts.Count; i++)
            {
                var paramName = $"@part{i}";
                parameterNames.Add(paramName);
                queryDefinition.WithParameter(paramName, normalizedParts[i]);
            }

            var queryText = $"SELECT * FROM c WHERE c.partNumber IN ({string.Join(", ", parameterNames)})";
            queryDefinition = new QueryDefinition(queryText);
            for (var i = 0; i < normalizedParts.Count; i++)
            {
                queryDefinition.WithParameter($"@part{i}", normalizedParts[i]);
            }

            var parts = new List<Part>();
            using var iterator = _partsContainer.GetItemQueryIterator<Part>(queryDefinition);

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(ct);
                parts.AddRange(response);
            }

            _logger.LogInformation("Fetched {PartCount} parts.", parts.Count);

            return parts;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to query parts inventory from Cosmos DB.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while querying parts inventory.");
            throw;
        }
    }

    public async Task<WorkOrder> CreateWorkOrderAsync(WorkOrder workOrder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workOrder);

        try
        {
            if (string.IsNullOrWhiteSpace(workOrder.Id))
            {
                workOrder.Id = Guid.NewGuid().ToString("n");
            }

            if (string.IsNullOrWhiteSpace(workOrder.WorkOrderNumber))
            {
                workOrder.WorkOrderNumber = $"WO-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            }

            workOrder.CreatedAtUtc ??= DateTimeOffset.UtcNow; // ??= means "assign if null" (like Python's: x = x or default_value)
            workOrder.UpdatedAtUtc = DateTimeOffset.UtcNow;

            var response = await _workOrdersContainer.CreateItemAsync(
                workOrder,
                new PartitionKey(workOrder.Status),
                cancellationToken: ct);

            _logger.LogInformation("Created work order {WorkOrderId} with status {Status}.", workOrder.Id, workOrder.Status);
            return response.Resource;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to create work order in Cosmos DB.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while creating work order.");
            throw;
        }
    }
}
