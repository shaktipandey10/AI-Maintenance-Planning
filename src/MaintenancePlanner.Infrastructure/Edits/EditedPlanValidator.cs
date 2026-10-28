using System.Text.Json;
using MaintenancePlanner.Application.Edits;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Edits;

/// <summary>The plan an approved draft's edits would produce, and every way it violates current constraints.</summary>
public sealed record EditedPlanCheck(
    IReadOnlyList<PlanningTaskPlacement> RevisedPlacements,
    IReadOnlyList<ValidationViolation> Violations);

/// <summary>
/// FR05: checks the edits of an approved draft together, as the plan they would produce: completed tasks are left out
/// (they are not scheduled again), tasks in progress keep their approved placement and a locked task must keep its
/// locked technician (FR08), and the whole plan goes through the planning validator (order, overlaps, window, crews,
/// stock). Used by both edit validation and revision creation, so both report the same violations.
/// </summary>
public sealed class EditedPlanValidator(
    MaintenancePlannerDbContext dbContext,
    PlanningCandidateValidator candidateValidator)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Null when the draft has no readable approved plan to apply the edits to.</summary>
    public async Task<EditedPlanCheck?> CheckAsync(
        Draft draft,
        string requestPayload,
        IReadOnlyList<EditedTaskPlacement> edits,
        CancellationToken cancellationToken)
    {
        var approvedPlacements = ApprovedCandidateSnapshot.ReadPlacements(draft.ApprovedCandidatePayload);
        if (approvedPlacements is null)
            return null;

        var violations = new List<ValidationViolation>();
        var progress = await dbContext.DraftTaskProgress.AsNoTracking()
            .Where(p => p.DraftId == draft.Id)
            .ToListAsync(cancellationToken);
        var completed = progress.Where(p => p.Status == TaskProgressStatus.Completed).Select(p => p.TaskId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inProgress = progress.Where(p => p.Status == TaskProgressStatus.InProgress).Select(p => p.TaskId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var locks = await dbContext.LockedTaskAssignments.AsNoTracking()
            .Where(l => l.DraftId == draft.Id)
            .ToDictionaryAsync(l => l.TaskId, l => l.LockedTechnicianId, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var editsByTask = new Dictionary<string, EditedTaskPlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            var technicianIds = ReadTechnicianIds(edit.NewTechnicianIds);
            if (!approvedPlacements.Any(p => string.Equals(p.TaskId, edit.TaskId, StringComparison.OrdinalIgnoreCase)))
                violations.Add(new(edit.TaskId, "TASK_NOT_IN_PLAN", $"Task {edit.TaskId} is not part of the approved plan."));
            else if (completed.Contains(edit.TaskId))
                violations.Add(new(edit.TaskId, "TASK_ALREADY_COMPLETED", $"Task {edit.TaskId} is completed and is not scheduled again."));
            else if (inProgress.Contains(edit.TaskId))
                violations.Add(new(edit.TaskId, "TASK_IN_PROGRESS", $"Task {edit.TaskId} is in progress and keeps its approved placement."));
            else if (locks.TryGetValue(edit.TaskId, out var lockedTechnicianId) &&
                     !technicianIds.Contains(lockedTechnicianId, StringComparer.OrdinalIgnoreCase))
            {
                violations.Add(new(edit.TaskId, "LOCKED_ASSIGNMENT_CHANGED",
                    $"Task {edit.TaskId} is locked to technician {lockedTechnicianId}; keep {lockedTechnicianId} in its crew or unlock the task first."));
            }

            editsByTask[edit.TaskId] = edit;
        }

        // The revised plan: completed work left out, edited tasks moved, everything else at its approved placement
        var revised = approvedPlacements
            .Where(placement => !completed.Contains(placement.TaskId))
            .Select(placement => editsByTask.TryGetValue(placement.TaskId, out var edit) && !inProgress.Contains(placement.TaskId)
                ? new PlanningTaskPlacement(
                    placement.TaskId,
                    edit.NewStart,
                    edit.NewEnd,
                    LockedFirst(ReadTechnicianIds(edit.NewTechnicianIds), locks.GetValueOrDefault(placement.TaskId)))
                : placement)
            .ToArray();
        if (revised.Length == 0)
        {
            violations.Add(new(string.Empty, "NO_REMAINING_WORK", "Every task of the approved plan is completed."));
        }
        else
        {
            var (assetIds, windowId) = ReadScope(requestPayload);
            var heldByDraft = (await dbContext.Reservations.AsNoTracking()
                    .Where(r => r.DraftId == draft.Id && r.Status == ReservationStatus.Confirmed && r.CompletedAtUtc == null)
                    .Select(r => new { r.SparePartId, r.QuantityReserved })
                    .ToListAsync(cancellationToken))
                .GroupBy(r => r.SparePartId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.QuantityReserved), StringComparer.OrdinalIgnoreCase);
            // The same whole-plan validator as planning and approval; the draft's unused reservations are released
            // when the revision is approved, so they count as available to it
            var validation = await candidateValidator.ValidateAsync(
                new PlanningCandidateValidationRequest(assetIds, windowId ?? string.Empty, new PlanningCandidate(revised, string.Empty, []))
                {
                    CompletedTaskIds = completed.ToArray(),
                    ReleasableReservedQuantities = heldByDraft
                },
                cancellationToken);
            violations.AddRange(validation.Violations.Select(v => new ValidationViolation(v.TaskId ?? string.Empty, v.Code, v.Message)));
        }

        return new EditedPlanCheck(revised, violations);
    }

    private static IReadOnlyList<string> LockedFirst(IReadOnlyList<string> technicianIds, string? lockedTechnicianId) =>
        lockedTechnicianId is null
            ? technicianIds
            : technicianIds.OrderBy(id => string.Equals(id, lockedTechnicianId, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();

    private static IReadOnlyList<string> ReadTechnicianIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static (IReadOnlyList<string> AssetIds, string? WindowId) ReadScope(string requestPayload)
    {
        try
        {
            using var document = JsonDocument.Parse(requestPayload);
            var root = document.RootElement;
            var assetIds = root.TryGetProperty("assetIds", out var assets) && assets.ValueKind == JsonValueKind.Array
                ? assets.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToArray()
                : [];
            var windowId = root.TryGetProperty("windowId", out var window) && window.ValueKind == JsonValueKind.String
                ? window.GetString()
                : null;
            return (assetIds, windowId);
        }
        catch (JsonException)
        {
            return ([], null);
        }
    }
}
