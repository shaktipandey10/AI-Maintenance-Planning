namespace MaintenancePlanner.Application.Edits;

/// <summary>A single task edit within a draft.</summary>
public sealed record TaskEditRequest(
    string TaskId,
    DateTimeOffset NewStart,
    DateTimeOffset NewEnd,
    IReadOnlyList<string> NewTechnicianIds);

/// <summary>Request to submit edits for one or more tasks in a draft.</summary>
public sealed record TaskEditsSubmissionRequest(
    IReadOnlyList<TaskEditRequest> Edits);

/// <summary>A persisted task edit with original and proposed values.</summary>
public sealed record EditedTaskPlacementResponse(
    string TaskId,
    DateTimeOffset OriginalStart,
    DateTimeOffset OriginalEnd,
    IReadOnlyList<string> OriginalTechnicianIds,
    DateTimeOffset NewStart,
    DateTimeOffset NewEnd,
    IReadOnlyList<string> NewTechnicianIds,
    string ValidationStatus);

/// <summary>Response containing all edits for a draft.</summary>
public sealed record TaskEditsResponse(
    Guid DraftId,
    IReadOnlyList<EditedTaskPlacementResponse> EditedTasks);
