using System.Collections.ObjectModel;
using MaintenancePlanner.Application.Planning.Tools;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// AI03 stage 4: save_candidate against seeded SQLite data, through the real validator, calculator and queries. Saving
/// keeps a validated candidate for the current planning job only; it never writes drafts, reservations or locks.
/// </summary>
public sealed class AI03SaveCandidateTests
{
    private const string Baseline = "MW-P101-BASELINE-20261024";
    private const string Reduced = "MW-P101-REDUCED-20261024";

    // A valid P-101 schedule that differs from both scheduler policies' candidates (it starts at 08:30)
    private static string Placements(string bearingCrew = "\"TECH-001\",\"TECH-002\"", bool includePrep = true) =>
        "{\"placements\":[" +
        (includePrep ? """{"taskId":"P101-PREP","start":"2026-10-24T08:30:00+05:30","end":"2026-10-24T09:30:00+05:30","technicianIds":["TECH-001"]},""" : "") +
        $$"""{"taskId":"P101-BEARING-REPLACEMENT","start":"2026-10-24T09:30:00+05:30","end":"2026-10-24T12:30:00+05:30","technicianIds":[{{bearingCrew}}]},""" +
        """{"taskId":"P101-VERIFICATION","start":"2026-10-24T12:30:00+05:30","end":"2026-10-24T14:30:00+05:30","technicianIds":["TECH-003"]}]}""";

    private static ModelCandidateContext Context(string windowId = Baseline, params string[] completedTaskIds) =>
        new(["P-101"], windowId, completedTaskIds, ReadOnlyDictionary<string, decimal>.Empty);

    [Fact]
    public async Task SaveCandidate_OutsideAPlanningJob_IsRefused()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();

        var result = await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, Placements(), CancellationToken.None);

        Assert.Equal(SaveCandidateTool.NoPlanningContext, result.ErrorCode);
        Assert.Empty(fixture.Proposals.Proposals);
    }

    [Fact]
    public async Task SaveCandidate_ValidCandidate_IsValidatedCostedAndKept_AndSavingItAgainReturnsTheSameNumber()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();
        fixture.Proposals.Begin(Context());

        var first = await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, Placements(), CancellationToken.None);
        var again = await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, Placements(), CancellationToken.None);

        var saved = Assert.IsType<SaveCandidateToolResult>(first.Data);
        Assert.Equal(1, saved.ProposalNumber);
        Assert.False(saved.AlreadySaved);
        Assert.Equal(Baseline, saved.WindowId);
        Assert.Equal(360.50m, saved.Metrics.TotalLabourCostAmount);
        Assert.Equal("UNKNOWN", saved.Metrics.TotalLabourCostCurrency);
        var repeat = Assert.IsType<SaveCandidateToolResult>(again.Data);
        Assert.Equal(1, repeat.ProposalNumber);
        Assert.True(repeat.AlreadySaved);
        var proposal = Assert.Single(fixture.Proposals.Proposals);
        Assert.Equal(SaveCandidateTool.Rationale, proposal.Candidate.Rationale);
        Assert.Equal(360.50m, proposal.Candidate.TotalLabourCostAmount);
    }

    [Theory]
    [InlineData("""{"windowId":"2023-10-15T08:00:00Z/2023-10-15T18:00:00Z","placements":[]}""")]
    [InlineData("""{"assetIds":["P-999"],"placements":[]}""")]
    [InlineData("""{"placements":[],"approved":true}""")]
    [InlineData("""{"placements":[{"taskId":"P101-PREP","start":"2026-10-24T08:00:00+05:30","end":"2026-10-24T09:00:00+05:30","technicianIds":["TECH-001"],"hourlyRate":1}]}""")]
    [InlineData("""{"placements":[{"taskId":"P101-PREP","start":"2026-10-24T08:00:00","end":"2026-10-24T09:00:00+05:30","technicianIds":["TECH-001"]}]}""")]
    [InlineData("{}")]
    public async Task SaveCandidate_ModelCannotChooseWindowAssetsApprovalOrRates_AndMalformedArgumentsAreRejected(string arguments)
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();
        fixture.Proposals.Begin(Context());

        var result = await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, arguments, CancellationToken.None);

        Assert.Equal("INVALID_ARGUMENTS", result.ErrorCode);
        Assert.Empty(fixture.Proposals.Proposals);
    }

    [Theory]
    [InlineData(Baseline, "\"TECH-001\",\"TECH-404\"", "TECHNICIAN_NOT_FOUND")]
    [InlineData(Baseline, "\"TECH-001\",\"TECH-003\"", "TECHNICIAN_UNQUALIFIED")]
    [InlineData(Reduced, "\"TECH-001\",\"TECH-002\"", "PART_UNAVAILABLE")]
    public async Task SaveCandidate_InvalidCandidates_AreRefusedWithTheValidatorsViolations(string windowId, string bearingCrew, string expectedCode)
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();
        fixture.Proposals.Begin(Context(windowId));

        var result = await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, Placements(bearingCrew), CancellationToken.None);

        Assert.Equal(CostCalculationTool.InvalidCandidate, result.ErrorCode);
        Assert.Contains(expectedCode, result.ErrorMessage);
        Assert.Empty(fixture.Proposals.Proposals);
    }

    [Fact]
    public async Task SaveCandidate_UsesTheReplanningContext_SoATaskCompletedOnTheReplacedPlanIsRejected()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();
        fixture.Proposals.Begin(Context(Baseline, "P101-PREP"));

        var withCompletedTask = await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, Placements(), CancellationToken.None);
        var withoutIt = await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, Placements(includePrep: false), CancellationToken.None);

        Assert.Equal(CostCalculationTool.InvalidCandidate, withCompletedTask.ErrorCode);
        Assert.Contains("TASK_ALREADY_COMPLETED", withCompletedTask.ErrorMessage);
        Assert.True(withoutIt.Ok, withoutIt.ErrorMessage);
    }

    [Fact]
    public async Task SaveCandidate_KeepsAtMostThreeDistinctCandidatesPerJob()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();
        fixture.Proposals.Begin(Context());
        string Shifted(int hour) =>
            $$"""{"placements":[{"taskId":"P101-PREP","start":"2026-10-24T{{hour:00}}:00:00+05:30","end":"2026-10-24T{{hour + 1:00}}:00:00+05:30","technicianIds":["TECH-001"]}]}""";

        var results = new List<string?>();
        foreach (var hour in new[] { 8, 9, 10, 11 })
            results.Add((await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, Shifted(hour), CancellationToken.None)).ErrorCode);

        Assert.Equal([null, null, null, SaveCandidateTool.ProposalLimit], results);
        Assert.Equal(3, fixture.Proposals.Proposals.Count);
    }

    [Fact]
    public async Task SavingCandidates_IncludingRefusedOnes_WritesNoDraftReservationLockOrBusinessAction()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();
        var before = await fixture.SnapshotAsync();
        fixture.Proposals.Begin(Context());

        await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, Placements(), CancellationToken.None);
        await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, Placements(), CancellationToken.None);
        await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, Placements("\"TECH-404\""), CancellationToken.None);
        await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, """{"windowId":"X","placements":[]}""", CancellationToken.None);
        fixture.Proposals.Begin(Context(Reduced));
        await fixture.Dispatcher.ExecuteAsync(SaveCandidateTool.ToolName, Placements(), CancellationToken.None);

        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.False(fixture.DbContext.ChangeTracker.HasChanges());
    }
}
