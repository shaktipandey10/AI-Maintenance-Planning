namespace MaintenancePlanner.Application.Approvals;

public interface IDraftApprovalService
{
    Task<DraftApprovalResult> ApproveDraftAsync(
        Guid planningJobId,
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
