namespace MaintenancePlanner.Application.Edits;

public interface ITaskEditorService
{
    /// <summary>Submit edits for a draft, capturing original values from the approved candidate.</summary>
    /// <remarks>
    /// Verifies draft exists and is in Approved status.
    /// Recovers original timing and technicians from the planning job's first candidate.
    /// Creates EditedTaskPlacement records for each submitted edit.
    /// Edits are marked as pending and are not validated until Stage 2B.
    /// Throws 404 if draft not found, 409 if draft not approved.
    /// </remarks>
    Task<TaskEditsResponse> SubmitEditsAsync(
        Guid draftId,
        TaskEditsSubmissionRequest request,
        CancellationToken cancellationToken);

    /// <summary>Retrieve all current edits for a draft.</summary>
    Task<IReadOnlyList<EditedTaskPlacementResponse>> GetEditsAsync(
        Guid draftId,
        CancellationToken cancellationToken);

    /// <summary>Delete the edit for a single task within a draft.</summary>
    Task<bool> DeleteEditAsync(
        Guid draftId,
        string taskId,
        CancellationToken cancellationToken);

    /// <summary>Delete all edits for a draft.</summary>
    Task<bool> ClearEditsAsync(
        Guid draftId,
        CancellationToken cancellationToken);
}
