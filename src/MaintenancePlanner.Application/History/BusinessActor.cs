namespace MaintenancePlanner.Application.History;

/// <summary>
/// Who performs the business actions of the current request: the role chosen in the application's role selector,
/// sent in the X-Actor-Role header. Roles are self-declared, not authenticated; a missing or unknown role is
/// recorded as "unspecified".
/// </summary>
public sealed class BusinessActor
{
    public const string HeaderName = "X-Actor-Role";
    public const string Unspecified = "unspecified";
    public static readonly IReadOnlyList<string> Roles = ["planner", "reviewer", "technician"];

    public string Role { get; private set; } = Unspecified;

    public void SetRole(string? role) =>
        Role = Roles.FirstOrDefault(known => string.Equals(known, role?.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? Unspecified;
}

/// <summary>The recorded business actions.</summary>
public static class BusinessActions
{
    public const string Approved = "Approved";
    public const string Cancelled = "Cancelled";
    public const string ReplanRequested = "ReplanRequested";
    public const string TaskProgressUpdated = "TaskProgressUpdated";
    public const string PartConsumptionRecorded = "PartConsumptionRecorded";
    public const string TaskEditSubmitted = "TaskEditSubmitted";
    public const string TaskEditDeleted = "TaskEditDeleted";
    public const string TaskEditsCleared = "TaskEditsCleared";
    public const string TaskLocked = "TaskLocked";
    public const string TaskUnlocked = "TaskUnlocked";
    public const string RevisionCreatedFromEdits = "RevisionCreatedFromEdits";
}
