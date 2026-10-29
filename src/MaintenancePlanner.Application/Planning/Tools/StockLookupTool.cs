using System.Text.Json;
using MaintenancePlanner.Application.Queries;

namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>
/// Spare-part stock lookup. Reads through <see cref="ISparePartReadModelProvider"/>, the same window-aware source the
/// candidate validator uses, so the model and the validator see the same quantities.
/// </summary>
public sealed class StockLookupTool(
    ISparePartReadModelProvider sparePartProvider,
    IMaintenancePlannerQueries queries) : IPlanningTool
{
    public const string ToolName = "lookup_stock";

    private static readonly string[] AllowedArguments = ["windowId", "assetId", "partIds"];

    public string Name => ToolName;

    public string Description =>
        "Look up spare-part stock (on hand, reserved, available). Optionally limit to parts compatible with an asset, " +
        "to specific part IDs, or apply the inventory of a maintenance window.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "windowId": { "type": "string", "description": "Maintenance window ID whose inventory applies." },
            "assetId": { "type": "string", "description": "Only parts compatible with this asset." },
            "partIds": { "type": "array", "items": { "type": "string" }, "description": "Only these part IDs." }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public async Task<PlanningToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var reader = PlanningToolArguments.Read(arguments, AllowedArguments);
        var windowId = reader.OptionalIdentifier("windowId");
        var assetId = reader.OptionalIdentifier("assetId");
        var partIds = reader.OptionalIdentifierList("partIds");
        if (reader.Error is not null)
            return PlanningToolResult.Failure(PlanningToolErrors.InvalidArguments, reader.Error);

        string? window = null;
        if (windowId is not null)
        {
            window = await PlanningToolLookups.FindWindowIdAsync(queries, windowId, cancellationToken);
            if (window is null)
                return PlanningToolResult.Failure(PlanningToolErrors.UnknownWindow, $"Maintenance window {windowId} does not exist.");
        }

        string? asset = null;
        if (assetId is not null)
        {
            asset = await PlanningToolLookups.FindAssetIdAsync(queries, assetId, cancellationToken);
            if (asset is null)
                return PlanningToolResult.Failure(PlanningToolErrors.UnknownAsset, $"Asset {assetId} does not exist.");
        }

        var parts = asset is null
            ? await sparePartProvider.GetSparePartsAsync(window, cancellationToken)
            : await sparePartProvider.GetCompatibleSparePartsAsync(asset, window, cancellationToken);

        if (partIds is not null)
        {
            var missing = partIds.FirstOrDefault(partId =>
                !parts.Any(part => string.Equals(part.Id, partId, StringComparison.OrdinalIgnoreCase)));
            if (missing is not null)
            {
                return PlanningToolResult.Failure(
                    PlanningToolErrors.UnknownPart,
                    asset is null
                        ? $"Spare part {missing} does not exist."
                        : $"Spare part {missing} does not exist or is not compatible with asset {asset}.");
            }

            parts = parts
                .Where(part => partIds.Contains(part.Id, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        // Stock is only passed on while it is internally consistent; an inconsistent record is not repaired here
        var inconsistent = parts.FirstOrDefault(part =>
            string.IsNullOrWhiteSpace(part.Id) ||
            part.OnHandQuantity < 0 ||
            part.ReservedQuantity < 0 ||
            part.AvailableQuantity != part.OnHandQuantity - part.ReservedQuantity);
        if (inconsistent is not null)
        {
            return PlanningToolResult.Failure(
                PlanningToolErrors.InvalidToolResult,
                $"The stock record for spare part {inconsistent.Id} is inconsistent and cannot be used.");
        }

        return PlanningToolResult.Success(new StockLookupResult(window, asset, parts));
    }
}

public sealed record StockLookupResult(
    string? WindowId,
    string? AssetId,
    IReadOnlyList<SparePartReadModel> Parts);
