using MaintenancePlanner.Application.Queries;

namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>Resolves identifiers from tool arguments to their stored spelling, as the planning workflow matches them.</summary>
internal static class PlanningToolLookups
{
    public static async Task<string?> FindAssetIdAsync(
        IMaintenancePlannerQueries queries,
        string assetId,
        CancellationToken cancellationToken) =>
        (await queries.GetAssetsAsync(cancellationToken))
            .FirstOrDefault(asset => string.Equals(asset.Id, assetId, StringComparison.OrdinalIgnoreCase))?
            .Id;

    public static async Task<string?> FindWindowIdAsync(
        IMaintenancePlannerQueries queries,
        string windowId,
        CancellationToken cancellationToken) =>
        (await queries.GetMaintenanceWindowsAsync(cancellationToken))
            .FirstOrDefault(window => string.Equals(window.Id, windowId, StringComparison.OrdinalIgnoreCase))?
            .Id;
}
