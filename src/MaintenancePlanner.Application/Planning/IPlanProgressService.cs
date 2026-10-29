using MaintenancePlanner.Domain.Entities;

namespace MaintenancePlanner.Application.Planning;

public interface IPlanProgressService
{
    Task<PlanProgressResult> MarkTaskCompleteAsync(
        Guid draftId,
        string sparePartId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves one task of an approved draft forward: NotStarted → InProgress → Completed. Skipped, backward and
    /// repeated transitions are rejected without changing anything.
    /// </summary>
    Task<TaskStatusUpdateResult> UpdateTaskStatusAsync(
        Guid draftId,
        string taskId,
        TaskProgressStatus status,
        CancellationToken cancellationToken);

    Task<PlanProgress> GetPlanProgressAsync(
        Guid draftId,
        CancellationToken cancellationToken);
}

public sealed record PlanProgressResult(
    bool Succeeded,
    string? FailureReason = null);

public sealed record TaskStatusUpdateResult(
    bool Succeeded,
    string? FailureCode = null,
    string? FailureReason = null)
{
    public const string DraftNotFound = "DRAFT_NOT_FOUND";
    public const string DraftNotApproved = "DRAFT_NOT_APPROVED";
    public const string TaskNotInPlan = "TASK_NOT_IN_PLAN";
    public const string InvalidTransition = "INVALID_TASK_TRANSITION";
}

public sealed record PlanProgress(
    Guid DraftId,
    decimal TotalRequired,
    decimal CompletedQuantity,
    decimal RemainingQuantity,
    IReadOnlyList<TaskProgress> Tasks)
{
    /// <summary>The status of every task placed in the draft's approved snapshot, in plan order.</summary>
    public IReadOnlyList<TaskStatusProgress> TaskStatuses { get; init; } = [];
}

public sealed record TaskStatusProgress(
    string TaskId,
    TaskProgressStatus Status,
    DateTimeOffset? UpdatedAtUtc);

public sealed record TaskProgress(
    string SparePartId,
    decimal RequiredQuantity,
    decimal CompletedQuantity,
    decimal RemainingQuantity);
