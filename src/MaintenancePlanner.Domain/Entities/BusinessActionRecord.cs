namespace MaintenancePlanner.Domain.Entities;

/// <summary>
/// One business action on a plan (FR09): who performed it, when, and on which planning job, draft and task. Written in
/// the same save as the action itself and never changed afterwards.
/// </summary>
public sealed class BusinessActionRecord
{
    public Guid Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;
    public DateTimeOffset PerformedAtUtc { get; set; }
    public Guid? PlanningJobId { get; set; }
    public Guid? DraftId { get; set; }
    public string? TaskId { get; set; }
    public string? Details { get; set; }
}
