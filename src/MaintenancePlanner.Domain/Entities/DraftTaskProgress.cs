namespace MaintenancePlanner.Domain.Entities;

/// <summary>
/// The simulated progress of one task placed in an approved draft. A placed task without a row has not started.
/// </summary>
public sealed class DraftTaskProgress
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public Draft? Draft { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public TaskProgressStatus Status { get; set; } = TaskProgressStatus.NotStarted;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Task progress moves forward only: NotStarted → InProgress → Completed.</summary>
public enum TaskProgressStatus
{
    NotStarted,
    InProgress,
    Completed
}
