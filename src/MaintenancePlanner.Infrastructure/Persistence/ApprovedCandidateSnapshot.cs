using System.Text.Json;
using MaintenancePlanner.Application.Planning;

namespace MaintenancePlanner.Infrastructure.Persistence;

/// <summary>Reads the task placements of a draft's approved candidate snapshot (Draft.ApprovedCandidatePayload).</summary>
public static class ApprovedCandidateSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The placements in plan order, or null when the snapshot is missing or unreadable.</summary>
    public static IReadOnlyList<PlanningTaskPlacement>? ReadPlacements(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot))
            return null;

        try
        {
            var placements = JsonSerializer.Deserialize<Snapshot>(snapshot, JsonOptions)?.TaskPlacements;
            if (placements is null || placements.Any(placement =>
                    placement?.TaskId is null || placement.TechnicianIds is null))
            {
                return null;
            }

            return placements
                .Select(placement => new PlanningTaskPlacement(
                    placement!.TaskId!, placement.Start, placement.End, placement.TechnicianIds!))
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Snapshot(IReadOnlyList<Placement?>? TaskPlacements);

    private sealed record Placement(
        string? TaskId,
        DateTimeOffset Start,
        DateTimeOffset End,
        IReadOnlyList<string>? TechnicianIds);
}
