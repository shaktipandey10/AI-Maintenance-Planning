using System.Text.Json.Serialization;

namespace MaintenancePlanner.Api.Contracts;

/// <summary>Natural-language maintenance request and selected planning inputs.</summary>
public sealed record CreatePlanningJobRequest
{
    /// <summary>The planner's natural-language maintenance request.</summary>
    [JsonRequired]
    public required string RequestText { get; init; }

    /// <summary>Stable identifiers for the assets included in the request.</summary>
    [JsonRequired]
    public required IReadOnlyList<string?> AssetIds { get; init; }

    /// <summary>The selected maintenance-window ID, if the user has specified one.</summary>
    public string? WindowId { get; init; }
}

/// <summary>Confirmation that a planning job was queued.</summary>
public sealed record PlanningJobAcceptedResponse(Guid JobId, string StatusUrl);

/// <summary>Confirmation that a replacement planning job was created.</summary>
public sealed record ReplanAcceptedResponse(Guid NewJobId, string StatusUrl);

/// <summary>Persisted status and timestamps for a planning job.</summary>
public sealed record PlanningJobStatusResponse(
    Guid JobId,
    string Status,
    int AttemptCount,
    int MaxAttempts,
    string CurrentStep,
    string? ResultPayload,
    string? ClarificationPayload,
    string? FailureCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorMessage);

/// <summary>Minimal revision history entry showing parent-child relationships.</summary>
public sealed record PlanningJobRevisionResponse(
    Guid Id,
    string Status,
    DateTimeOffset CreatedAtUtc,
    Guid? OriginalJobId);

/// <summary>Clarification answers submitted by the planner to resume planning.</summary>
public sealed record SubmitClarificationRequest
{
    /// <summary>The clarified maintenance window ID.</summary>
    public string? WindowId { get; init; }

    /// <summary>Additional context or confirmation from the planner.</summary>
    public string? AdditionalContext { get; init; }
}

/// <summary>Response for retrieving candidates from a planning job.</summary>
public sealed record PlanningJobCandidatesResponse(
    Guid JobId,
    string Status,
    string Outcome,
    IReadOnlyList<CandidateResponse> Candidates,
    InterpretationResponse? Interpretation);

/// <summary>A single candidate maintenance plan.</summary>
public sealed record CandidateResponse(
    string Rationale,
    IReadOnlyList<string> RequestedTaskIds,
    IReadOnlyList<string> DependencyTaskIds,
    IReadOnlyList<object> TaskPlacements,
    IReadOnlyList<string> EvidenceReferences,
    decimal TotalLabourCostAmount,
    string TotalLabourCostCurrency,
    long CompletionTimeMinutes,
    IReadOnlyList<DeferralReasonResponse> DeferralReasons,
    bool IsLowestCostCandidate,
    string? CostComparabilityWarning);

/// <summary>A deferred task with its reason.</summary>
public sealed record DeferralReasonResponse(
    string TaskId,
    string Reason);

/// <summary>Interpretation of the maintenance request.</summary>
public sealed record InterpretationResponse(
    string MaintenanceIntent,
    string Scope,
    IReadOnlyList<string> ProposedAssetIds,
    IReadOnlyList<string> ProposedTaskIds);

/// <summary>Complete draft details with selected candidate and reservations.</summary>
public sealed record DraftDetailResponse(
    Guid DraftId,
    Guid PlanningJobId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    CandidateResponse? SelectedCandidate,
    IReadOnlyList<ReservationResponse> Reservations,
    InterpretationResponse? Interpretation);

/// <summary>Spare part reservation for a draft.</summary>
public sealed record ReservationResponse(
    Guid ReservationId,
    string SparePartId,
    decimal QuantityReserved,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ConfirmedAtUtc);

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
