namespace MaintenancePlanner.Application.Planning;

public sealed record PlanningRequest(
    string RequestText,
    IReadOnlyList<string> AssetIds,
    string? MaintenanceWindowId);

public sealed record PlanningInterpretation(
    string MaintenanceIntent,
    string Scope,
    IReadOnlyList<string> ProposedAssetIds,
    IReadOnlyList<string> ProposedTaskIds,
    IReadOnlyList<string> MissingInformation,
    IReadOnlyList<string> Assumptions)
{
    public PlanningScopeResolution ScopeResolution { get; init; } =
        PlanningScopeResolution.Unresolved;
    public IReadOnlyList<PlanningRequestScopeMapping> ScopeMappings { get; init; } = [];
}

public enum PlanningScopeResolution
{
    Unresolved,
    Ambiguous,
    Unsupported,
    Resolved
}

public sealed record PlanningRequestScopeMapping(
    string TaskId,
    string AssetId,
    string? RequestExcerpt);

public enum PlanningEvidenceSourceKind
{
    Asset,
    MaintenanceTask,
    MaintenanceWindow,
    Technician,
    SparePart,
    Compatibility,
    MaintenanceHistory,
    MaintenanceManual
}

public sealed record PlanningEvidenceReference(
    PlanningEvidenceSourceKind SourceKind,
    string SourceId,
    string? DocumentId,
    string? Version,
    int? Page,
    string? Section,
    string? Excerpt);

public sealed record PlanningTaskPlacement(
    string TaskId,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<string> TechnicianIds);

public sealed record PlanningDeferralReason(
    string TaskId,
    string Reason);

public sealed record PlanningCandidate(
    IReadOnlyList<PlanningTaskPlacement> TaskPlacements,
    string Rationale,
    IReadOnlyList<PlanningEvidenceReference> EvidenceReferences)
{
    public IReadOnlyList<string> RequestedTaskIds { get; init; } = [];
    public IReadOnlyList<string> DependencyTaskIds { get; init; } = [];
    public IReadOnlyList<PlanningDeferralReason> DeferralReasons { get; init; } = [];
    public decimal TotalLabourCostAmount { get; init; }
    public string TotalLabourCostCurrency { get; init; } = "UNKNOWN";
    public long CompletionTimeMinutes { get; init; }
    public bool IsLowestCostCandidate { get; init; }
    public string? CostComparabilityWarning { get; init; }
}

public abstract record PlanningWorkflowResult(
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<PlanningEvidenceReference> EvidenceReferences)
{
    public required PlanningInterpretation Interpretation { get; init; }
}

public sealed record PlanningClarificationRequiredResult(
    IReadOnlyList<string> Questions,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<PlanningEvidenceReference> EvidenceReferences)
    : PlanningWorkflowResult(Assumptions, EvidenceReferences);

public sealed record PlanningInfeasibleResult(
    IReadOnlyList<PlanningValidationViolation> Violations,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<PlanningEvidenceReference> EvidenceReferences)
    : PlanningWorkflowResult(Assumptions, EvidenceReferences);

public sealed record PlanningDataUnavailableResult(
    IReadOnlyList<PlanningValidationViolation> Violations,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<PlanningEvidenceReference> EvidenceReferences)
    : PlanningWorkflowResult(Assumptions, EvidenceReferences);

public sealed record PlanningSearchLimitReachedResult(
    IReadOnlyList<PlanningValidationViolation> Violations,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<PlanningEvidenceReference> EvidenceReferences)
    : PlanningWorkflowResult(Assumptions, EvidenceReferences);

public sealed record PlanningInvalidInputResult(
    IReadOnlyList<PlanningValidationViolation> Violations,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<PlanningEvidenceReference> EvidenceReferences)
    : PlanningWorkflowResult(Assumptions, EvidenceReferences);

public sealed record PlanningCandidatesGeneratedResult(
    IReadOnlyList<PlanningCandidate> Candidates,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<PlanningEvidenceReference> EvidenceReferences)
    : PlanningWorkflowResult(Assumptions, EvidenceReferences);

public sealed record PlanningCandidateValidationRequest(
    IReadOnlyList<string> AssetIds,
    string MaintenanceWindowId,
    PlanningCandidate Candidate);

public sealed record PlanningValidationViolation(
    string Code,
    string Message,
    string? TaskId,
    string? ResourceId);

public sealed record PlanningCandidateValidationResult(
    bool IsFeasible,
    IReadOnlyList<PlanningValidationViolation> Violations);

public sealed record PlanningScheduleRequest(
    IReadOnlyList<string> AssetIds,
    string? MaintenanceWindowId,
    IReadOnlyList<string> RequiredTaskIds);

public enum PlanningScheduleOutcome
{
    CandidatesGenerated,
    ClarificationRequired,
    InvalidInput,
    DataUnavailable,
    Infeasible,
    SearchLimitReached
}

public sealed record PlanningScheduleResult(
    PlanningScheduleOutcome Outcome,
    IReadOnlyList<PlanningCandidate> Candidates,
    IReadOnlyList<PlanningValidationViolation> Violations,
    IReadOnlyList<string> ClarificationQuestions);
