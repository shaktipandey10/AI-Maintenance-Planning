namespace MaintenancePlanner.Domain.Entities;

public sealed class Draft
{
    public Guid Id { get; set; }
    public Guid PlanningJobId { get; set; }
    public PlanningJob? PlanningJob { get; set; }
    public DraftStatus Status { get; set; } = DraftStatus.Pending;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAtUtc { get; set; }
    public string? ApprovedCandidatePayload { get; set; }
    public ICollection<Reservation> Reservations { get; set; } = new List<Reservation>();
    public ICollection<EditedTaskPlacement> EditedTaskPlacements { get; set; } = new List<EditedTaskPlacement>();
}

public enum DraftStatus
{
    Pending,
    Approved,
    Cancelled,
    Superseded
}
