namespace MaintenancePlanner.Application.Planning;

public static class PlanningWorkflowResultMapper
{
    public static PlanningWorkflowResult FromScheduleResult(
        PlanningScheduleResult scheduleResult,
        PlanningInterpretation interpretation,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<PlanningEvidenceReference> evidenceReferences)
    {
        ArgumentNullException.ThrowIfNull(scheduleResult);
        ArgumentNullException.ThrowIfNull(interpretation);
        ArgumentNullException.ThrowIfNull(assumptions);
        ArgumentNullException.ThrowIfNull(evidenceReferences);

        return scheduleResult.Outcome switch
        {
            PlanningScheduleOutcome.CandidatesGenerated =>
                new PlanningCandidatesGeneratedResult(
                    scheduleResult.Candidates,
                    assumptions,
                    evidenceReferences)
                {
                    Interpretation = interpretation
                },
            PlanningScheduleOutcome.ClarificationRequired =>
                new PlanningClarificationRequiredResult(
                    scheduleResult.ClarificationQuestions,
                    assumptions,
                    evidenceReferences)
                {
                    Interpretation = interpretation
                },
            PlanningScheduleOutcome.Infeasible =>
                new PlanningInfeasibleResult(
                    scheduleResult.Violations,
                    assumptions,
                    evidenceReferences)
                {
                    Interpretation = interpretation
                },
            PlanningScheduleOutcome.DataUnavailable =>
                new PlanningDataUnavailableResult(
                    scheduleResult.Violations,
                    assumptions,
                    evidenceReferences)
                {
                    Interpretation = interpretation
                },
            PlanningScheduleOutcome.SearchLimitReached =>
                new PlanningSearchLimitReachedResult(
                    scheduleResult.Violations,
                    assumptions,
                    evidenceReferences)
                {
                    Interpretation = interpretation
                },
            PlanningScheduleOutcome.InvalidInput =>
                new PlanningInvalidInputResult(
                    scheduleResult.Violations,
                    assumptions,
                    evidenceReferences)
                {
                    Interpretation = interpretation
                },
            _ => throw new ArgumentOutOfRangeException(
                nameof(scheduleResult),
                scheduleResult.Outcome,
                "The planning scheduler returned an unsupported outcome.")
        };
    }
}
