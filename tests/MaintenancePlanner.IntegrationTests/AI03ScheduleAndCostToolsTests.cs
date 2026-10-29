using System.Text.Json;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Planning.Tools;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>AI03 stage 2: schedule validation and cost tools against seeded SQLite data, through the real services.</summary>
public sealed class AI03ScheduleAndCostToolsTests
{
    private const string Baseline = "MW-P101-BASELINE-20261024";
    private const string Reduced = "MW-P101-REDUCED-20261024";

    private static string Candidate(string windowId, string bearingCrew = "\"TECH-001\",\"TECH-002\"") => $$"""
        {"assetIds":["P-101"],"windowId":"{{windowId}}","placements":[
          {"taskId":"P101-PREP","start":"2026-10-24T08:00:00+05:30","end":"2026-10-24T09:00:00+05:30","technicianIds":["TECH-001"]},
          {"taskId":"P101-BEARING-REPLACEMENT","start":"2026-10-24T09:00:00+05:30","end":"2026-10-24T12:00:00+05:30","technicianIds":[{{bearingCrew}}]},
          {"taskId":"P101-VERIFICATION","start":"2026-10-24T12:00:00+05:30","end":"2026-10-24T14:00:00+05:30","technicianIds":["TECH-003"]}]}
        """;

    [Fact]
    public async Task ValidateSchedule_SeededBaselineCandidate_IsFeasible_AsTheValidatorFindsIt()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();

        var result = await fixture.Dispatcher.ExecuteAsync(ScheduleValidationTool.ToolName, Candidate(Baseline), CancellationToken.None);

        var data = Assert.IsType<ScheduleValidationToolResult>(result.Data);
        var direct = await ValidateDirectlyAsync(fixture, Candidate(Baseline), Baseline);
        Assert.True(direct.IsFeasible);
        Assert.True(data.IsFeasible);
        Assert.Equal(direct.Violations, data.Violations);
        Assert.Equal(ScheduleValidationTool.Limitations, data.Limitations);
    }

    [Fact]
    public async Task ValidateSchedule_ReducedWindow_ReturnsTheValidatorsWindowAndStockViolations()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();

        var result = await fixture.Dispatcher.ExecuteAsync(ScheduleValidationTool.ToolName, Candidate(Reduced), CancellationToken.None);

        var data = Assert.IsType<ScheduleValidationToolResult>(result.Data);
        var direct = await ValidateDirectlyAsync(fixture, Candidate(Reduced), Reduced);
        Assert.False(data.IsFeasible);
        Assert.Equal(direct.Violations, data.Violations);
        Assert.Contains(data.Violations, v => v.Code == "TASK_OUTSIDE_WINDOW" && v.TaskId == "P101-VERIFICATION");
        Assert.Contains(data.Violations, v => v.Code == "PART_UNAVAILABLE" && v.ResourceId == "BR-101");
    }

    [Fact]
    public async Task CalculateCost_SeededBaselineCandidate_MatchesTheCalculatorWithStoredRates()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();

        var result = await fixture.Dispatcher.ExecuteAsync(CostCalculationTool.ToolName, Candidate(Baseline), CancellationToken.None);

        var data = Assert.IsType<CostCalculationToolResult>(result.Data);
        var technicians = await fixture.Queries.GetTechniciansAsync(CancellationToken.None);
        var direct = new CandidateCostCalculator().CalculateMetrics(
            ReadCandidate(Candidate(Baseline)),
            technicians.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(direct, data.Metrics);
        Assert.Equal(360.50m, data.Metrics.TotalLabourCostAmount);
        Assert.Equal("UNKNOWN", data.Metrics.TotalLabourCostCurrency);
        Assert.Equal(360, data.Metrics.CompletionTimeMinutes);
    }

    [Theory]
    [InlineData("\"TECH-001\",\"TECH-404\"", "TECHNICIAN_NOT_FOUND")]
    [InlineData("\"TECH-001\",\"TECH-003\"", "TECHNICIAN_UNQUALIFIED")]
    [InlineData("\"TECH-001\"", "CREW_SIZE_UNMET")]
    public async Task CalculateCost_CandidateFailingValidation_IsRefused(string bearingCrew, string expectedCode)
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();

        var result = await fixture.Dispatcher.ExecuteAsync(
            CostCalculationTool.ToolName, Candidate(Baseline, bearingCrew), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(CostCalculationTool.InvalidCandidate, result.ErrorCode);
        Assert.Contains(expectedCode, result.ErrorMessage);
    }

    [Fact]
    public async Task RunningTheScheduleTools_IncludingFailingCalls_WritesNoDraftReservationLockOrBusinessAction()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();
        var before = await fixture.SnapshotAsync();

        string[][] calls =
        [
            [ScheduleValidationTool.ToolName, Candidate(Baseline)],
            [ScheduleValidationTool.ToolName, Candidate(Reduced)],
            [ScheduleValidationTool.ToolName, Candidate("MW-UNKNOWN")],
            [ScheduleValidationTool.ToolName, """{"assetIds":["P-101"]}"""],
            [CostCalculationTool.ToolName, Candidate(Baseline)],
            [CostCalculationTool.ToolName, Candidate(Reduced)],
            [CostCalculationTool.ToolName, Candidate(Baseline, "\"TECH-404\"")],
            [CostCalculationTool.ToolName, "{not json"]
        ];
        foreach (var call in calls)
            await fixture.Dispatcher.ExecuteAsync(call[0], call[1], CancellationToken.None);

        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.False(fixture.DbContext.ChangeTracker.HasChanges());
    }

    private static Task<PlanningCandidateValidationResult> ValidateDirectlyAsync(
        AI03ToolFixture fixture,
        string arguments,
        string windowId) =>
        fixture.Validator.ValidateAsync(
            new PlanningCandidateValidationRequest(["P-101"], windowId, ReadCandidate(arguments)),
            CancellationToken.None);

    /// <summary>The candidate in the arguments, built independently of the tools' argument reader.</summary>
    private static PlanningCandidate ReadCandidate(string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        var placements = document.RootElement.GetProperty("placements").EnumerateArray()
            .Select(p => new PlanningTaskPlacement(
                p.GetProperty("taskId").GetString()!,
                DateTimeOffset.Parse(p.GetProperty("start").GetString()!),
                DateTimeOffset.Parse(p.GetProperty("end").GetString()!),
                p.GetProperty("technicianIds").EnumerateArray().Select(t => t.GetString()!).ToArray()))
            .ToArray();
        return new PlanningCandidate(placements, string.Empty, []);
    }
}
