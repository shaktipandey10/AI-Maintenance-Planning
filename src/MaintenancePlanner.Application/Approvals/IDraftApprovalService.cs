namespace MaintenancePlanner.Application.Approvals;

public interface IDraftApprovalService
{
    /// <summary>
    /// Approves the candidate at <paramref name="candidateIndex"/> of a succeeded planning job. When the job
    /// is a replacement (it has an original job), the original approved draft is superseded and its unused
    /// reservations are released in the same transaction. A null index is accepted only when the job has
    /// exactly one candidate; the first candidate is never chosen by default.
    /// </summary>
    Task<DraftApprovalResult> ApproveDraftAsync(
        Guid planningJobId,
        int? candidateIndex,
        CancellationToken cancellationToken);

    Task<DraftApprovalResult> ApproveDraftAsync(
        Guid planningJobId,
        CancellationToken cancellationToken);

    /// <summary>Same as <see cref="ApproveDraftAsync(Guid, int?, CancellationToken)"/>, but rejects jobs that are not replacements.</summary>
    Task<DraftApprovalResult> ApproveDraftReplacementAsync(
        Guid planningJobId,
        int? candidateIndex,
        CancellationToken cancellationToken);

    Task<DraftApprovalResult> ApproveDraftReplacementAsync(
        Guid planningJobId,
        CancellationToken cancellationToken);

    Task<CancellationResult> CancelDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken);

    Task<ApprovedDraftInfo?> GetApprovedDraftForJobAsync(
        Guid planningJobId,
        CancellationToken cancellationToken);
}

public sealed record ApprovedDraftInfo(
    Guid Id,
    Guid PlanningJobId,
    string Status);

public sealed record ApprovalConflictData(
    string ResourceType,
    string Reason,
    string? ResourceId = null);

public sealed record DraftApprovalResult(
    bool Succeeded,
    Guid? DraftId = null,
    string? FailureReason = null,
    ApprovalConflictData? ConflictData = null);

public sealed record CancellationResult(
    bool Succeeded,
    string? FailureReason = null,
    ApprovalConflictData? ConflictData = null);
