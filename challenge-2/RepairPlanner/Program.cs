using System;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlanner;
using RepairPlanner.Models;
using RepairPlanner.Services;

var projectEndpoint = GetRequiredEnv("AZURE_AI_PROJECT_ENDPOINT");
var modelDeploymentName = GetRequiredEnv("MODEL_DEPLOYMENT_NAME");
var cosmosEndpoint = GetRequiredEnv("COSMOS_ENDPOINT");
var cosmosKey = GetRequiredEnv("COSMOS_KEY");
var cosmosDatabaseName = GetRequiredEnv("COSMOS_DATABASE_NAME");

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddSingleton<IFaultMappingService, FaultMappingService>();
services.AddSingleton(_ => new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential()));
services.AddSingleton(sp =>
{
	var options = new CosmosDbOptions
	{
		Endpoint = cosmosEndpoint,
		Key = cosmosKey,
		DatabaseName = cosmosDatabaseName,
	};

	return new CosmosDbService(options, sp.GetRequiredService<ILogger<CosmosDbService>>());
});
services.AddSingleton(sp => new RepairPlannerAgent(
	sp.GetRequiredService<AIProjectClient>(),
	sp.GetRequiredService<CosmosDbService>(),
	sp.GetRequiredService<IFaultMappingService>(),
	modelDeploymentName,
	sp.GetRequiredService<ILogger<RepairPlannerAgent>>()));

// await using - like Python's "async with"
await using var provider = services.BuildServiceProvider();

var agent = provider.GetRequiredService<RepairPlannerAgent>();
var programLogger = provider.GetRequiredService<ILogger<Program>>();
await agent.EnsureAgentVersionAsync();

var sampleFault = new DiagnosedFault
{
	Id = Guid.NewGuid().ToString("n"),
	MachineId = "TCP-01",
	FaultType = "curing_temperature_excessive",
	Severity = "high",
	Description = "Temperature exceeds target by 25C during curing cycle.",
	DetectedAtUtc = DateTimeOffset.UtcNow,
	Confidence = 0.92,
};

var workOrder = await agent.PlanAndCreateWorkOrderAsync(sampleFault);
programLogger.LogInformation(
	"Saved work order {WorkOrderNumber} (id={WorkOrderId}, status={Status}, assignedTo={AssignedTo}).",
	workOrder.WorkOrderNumber,
	workOrder.Id,
	workOrder.Status,
	workOrder.AssignedTo ?? "unassigned");
var output = JsonSerializer.Serialize(workOrder, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(output);

static string GetRequiredEnv(string name)
{
	var value = Environment.GetEnvironmentVariable(name);
	if (string.IsNullOrWhiteSpace(value))
	{
		throw new InvalidOperationException($"Environment variable '{name}' is required.");
	}

	return value;
}
