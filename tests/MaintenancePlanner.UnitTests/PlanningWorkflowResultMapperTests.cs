using MaintenancePlanner.Application.Planning;

namespace MaintenancePlanner.UnitTests;

public sealed class PlanningWorkflowResultMapperTests
{
    private static readonly PlanningInterpretation Interpretation = new(
        "Bearing replacement",
        "Replace the P-101 bearing",
        ["P-101"],
        ["P101-BEARING-REPLACEMENT"],
        [],
        ["Use the selected maintenance window"]);

    private static readonly PlanningEvidenceReference EvidenceReference = new(
        PlanningEvidenceSourceKind.MaintenanceManual,
        "MAN-P101-PLANNING-001:page:3",
        "MAN-P101-PLANNING-001",
        "1.0",
        3,
        "Bearing replacement",
        "Synthetic test evidence excerpt.");

    private static readonly PlanningValidationViolation Violation = new(
        "PART_UNAVAILABLE",
        "BR-101 stock is insufficient.",
        "P101-BEARING-REPLACEMENT",
        "BR-101");

    private static readonly IReadOnlyList<string> Assumptions = ["Synthetic test assumption"];

    [Fact]
    public void FromScheduleResult_CandidatesGenerated_PreservesCandidateAndWorkflowDetails()
    {
        var candidate = new PlanningCandidate(
            [
                new PlanningTaskPlacement(
                    "P101-BEARING-REPLACEMENT",
                    new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(5.5)),
                    new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(5.5)),
                    ["TECH-001", "TECH-002"])
            ],
            "Earliest feasible schedule",
            [EvidenceReference]);

        var result = PlanningWorkflowResultMapper.FromScheduleResult(
            new PlanningScheduleResult(
                PlanningScheduleOutcome.CandidatesGenerated,
                [candidate],
                [],
                []),
            Interpretation,
            Assumptions,
            [EvidenceReference]);

        var candidates = Assert.IsType<PlanningCandidatesGeneratedResult>(result);
        Assert.Same(Interpretation, candidates.Interpretation);
        Assert.Same(Assumptions, candidates.Assumptions);
        Assert.Same(EvidenceReference, Assert.Single(candidates.EvidenceReferences));
        var mappedCandidate = Assert.Single(candidates.Candidates);
        Assert.Same(candidate, mappedCandidate);
        Assert.Equal("P101-BEARING-REPLACEMENT", Assert.Single(mappedCandidate.TaskPlacements).TaskId);
        Assert.Same(EvidenceReference, Assert.Single(mappedCandidate.EvidenceReferences));
    }

    [Fact]
    public void FromScheduleResult_ClarificationRequired_PreservesQuestionsAndContext()
    {
        var questions = new[] { "Which maintenance window should be used?" };
        var result = PlanningWorkflowResultMapper.FromScheduleResult(
            new PlanningScheduleResult(
                PlanningScheduleOutcome.ClarificationRequired,
                [],
                [],
                questions),
            Interpretation,
            Assumptions,
            [EvidenceReference]);

        var clarification = Assert.IsType<PlanningClarificationRequiredResult>(result);
        Assert.Same(Interpretation, clarification.Interpretation);
        Assert.Same(questions, clarification.Questions);
        Assert.Same(Assumptions, clarification.Assumptions);
        Assert.Same(EvidenceReference, Assert.Single(clarification.EvidenceReferences));
    }

    [Theory]
    [InlineData(PlanningScheduleOutcome.Infeasible, typeof(PlanningInfeasibleResult))]
    [InlineData(PlanningScheduleOutcome.DataUnavailable, typeof(PlanningDataUnavailableResult))]
    [InlineData(PlanningScheduleOutcome.SearchLimitReached, typeof(PlanningSearchLimitReachedResult))]
    [InlineData(PlanningScheduleOutcome.InvalidInput, typeof(PlanningInvalidInputResult))]
    public void FromScheduleResult_NonCandidateOutcomes_PreserveDetailedViolations(
        PlanningScheduleOutcome outcome,
        Type expectedResultType)
    {
        var result = PlanningWorkflowResultMapper.FromScheduleResult(
            new PlanningScheduleResult(outcome, [], [Violation], []),
            Interpretation,
            Assumptions,
            [EvidenceReference]);

        Assert.Equal(expectedResultType, result.GetType());
        var violations = outcome switch
        {
            PlanningScheduleOutcome.Infeasible =>
                Assert.IsType<PlanningInfeasibleResult>(result).Violations,
            PlanningScheduleOutcome.DataUnavailable =>
                Assert.IsType<PlanningDataUnavailableResult>(result).Violations,
            PlanningScheduleOutcome.SearchLimitReached =>
                Assert.IsType<PlanningSearchLimitReachedResult>(result).Violations,
            PlanningScheduleOutcome.InvalidInput =>
                Assert.IsType<PlanningInvalidInputResult>(result).Violations,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };

        Assert.Same(Violation, Assert.Single(violations));
        Assert.Equal("P101-BEARING-REPLACEMENT", violations[0].TaskId);
        Assert.Equal("BR-101", violations[0].ResourceId);
        Assert.Same(Interpretation, result.Interpretation);
        Assert.Same(Assumptions, result.Assumptions);
        Assert.Same(EvidenceReference, Assert.Single(result.EvidenceReferences));
    }
}
