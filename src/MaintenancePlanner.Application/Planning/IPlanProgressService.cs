namespace MaintenancePlanner.Application.Planning;

public interface IPlanProgressService
{
    Task<PlanProgressResult> MarkTaskCompleteAsync(
        Guid draftId,
        string sparePartId,
        CancellationToken cancellationToken);

    Task<PlanProgress> GetPlanProgressAsync(
        Guid draftId,
        CancellationToken cancellationToken);
}

public sealed record PlanProgressResult(
    bool Succeeded,
    string? FailureReason = null);

public sealed record PlanProgress(
    Guid DraftId,
    decimal TotalRequired,
    decimal CompletedQuantity,
    decimal RemainingQuantity,
    IReadOnlyList<TaskProgress> Tasks);

public sealed record TaskProgress(
    string SparePartId,
    decimal RequiredQuantity,
    decimal CompletedQuantity,
    decimal RemainingQuantity);
