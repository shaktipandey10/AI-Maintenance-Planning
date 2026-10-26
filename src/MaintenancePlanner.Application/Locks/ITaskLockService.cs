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
}
