namespace MaintenancePlanner.Application.Edits;

/// <summary>
/// FR05: turns the task edits of an approved draft into a revised plan that is reviewed and approved through the
/// normal workflow. The revision is a replacement planning job of the draft's job whose single candidate is the
/// approved plan with the edited placements; approving it supersedes the draft exactly as a replanned V2 does.
/// </summary>
public interface IEditRevisionService
{
    /// <summary>
    /// Validates every edit and the whole edited plan against current data, locks and task progress, and creates the
    /// revision only if nothing is violated. A repeated request with the same operation ID returns the same revision.
    /// </summary>
    Task<EditRevisionResult> CreateRevisionAsync(
        Guid draftId,
        Guid operationId,
        CancellationToken cancellationToken);
}

public sealed record EditRevisionResult(
    bool Succeeded,
    Guid? PlanningJobId = null,
    string? FailureCode = null,
    string? FailureReason = null,
    IReadOnlyList<ValidationViolation>? Violations = null)
{
    public const string DraftNotFound = "DRAFT_NOT_FOUND";
    public const string DraftNotApproved = "DRAFT_NOT_APPROVED";
    public const string NoEdits = "NO_TASK_EDITS";
    public const string EditsInvalid = "TASK_EDITS_INVALID";
    public const string SnapshotUnavailable = "APPROVED_SNAPSHOT_UNAVAILABLE";
    public const string OperationIdReused = "OPERATION_ID_REUSED";
}
