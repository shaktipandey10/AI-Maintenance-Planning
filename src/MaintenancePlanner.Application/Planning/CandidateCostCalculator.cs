using MaintenancePlanner.Application.Queries;

namespace MaintenancePlanner.Application.Planning;

public sealed record CandidateCostMetrics(
    decimal TotalLabourCostAmount,
    string TotalLabourCostCurrency,
    long CompletionTimeMinutes,
    bool HasValidCosts,
    string? CostWarning);

public sealed class CandidateCostCalculator
{
    private const decimal MinutesPerHour = 60m;
    private const string UnknownCurrency = "UNKNOWN";

    public CandidateCostMetrics CalculateMetrics(
        PlanningCandidate candidate,
        IReadOnlyDictionary<string, TechnicianReadModel> techniciansByIdLookup)
    {
        if (candidate.TaskPlacements.Count == 0)
            return NoTasksMetrics();

        var completionTime = CalculateCompletionTime(candidate.TaskPlacements);
        var (cost, hasValidCosts, warning) = CalculateLabourCost(candidate.TaskPlacements, techniciansByIdLookup);

        return new CandidateCostMetrics(
            cost,
            UnknownCurrency,
            completionTime,
            hasValidCosts,
            warning);
    }

    private static long CalculateCompletionTime(IReadOnlyList<PlanningTaskPlacement> placements)
    {
        if (placements.Count == 0)
            return 0;

        var minStart = placements.Min(p => p.Start);
        var maxEnd = placements.Max(p => p.End);
        var elapsed = maxEnd - minStart;
        return (long)elapsed.TotalMinutes;
    }

    private static (decimal cost, bool hasValidCosts, string? warning) CalculateLabourCost(
        IReadOnlyList<PlanningTaskPlacement> placements,
        IReadOnlyDictionary<string, TechnicianReadModel> techniciansByIdLookup)
    {
        decimal totalCost = 0m;
        var warnings = new List<string>();

        foreach (var placement in placements)
        {
            var taskDurationMinutes = (placement.End - placement.Start).TotalMinutes;
            var taskDurationHours = (decimal)taskDurationMinutes / MinutesPerHour;

            foreach (var technicianId in placement.TechnicianIds)
            {
                if (!techniciansByIdLookup.TryGetValue(technicianId, out var technician))
                {
                    warnings.Add($"Technician {technicianId} not found in lookup; labour cost is incomplete.");
                    continue;
                }

                if (technician.HourlyRate < 0)
                {
                    warnings.Add($"Technician {technicianId} has invalid hourly rate {technician.HourlyRate}; skipped.");
                    continue;
                }

                var taskCost = technician.HourlyRate * taskDurationHours;
                totalCost += taskCost;
            }
        }

        var hasValidCosts = warnings.Count == 0;
        var warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;

        return (Decimal.Round(totalCost, 2), hasValidCosts, warning);
    }

    private static CandidateCostMetrics NoTasksMetrics() =>
        new(
            TotalLabourCostAmount: 0m,
            TotalLabourCostCurrency: UnknownCurrency,
            CompletionTimeMinutes: 0,
            HasValidCosts: true,
            CostWarning: null);

    public static void ApplyRankingMetadata(List<PlanningCandidate> candidates)
    {
        if (candidates.Count == 0)
            return;

        var validCostCandidates = candidates
            .Where((c, i) => c.CostComparabilityWarning == null)
            .ToList();

        if (validCostCandidates.Count == 0)
            return;

        var minCost = validCostCandidates.Min(c => c.TotalLabourCostAmount);

        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var isLowest = candidate.CostComparabilityWarning == null &&
                           candidate.TotalLabourCostAmount == minCost;

            candidates[i] = candidate with { IsLowestCostCandidate = isLowest };
        }
    }
}
