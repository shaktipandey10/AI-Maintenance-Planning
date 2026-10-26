namespace MaintenancePlanner.Domain.Entities;

/// <summary>
/// Represents a technician assignment locked to a specific task within an approved draft.
/// Locks prevent reassignment during replanning unless the locked technician becomes unavailable.
/// </summary>
public sealed class LockedTaskAssignment
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public Draft? Draft { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string LockedTechnicianId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
