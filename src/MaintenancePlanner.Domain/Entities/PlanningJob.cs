namespace MaintenancePlanner.Domain.Entities;

public sealed class PlanningJob
{
    public Guid Id { get; set; }
    public Guid? OperationId { get; set; }
    public Guid? OriginalJobId { get; set; }
    public string RequestPayload { get; set; } = string.Empty;
    public string? RequestFingerprint { get; set; }
    public int ConcurrencyVersion { get; set; }
    public PlanningJobStatus Status { get; set; } = PlanningJobStatus.Queued;
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 2;
    public string CurrentStep { get; set; } = "Queued";
    public string? CheckpointPayload { get; set; }
    public string? ResultPayload { get; set; }
    public string? ClarificationPayload { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public Guid? WorkerLeaseToken { get; set; }
    public long? LeaseExpiresAtUnixMilliseconds { get; set; }
    public string? ErrorMessage { get; set; }
}
