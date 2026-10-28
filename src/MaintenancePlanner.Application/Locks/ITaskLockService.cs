namespace MaintenancePlanner.Application.Locks;

/// <summary>
/// Service for retrieving and managing task locks from approved drafts.
/// Locks represent technician assignments frozen at approval time and used to constrain subsequent replanning.
/// </summary>
public interface ITaskLockService
{
    /// <summary>
    /// Retrieve locks from an approved draft.
    /// Returns a dictionary mapping taskId to lockedTechnicianId.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetLocksAsync(
        Guid draftId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retrieve locks from the approved draft of a planning job.
    /// If the job has no approved draft, returns empty dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetLocksForJobAsync(
        Guid planningJobId,
        CancellationToken cancellationToken);

    /// <summary>
    /// FR05: locks a task of an approved draft to the technician the user chooses, replacing its current lock.
    /// Replanning and edit revisions then keep that technician on the task. A lock that cannot be honoured with
    /// current data (unknown, unqualified or unavailable technician) is rejected with the specific conflict and nothing
    /// is changed. Locks of superseded or cancelled drafts are history and cannot be changed, and neither can the lock
    /// of a task in progress or completed (FR08).
    /// </summary>
    Task<TaskLockChangeResult> LockAsync(
        Guid draftId,
        string taskId,
        string technicianId,
        CancellationToken cancellationToken);

    /// <summary>
    /// FR05: removes a task's lock from an approved draft, so replanning may reassign the task. As with
    /// <see cref="LockAsync"/>, the lock of a task in progress or completed cannot be changed (FR08).
    /// </summary>
    Task<TaskLockChangeResult> UnlockAsync(
        Guid draftId,
        string taskId,
        CancellationToken cancellationToken);
}

public sealed record TaskLockChangeResult(
    bool Succeeded,
    string? FailureCode = null,
    string? FailureReason = null)
{
    public const string DraftNotFound = "DRAFT_NOT_FOUND";
    public const string DraftNotApproved = "DRAFT_NOT_APPROVED";
    public const string TaskNotInPlan = "TASK_NOT_IN_PLAN";
    public const string TaskInProgress = "TASK_IN_PROGRESS";
    public const string TaskCompleted = "TASK_ALREADY_COMPLETED";
    public const string TechnicianNotFound = "LOCKED_TECHNICIAN_NOT_FOUND";
    public const string TechnicianUnqualified = "LOCKED_TECHNICIAN_UNQUALIFIED";
    public const string TechnicianUnavailable = "LOCKED_TECHNICIAN_UNAVAILABLE";
}
