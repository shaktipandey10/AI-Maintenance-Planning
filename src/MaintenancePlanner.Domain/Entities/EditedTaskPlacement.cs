namespace MaintenancePlanner.Domain.Entities;

/// <summary>
/// Represents a pending edit to a task's timing or technician assignment within a draft.
/// Stores original and proposed values for validation and comparison without modifying the approved draft.
/// </summary>
public sealed class EditedTaskPlacement
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public Draft? Draft { get; set; }
    public string TaskId { get; set; } = string.Empty;

    // Original values from the approved draft's selected candidate
    public DateTimeOffset OriginalStart { get; set; }
    public DateTimeOffset OriginalEnd { get; set; }
    public string OriginalTechnicianIds { get; set; } = string.Empty; // JSON-serialized array

    // Proposed new values
    public DateTimeOffset NewStart { get; set; }
    public DateTimeOffset NewEnd { get; set; }
    public string NewTechnicianIds { get; set; } = string.Empty; // JSON-serialized array

    // Validation state
    public string ValidationStatus { get; set; } = "pending"; // pending, valid, invalid
    public string? ValidationViolations { get; set; } // JSON-serialized array of FeasibilityViolation

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}
