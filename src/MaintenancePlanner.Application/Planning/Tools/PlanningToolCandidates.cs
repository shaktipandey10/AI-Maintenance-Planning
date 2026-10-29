using System.Text.Json;
using MaintenancePlanner.Application.Queries;

namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>
/// Reads the candidate schedule shared by <c>validate_schedule</c> and <c>calculate_cost</c> into the existing
/// <see cref="PlanningCandidateValidationRequest"/> contract, with the window and asset IDs in their stored spelling.
/// </summary>
internal static class PlanningToolCandidates
{
    public static readonly string[] Arguments = ["assetIds", "windowId", "placements"];

    public static readonly JsonElement ParametersSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "assetIds": { "type": "array", "items": { "type": "string" }, "description": "The assets the schedule covers." },
            "windowId": { "type": "string", "description": "The maintenance window the schedule must fit." },
            "placements": {
              "type": "array",
              "description": "One placement per scheduled task.",
              "items": {
                "type": "object",
                "properties": {
                  "taskId": { "type": "string" },
                  "start": { "type": "string", "description": "ISO 8601 with an explicit offset." },
                  "end": { "type": "string", "description": "ISO 8601 with an explicit offset." },
                  "technicianIds": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["taskId", "start", "end", "technicianIds"],
                "additionalProperties": false
              }
            }
          },
          "required": ["assetIds", "windowId", "placements"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    /// <summary>
    /// The candidate as a validation request, or the error to return. The replaced-plan context
    /// (<c>CompletedTaskIds</c>, <c>ReleasableReservedQuantities</c>) is left empty: the model does not supply it.
    /// </summary>
    public static async Task<(PlanningCandidateValidationRequest? Request, PlanningToolResult? Failure)> ReadAsync(
        JsonElement arguments,
        IMaintenancePlannerQueries queries,
        CancellationToken cancellationToken)
    {
        var reader = PlanningToolArguments.Read(arguments, Arguments);
        var assetIds = reader.RequiredIdentifierList("assetIds");
        var windowId = reader.RequiredIdentifier("windowId");
        var placements = reader.RequiredPlacements("placements");
        if (reader.Error is not null)
            return (null, PlanningToolResult.Failure(PlanningToolErrors.InvalidArguments, reader.Error));

        var window = await PlanningToolLookups.FindWindowIdAsync(queries, windowId!, cancellationToken);
        if (window is null)
            return (null, PlanningToolResult.Failure(PlanningToolErrors.UnknownWindow, $"Maintenance window {windowId} does not exist."));

        var assets = new List<string>();
        foreach (var assetId in assetIds!)
        {
            var asset = await PlanningToolLookups.FindAssetIdAsync(queries, assetId, cancellationToken);
            if (asset is null)
                return (null, PlanningToolResult.Failure(PlanningToolErrors.UnknownAsset, $"Asset {assetId} does not exist."));

            assets.Add(asset);
        }

        return (new PlanningCandidateValidationRequest(
            assets,
            window,
            new PlanningCandidate(placements!, Rationale: string.Empty, EvidenceReferences: [])), null);
    }
}
