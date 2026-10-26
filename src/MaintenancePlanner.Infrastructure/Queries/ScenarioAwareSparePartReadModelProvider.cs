using MaintenancePlanner.Application.Queries;

namespace MaintenancePlanner.Infrastructure.Queries;

public sealed class ScenarioAwareSparePartReadModelProvider(
    IMaintenancePlannerQueries baseQueries) : ISparePartReadModelProvider
{
    private static readonly Dictionary<string, Dictionary<string, (decimal OnHand, decimal Reserved)>>
        ScenarioOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            {
                "MW-P101-REDUCED-20261024",
                new(StringComparer.OrdinalIgnoreCase)
                {
                    { "BR-101", (1m, 1m) }
                }
            }
        };

    public async Task<IReadOnlyList<SparePartReadModel>> GetSparePartsAsync(
        string? windowId,
        CancellationToken cancellationToken)
    {
        var parts = await baseQueries.GetSparePartsAsync(cancellationToken);
        if (windowId is null || !ScenarioOverrides.TryGetValue(windowId, out var overrides))
            return parts;

        return parts
            .Select(part => ApplyOverride(part, overrides))
            .ToList();
    }

    public async Task<IReadOnlyList<SparePartReadModel>> GetCompatibleSparePartsAsync(
        string assetId,
        string? windowId,
        CancellationToken cancellationToken)
    {
        var parts = await baseQueries.GetCompatibleSparePartsAsync(assetId, cancellationToken);
        if (windowId is null || !ScenarioOverrides.TryGetValue(windowId, out var overrides))
            return parts;

        return parts
            .Select(part => ApplyOverride(part, overrides))
            .ToList();
    }

    private static SparePartReadModel ApplyOverride(
        SparePartReadModel part,
        Dictionary<string, (decimal OnHand, decimal Reserved)> overrides)
    {
        if (overrides.TryGetValue(part.Id, out var override_))
        {
            var onHand = override_.OnHand;
            var reserved = override_.Reserved;
            return part with
            {
                OnHandQuantity = onHand,
                ReservedQuantity = reserved,
                AvailableQuantity = onHand - reserved
            };
        }
        return part;
    }
}
