using System.Text.Json;
using MaintenancePlanner.Application.History;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Planning;

public sealed class PlanProgressService(
    MaintenancePlannerDbContext dbContext,
    BusinessActor? actor = null) : IPlanProgressService
{
    public async Task<PlanProgressResult> MarkTaskCompleteAsync(
        Guid draftId,
        string sparePartId,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);

        if (draft is null)
            return new PlanProgressResult(false, "Draft not found");

        if (draft.Status != DraftStatus.Approved)
            return new PlanProgressResult(false, "Only approved drafts can have tasks marked complete");

        var reservations = await dbContext.Reservations
            .Where(r => r.DraftId == draftId && r.SparePartId == sparePartId)
            .ToListAsync(cancellationToken);

        if (reservations.Count == 0)
            return new PlanProgressResult(false, $"No reservation found for spare part {sparePartId} in this draft");

        // Mark one reservation as completed
        var incompleteReservation = reservations.FirstOrDefault(r => !r.CompletedAtUtc.HasValue);
        if (incompleteReservation is null)
            return new PlanProgressResult(false, "All tasks for this spare part are already completed");

        incompleteReservation.CompletedAtUtc = DateTimeOffset.UtcNow;
        dbContext.RecordBusinessAction(
            actor,
            BusinessActions.PartConsumptionRecorded,
            draft.PlanningJobId,
            draftId,
            details: $"Marked {incompleteReservation.QuantityReserved} {sparePartId} as consumed.");
        await dbContext.SaveChangesAsync(cancellationToken);

        return new PlanProgressResult(true);
    }

    public async Task<PlanProgress> GetPlanProgressAsync(
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);

        if (draft is null)
            throw new InvalidOperationException("Draft not found");

        var reservations = await dbContext.Reservations
            .Where(r => r.DraftId == draftId && r.Status == ReservationStatus.Confirmed)
            .GroupBy(r => r.SparePartId)
            .Select(g => new
            {
                SparePartId = g.Key,
                Total = g.Sum(r => r.QuantityReserved),
                Completed = g.Sum(r => r.CompletedAtUtc.HasValue ? r.QuantityReserved : 0)
            })
            .ToListAsync(cancellationToken);

        var totalRequired = reservations.Sum(r => r.Total);
        var completedQuantity = reservations.Sum(r => r.Completed);
        var remainingQuantity = totalRequired - completedQuantity;

        var tasks = reservations.Select(r => new TaskProgress(
            r.SparePartId,
            r.Total,
            r.Completed,
            r.Total - r.Completed
        )).ToList();

        var recordedStatuses = await dbContext.DraftTaskProgress
            .AsNoTracking()
            .Where(p => p.DraftId == draftId)
            .ToListAsync(cancellationToken);
        var taskStatuses = ReadPlacedTaskIds(draft.ApprovedCandidatePayload)
            .Select(taskId => recordedStatuses.FirstOrDefault(p => p.TaskId == taskId) is { } recorded
                ? new TaskStatusProgress(taskId, recorded.Status, recorded.UpdatedAtUtc)
                : new TaskStatusProgress(taskId, TaskProgressStatus.NotStarted, null))
            .ToList();

        return new PlanProgress(draftId, totalRequired, completedQuantity, remainingQuantity, tasks)
        {
            TaskStatuses = taskStatuses
        };
    }

    public async Task<TaskStatusUpdateResult> UpdateTaskStatusAsync(
        Guid draftId,
        string taskId,
        TaskProgressStatus status,
        CancellationToken cancellationToken)
    {
        // As in cancellation, decide from committed rows only: the transaction begins (taking SQLite's write lock)
        // before the draft is read, so a concurrent supersede or cancel cannot commit between the checks and the write.
        if (dbContext.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Task progress cannot be updated while the DbContext has unsaved changes.");
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
        if (draft is null)
            return Failure(TaskStatusUpdateResult.DraftNotFound, "Draft not found");

        if (draft.Status != DraftStatus.Approved)
            return Failure(TaskStatusUpdateResult.DraftNotApproved, "Only approved drafts can have task progress updated");

        var placedTaskIds = ReadPlacedTaskIds(draft.ApprovedCandidatePayload);
        var placedTaskId = placedTaskIds
            .FirstOrDefault(placed => string.Equals(placed, taskId, StringComparison.OrdinalIgnoreCase));
        if (placedTaskId is null)
            return Failure(TaskStatusUpdateResult.TaskNotInPlan, $"Task {taskId} is not part of this approved plan");

        var progress = await dbContext.DraftTaskProgress
            .FirstOrDefaultAsync(p => p.DraftId == draftId && p.TaskId == placedTaskId, cancellationToken);
        var current = progress?.Status ?? TaskProgressStatus.NotStarted;
        if (!IsAllowedTransition(current, status))
            return Failure(TaskStatusUpdateResult.InvalidTransition,
                $"Task {placedTaskId} cannot move from {current} to {status}");

        if (progress is null)
        {
            progress = new DraftTaskProgress { Id = Guid.NewGuid(), DraftId = draftId, TaskId = placedTaskId };
            await dbContext.DraftTaskProgress.AddAsync(progress, cancellationToken);
        }

        progress.Status = status;
        progress.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (status == TaskProgressStatus.Completed)
            await RecordPartConsumptionAsync(draftId, placedTaskIds, placedTaskId, progress.UpdatedAtUtc, cancellationToken);

        dbContext.RecordBusinessAction(
            actor,
            BusinessActions.TaskProgressUpdated,
            draft.PlanningJobId,
            draftId,
            placedTaskId,
            $"{current} -> {status}");

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TaskStatusUpdateResult(true);
    }

    /// <summary>
    /// FR08: a reserved part becomes consumed when the work that needs it is completed, recorded by stamping its
    /// reservation's CompletedAtUtc (stock quantities are unchanged; cancellation and replacement then keep the
    /// reservation). Approval holds one reservation per part for all of a draft's tasks needing that part, and the
    /// stamp covers the whole reservation, so it is set only once every placed task needing the part is Completed.
    /// A reservation already stamped (also by mark-task-complete) is left unchanged, so nothing is consumed twice.
    /// </summary>
    private async Task RecordPartConsumptionAsync(
        Guid draftId,
        IReadOnlyList<string> placedTaskIds,
        string completedTaskId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        var placedTasks = await dbContext.MaintenanceTaskDefinitions
            .AsNoTracking()
            .Where(task => placedTaskIds.Contains(task.Id))
            .ToListAsync(cancellationToken);
        var partId = placedTasks
            .FirstOrDefault(task => string.Equals(task.Id, completedTaskId, StringComparison.OrdinalIgnoreCase) &&
                                    task.RequiredPartQuantity > 0)
            ?.RequiredPartId;
        if (string.IsNullOrWhiteSpace(partId))
            return;

        var completedTaskIds = (await dbContext.DraftTaskProgress
                .AsNoTracking()
                .Where(p => p.DraftId == draftId && p.Status == TaskProgressStatus.Completed)
                .Select(p => p.TaskId)
                .ToListAsync(cancellationToken))
            .Append(completedTaskId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allWorkNeedingPartCompleted = placedTasks
            .Where(task => task.RequiredPartQuantity > 0 &&
                           string.Equals(task.RequiredPartId, partId, StringComparison.OrdinalIgnoreCase))
            .All(task => completedTaskIds.Contains(task.Id));
        if (!allWorkNeedingPartCompleted)
            return;

        var unconsumedReservations = await dbContext.Reservations
            .Where(r => r.DraftId == draftId &&
                        r.SparePartId == partId &&
                        r.Status == ReservationStatus.Confirmed &&
                        r.CompletedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var reservation in unconsumedReservations)
            reservation.CompletedAtUtc = completedAtUtc;
    }

    private static bool IsAllowedTransition(TaskProgressStatus current, TaskProgressStatus next) =>
        (current, next) is (TaskProgressStatus.NotStarted, TaskProgressStatus.InProgress)
            or (TaskProgressStatus.InProgress, TaskProgressStatus.Completed);

    private static TaskStatusUpdateResult Failure(string code, string reason) => new(false, code, reason);

    /// <summary>The distinct task IDs placed in an approved candidate snapshot, in plan order; none if unreadable.</summary>
    private static IReadOnlyList<string> ReadPlacedTaskIds(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot))
            return [];

        try
        {
            using var document = JsonDocument.Parse(snapshot);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("taskPlacements", out var placements) ||
                placements.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return placements.EnumerateArray()
                .Select(placement => placement.ValueKind == JsonValueKind.Object &&
                                     placement.TryGetProperty("taskId", out var id) &&
                                     id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
