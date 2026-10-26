namespace MaintenancePlanner.Application.Queries;

/// <summary>Provides spare part read models with optional scenario-specific inventory overrides.</summary>
public interface ISparePartReadModelProvider
{
    /// <summary>Get spare parts, optionally overridden by maintenance window scenario.</summary>
    Task<IReadOnlyList<SparePartReadModel>> GetSparePartsAsync(
        string? windowId,
        CancellationToken cancellationToken);

    /// <summary>Get spare parts compatible with an asset, optionally overridden by window scenario.</summary>
    Task<IReadOnlyList<SparePartReadModel>> GetCompatibleSparePartsAsync(
        string assetId,
        string? windowId,
        CancellationToken cancellationToken);
}
