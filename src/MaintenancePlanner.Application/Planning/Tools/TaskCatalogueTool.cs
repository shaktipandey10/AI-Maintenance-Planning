using System.Text.Json;
using MaintenancePlanner.Application.Queries;

namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>Task retrieval: an asset's maintenance task catalogue, in predecessor order.</summary>
public sealed class TaskCatalogueTool(IMaintenancePlannerQueries queries) : IPlanningTool
{
    public const string ToolName = "get_task_catalogue";

    private static readonly string[] AllowedArguments = ["assetId"];

    public string Name => ToolName;

    public string Description =>
        "Get an asset's maintenance task catalogue: durations, required skills and crew size, required parts, " +
        "predecessors and the source manual section of each task.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "assetId": { "type": "string", "description": "The asset whose task catalogue is returned." }
          },
          "required": ["assetId"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public async Task<PlanningToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var reader = PlanningToolArguments.Read(arguments, AllowedArguments);
        var assetId = reader.RequiredIdentifier("assetId");
        if (reader.Error is not null)
            return PlanningToolResult.Failure(PlanningToolErrors.InvalidArguments, reader.Error);

        var asset = await PlanningToolLookups.FindAssetIdAsync(queries, assetId!, cancellationToken);
        var tasks = asset is null ? null : await queries.GetTaskCatalogueAsync(asset, cancellationToken);
        if (asset is null || tasks is null)
            return PlanningToolResult.Failure(PlanningToolErrors.UnknownAsset, $"Asset {assetId} does not exist.");

        var invalid = tasks.FirstOrDefault(task =>
            string.IsNullOrWhiteSpace(task.Id) ||
            !string.Equals(task.AssetId, asset, StringComparison.OrdinalIgnoreCase) ||
            task.DurationMinutes <= 0 ||
            task.RequiredCrewSize <= 0 ||
            task.RequiredPartQuantity < 0);
        if (invalid is not null)
        {
            return PlanningToolResult.Failure(
                PlanningToolErrors.InvalidToolResult,
                $"The catalogue record of task {invalid.Id} is inconsistent and cannot be used.");
        }

        return PlanningToolResult.Success(new TaskCatalogueToolResult(asset, tasks));
    }
}

public sealed record TaskCatalogueToolResult(string AssetId, IReadOnlyList<MaintenanceTaskReadModel> Tasks);
