using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace MaintenancePlanner.Application.Planning;

public sealed record PlanningRequest(
    string RequestText,
    IReadOnlyList<string> AssetIds,
    string? MaintenanceWindowId)
{
    /// <summary>
    /// Tasks already completed on the approved plan this request replaces (FR08). They are not scheduled again,
    /// and a task whose predecessor is among them treats that predecessor as satisfied. Empty for a first plan.
    /// </summary>
    public IReadOnlyList<string> CompletedTaskIds { get; init; } = [];

    /// <summary>
    /// Part quantities held by the unconsumed reservations of the approved plan this request replaces (FR07). The
    /// replacement's approval releases them, so planning the replacement may count them as available; no other plan
    /// may. Empty for a first plan.
    /// </summary>
    public IReadOnlyDictionary<string, decimal> ReleasableReservedQuantities { get; init; } =
        ReadOnlyDictionary<string, decimal>.Empty;

    /// <summary>
    /// Approved placements of the tasks in progress on the plan this request replaces (FR08). They are carried into
    /// the replacement unchanged (same times and crew) instead of being scheduled again. Empty for a first plan.
    /// </summary>
    public IReadOnlyList<PlanningTaskPlacement> InProgressPlacements { get; init; } = [];
}

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

    /// <summary>
    /// What happened to each candidate the model saved with <c>save_candidate</c> (AI03): included, or why not.
    /// Null, and omitted from the stored result, when the model saved none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ModelCandidateNotes { get; init; }
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
    PlanningCandidate Candidate)
{
    /// <summary>
    /// Tasks already completed on the approved plan being replaced: placing one is rejected, and they satisfy the
    /// predecessor requirement of the tasks that follow them.
    /// </summary>
    public IReadOnlyList<string> CompletedTaskIds { get; init; } = [];

    /// <summary>Part quantities the approved plan being replaced holds and its replacement's approval releases.</summary>
    public IReadOnlyDictionary<string, decimal> ReleasableReservedQuantities { get; init; } =
        ReadOnlyDictionary<string, decimal>.Empty;
}

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
    IReadOnlyList<string> RequiredTaskIds)
{
    /// <summary>
    /// Tasks already completed on the approved plan being replaced: excluded from the schedule, and treated as
    /// satisfied predecessors of the remaining work.
    /// </summary>
    public IReadOnlyList<string> CompletedTaskIds { get; init; } = [];

    /// <summary>Part quantities the approved plan being replaced holds and its replacement's approval releases.</summary>
    public IReadOnlyDictionary<string, decimal> ReleasableReservedQuantities { get; init; } =
        ReadOnlyDictionary<string, decimal>.Empty;

    /// <summary>
    /// Approved placements of tasks in progress on the plan being replaced: kept exactly as they are, with the rest
    /// of the schedule fitted around them. Each must be within the required task scope.
    /// </summary>
    public IReadOnlyList<PlanningTaskPlacement> InProgressPlacements { get; init; } = [];
}

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
