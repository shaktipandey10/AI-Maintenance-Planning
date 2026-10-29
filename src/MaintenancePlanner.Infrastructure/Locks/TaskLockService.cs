using System.Text.Json;
using MaintenancePlanner.Application.History;
using MaintenancePlanner.Application.Locks;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Locks;

public sealed class TaskLockService(
    MaintenancePlannerDbContext dbContext,
    BusinessActor? actor = null) : ITaskLockService
{
    public async Task<IReadOnlyDictionary<string, string>> GetLocksAsync(
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var locks = await dbContext.LockedTaskAssignments
            .Where(l => l.DraftId == draftId)
            .ToDictionaryAsync(
                l => l.TaskId,
                l => l.LockedTechnicianId,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);

        return locks;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetLocksForJobAsync(
        Guid planningJobId,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts
            .Where(d => d.PlanningJobId == planningJobId && d.Status == DraftStatus.Approved)
            .FirstOrDefaultAsync(cancellationToken);

        if (draft is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return await GetLocksAsync(draft.Id, cancellationToken);
    }

    public async Task<TaskLockChangeResult> LockAsync(
        Guid draftId,
        string taskId,
        string technicianId,
        CancellationToken cancellationToken)
    {
        // As in approval and cancellation, decide from committed rows inside the write transaction, so a draft
        // superseded or cancelled concurrently cannot have its (historical) locks changed
        if (dbContext.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Locks cannot be changed while the DbContext has unsaved changes.");
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var (draft, placedTaskId, failure) = await FindPlacedTaskAsync(draftId, taskId, cancellationToken);
        if (failure is not null)
            return failure;

        var task = await dbContext.MaintenanceTaskDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == placedTaskId, cancellationToken);
        var technician = await dbContext.Technicians.AsNoTracking()
            .Include(t => t.TechnicalSkills)
            .Include(t => t.TechnicianAvailabilities)
            .FirstOrDefaultAsync(t => t.Id == technicianId, cancellationToken);
        if (technician is null)
            return Failure(TaskLockChangeResult.TechnicianNotFound, $"Technician {technicianId} does not exist.");

        // The same checks the scheduler applies to locks before replanning (DeterministicPlanningScheduler), so a lock
        // accepted here is not rejected as soon as it is used
        var requiredSkills = (task?.RequiredSkills ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var missingSkills = requiredSkills
            .Where(skill => !technician.TechnicalSkills.Any(s => string.Equals(s.SkillCode, skill, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (task is null || missingSkills.Length > 0)
        {
            return Failure(TaskLockChangeResult.TechnicianUnqualified,
                $"Task {placedTaskId} cannot be locked to technician {technician.Id}, who lacks required skills: {string.Join(", ", missingSkills)}.");
        }

        var window = await ReadWindowAsync(draft!.PlanningJob!.RequestPayload, cancellationToken);
        if (window is null ||
            !technician.TechnicianAvailabilities.Any(a => a.AvailableFrom <= window.Start && a.AvailableTo >= window.End))
        {
            return Failure(TaskLockChangeResult.TechnicianUnavailable,
                $"Task {placedTaskId} cannot be locked to technician {technician.Id}, who is unavailable during maintenance window {window?.Id ?? "(unknown)"}.");
        }

        var existing = await dbContext.LockedTaskAssignments
            .FirstOrDefaultAsync(l => l.DraftId == draftId && l.TaskId == placedTaskId, cancellationToken);
        if (existing is not null && string.Equals(existing.LockedTechnicianId, technician.Id, StringComparison.OrdinalIgnoreCase))
            return new TaskLockChangeResult(true); // Idempotent: already locked to this technician

        var previous = existing?.LockedTechnicianId;
        if (existing is null)
        {
            await dbContext.LockedTaskAssignments.AddAsync(new LockedTaskAssignment
            {
                Id = Guid.NewGuid(),
                DraftId = draftId,
                TaskId = placedTaskId!,
                LockedTechnicianId = technician.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
        else
        {
            existing.LockedTechnicianId = technician.Id;
            existing.CreatedAtUtc = DateTimeOffset.UtcNow;
        }

        dbContext.RecordBusinessAction(
            actor,
            BusinessActions.TaskLocked,
            draft.PlanningJobId,
            draftId,
            placedTaskId,
            previous is null ? $"Locked to {technician.Id}." : $"Locked to {technician.Id} (was {previous}).");
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TaskLockChangeResult(true);
    }

    public async Task<TaskLockChangeResult> UnlockAsync(
        Guid draftId,
        string taskId,
        CancellationToken cancellationToken)
    {
        if (dbContext.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Locks cannot be changed while the DbContext has unsaved changes.");
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var (draft, placedTaskId, failure) = await FindPlacedTaskAsync(draftId, taskId, cancellationToken);
        if (failure is not null)
            return failure;

        var existing = await dbContext.LockedTaskAssignments
            .FirstOrDefaultAsync(l => l.DraftId == draftId && l.TaskId == placedTaskId, cancellationToken);
        if (existing is null)
            return new TaskLockChangeResult(true); // Idempotent: not locked

        dbContext.LockedTaskAssignments.Remove(existing);
        dbContext.RecordBusinessAction(
            actor,
            BusinessActions.TaskUnlocked,
            draft!.PlanningJobId,
            draftId,
            placedTaskId,
            $"Unlocked (was locked to {existing.LockedTechnicianId}).");
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TaskLockChangeResult(true);
    }

    private async Task<(Draft? Draft, string? PlacedTaskId, TaskLockChangeResult? Failure)> FindPlacedTaskAsync(
        Guid draftId,
        string taskId,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts.AsNoTracking()
            .Include(d => d.PlanningJob)
            .FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
        if (draft is null)
            return (null, null, Failure(TaskLockChangeResult.DraftNotFound, "Draft not found."));

        if (draft.Status != DraftStatus.Approved)
        {
            return (draft, null, Failure(TaskLockChangeResult.DraftNotApproved,
                $"Draft is {draft.Status}; only the locks of an approved draft can be changed."));
        }

        var placedTaskId = ApprovedCandidateSnapshot.ReadPlacements(draft.ApprovedCandidatePayload)?
            .Select(placement => placement.TaskId)
            .FirstOrDefault(placed => string.Equals(placed, taskId, StringComparison.OrdinalIgnoreCase));
        if (placedTaskId is null)
            return (draft, null, Failure(TaskLockChangeResult.TaskNotInPlan, $"Task {taskId} is not part of this approved plan."));

        // FR08: started work keeps its approved assignment, so its lock is not changed. Read inside the caller's write
        // transaction, so a task started concurrently cannot have its lock changed afterwards.
        var progress = await dbContext.DraftTaskProgress.AsNoTracking()
            .Where(p => p.DraftId == draftId && p.TaskId == placedTaskId)
            .Select(p => (TaskProgressStatus?)p.Status)
            .FirstOrDefaultAsync(cancellationToken);
        return progress switch
        {
            TaskProgressStatus.InProgress => (draft, null, Failure(TaskLockChangeResult.TaskInProgress,
                $"Task {placedTaskId} is in progress; its lock cannot be changed while the work keeps its approved assignment.")),
            TaskProgressStatus.Completed => (draft, null, Failure(TaskLockChangeResult.TaskCompleted,
                $"Task {placedTaskId} is completed; its lock cannot be changed because the task is not scheduled again.")),
            _ => (draft, placedTaskId, null)
        };
    }

    private async Task<MaintenanceWindow?> ReadWindowAsync(string requestPayload, CancellationToken cancellationToken)
    {
        string? windowId = null;
        try
        {
            using var document = JsonDocument.Parse(requestPayload);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("windowId", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                windowId = value.GetString();
            }
        }
        catch (JsonException)
        {
            // Reported by the caller as an unknown window
        }

        return windowId is null
            ? null
            : await dbContext.MaintenanceWindows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == windowId, cancellationToken);
    }

    private static TaskLockChangeResult Failure(string code, string reason) => new(false, code, reason);
}
