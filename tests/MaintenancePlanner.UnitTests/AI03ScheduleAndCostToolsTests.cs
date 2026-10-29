using System.Text.Json;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Planning.Tools;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using Moq;

namespace MaintenancePlanner.UnitTests;

public sealed class AI03ScheduleAndCostToolsTests
{
    private const string Baseline = "MW-P101-BASELINE-20261024";
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly DateTimeOffset WindowStart = new(2026, 10, 24, 8, 0, 0, Ist);

    // A feasible P-101 candidate: preparation, bearing replacement (crew of two) and verification
    private const string FeasibleCandidate = """
        {"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[
          {"taskId":"P101-PREP","start":"2026-10-24T08:00:00+05:30","end":"2026-10-24T09:00:00+05:30","technicianIds":["TECH-001"]},
          {"taskId":"P101-BEARING-REPLACEMENT","start":"2026-10-24T09:00:00+05:30","end":"2026-10-24T12:00:00+05:30","technicianIds":["TECH-001","TECH-002"]},
          {"taskId":"P101-VERIFICATION","start":"2026-10-24T12:00:00+05:30","end":"2026-10-24T14:00:00+05:30","technicianIds":["TECH-003"]}]}
        """;

    // Duplicate technician, inverted interval, unknown task and unknown technician: domain rules, left to the validator
    private const string InvalidCandidate = """
        {"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[
          {"taskId":"P101-PREP","start":"2026-10-24T09:00:00+05:30","end":"2026-10-24T08:00:00+05:30","technicianIds":["TECH-001","TECH-001"]},
          {"taskId":"P101-UNKNOWN","start":"2026-10-24T09:00:00+05:30","end":"2026-10-24T10:00:00+05:30","technicianIds":["TECH-001"]},
          {"taskId":"P101-BEARING-REPLACEMENT","start":"2026-10-24T09:00:00+05:30","end":"2026-10-24T12:00:00+05:30","technicianIds":["TECH-001","TECH-404"]}]}
        """;

    [Fact]
    public async Task ValidateSchedule_FeasibleCandidate_AgreesWithTheValidator_AndStatesItsLimitations()
    {
        var fixture = new Fixture();

        var result = await fixture.Dispatcher.ExecuteAsync(ScheduleValidationTool.ToolName, FeasibleCandidate, CancellationToken.None);

        var data = Assert.IsType<ScheduleValidationToolResult>(AssertSuccess(result));
        var direct = await fixture.ValidateDirectlyAsync(FeasibleCandidate);
        Assert.True(direct.IsFeasible);
        Assert.Equal(direct.IsFeasible, data.IsFeasible);
        Assert.Equal(direct.Violations, data.Violations);
        Assert.Equal(Baseline, data.WindowId);
        Assert.Equal(["P-101"], data.AssetIds);
        Assert.Contains(data.Limitations, limitation => limitation.Contains("committed to other approved plans"));
    }

    [Fact]
    public async Task ValidateSchedule_DomainViolations_AreTheValidatorsViolationsUnchanged()
    {
        var fixture = new Fixture();

        var result = await fixture.Dispatcher.ExecuteAsync(ScheduleValidationTool.ToolName, InvalidCandidate, CancellationToken.None);

        var data = Assert.IsType<ScheduleValidationToolResult>(AssertSuccess(result));
        var direct = await fixture.ValidateDirectlyAsync(InvalidCandidate);
        Assert.False(data.IsFeasible);
        Assert.Equal(direct.Violations, data.Violations);
        Assert.Contains(data.Violations, v => v.Code == "DUPLICATE_TECHNICIAN_ASSIGNMENT");
        Assert.Contains(data.Violations, v => v.Code == "INVALID_TASK_INTERVAL");
        Assert.Contains(data.Violations, v => v.Code == "TASK_NOT_FOUND");
        Assert.Contains(data.Violations, v => v.Code == "TECHNICIAN_NOT_FOUND");
    }

    [Fact]
    public async Task ValidateSchedule_StoredSpellingOfWindowAndAssets_IsWhatTheValidatorReceives()
    {
        var fixture = new Fixture();
        var lowerCase = FeasibleCandidate.Replace("\"P-101\"]", "\"p-101\"]").Replace(Baseline, Baseline.ToLowerInvariant());

        var result = await fixture.Dispatcher.ExecuteAsync(ScheduleValidationTool.ToolName, lowerCase, CancellationToken.None);

        var data = Assert.IsType<ScheduleValidationToolResult>(AssertSuccess(result));
        Assert.True(data.IsFeasible);
        Assert.Equal(Baseline, data.WindowId);
        Assert.Equal(["P-101"], data.AssetIds);
    }

    public static TheoryData<string, string> MalformedCandidates()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in new[] { ScheduleValidationTool.ToolName, CostCalculationTool.ToolName })
        {
            data.Add(tool, """{"windowId":"MW-P101-BASELINE-20261024","placements":[]}""");
            data.Add(tool, """{"assetIds":["P-101"],"placements":[]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":7,"placements":[]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":{}}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":["P101-PREP"]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","rates":{"TECH-001":1},"placements":[{"taskId":"P101-PREP","start":"2026-10-24T08:00:00+05:30","end":"2026-10-24T09:00:00+05:30","technicianIds":["TECH-001"]}]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[{"taskId":"P101-PREP","start":"2026-10-24T08:00:00+05:30","end":"2026-10-24T09:00:00+05:30","technicianIds":["TECH-001"],"hourlyRate":1}]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[{"taskId":"P101-PREP","start":"2026-10-24T08:00:00+05:30","technicianIds":["TECH-001"]}]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[{"taskId":"P101-PREP","start":"2026-10-24T08:00:00","end":"2026-10-24T09:00:00+05:30","technicianIds":["TECH-001"]}]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[{"taskId":"P101-PREP","start":"2026-10-24","end":"2026-10-24T09:00:00+05:30","technicianIds":["TECH-001"]}]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[{"taskId":"P101-PREP","start":"2026-13-45T08:00:00+05:30","end":"2026-10-24T09:00:00+05:30","technicianIds":["TECH-001"]}]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[{"taskId":"P101-PREP","start":"2026-10-24T08:00:00+05:30","end":"2026-10-24T09:00:00+05:30","technicianIds":"TECH-001"}]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[{"taskId":"P101-PREP","start":"2026-10-24T08:00:00+05:30","end":"2026-10-24T09:00:00+05:30","technicianIds":[" "]}]}""");
            data.Add(tool, """{"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[{"taskId":42,"start":"2026-10-24T08:00:00+05:30","end":"2026-10-24T09:00:00+05:30","technicianIds":["TECH-001"]}]}""");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MalformedCandidates))]
    public async Task ScheduleTools_MalformedArguments_ReturnInvalidArguments_WithoutReadingData(string toolName, string arguments)
    {
        var fixture = new Fixture();

        var result = await fixture.Dispatcher.ExecuteAsync(toolName, arguments, CancellationToken.None);

        AssertFailure(result, PlanningToolErrors.InvalidArguments);
        Assert.Empty(fixture.Queries.Invocations);
        Assert.Empty(fixture.Provider.Invocations);
    }

    [Theory]
    [InlineData(ScheduleValidationTool.ToolName, "MW-UNKNOWN", "P-101", PlanningToolErrors.UnknownWindow)]
    [InlineData(ScheduleValidationTool.ToolName, Baseline, "P-999", PlanningToolErrors.UnknownAsset)]
    [InlineData(CostCalculationTool.ToolName, "MW-UNKNOWN", "P-101", PlanningToolErrors.UnknownWindow)]
    [InlineData(CostCalculationTool.ToolName, Baseline, "P-999", PlanningToolErrors.UnknownAsset)]
    public async Task ScheduleTools_UnknownWindowOrAsset_AreErrors(string toolName, string windowId, string assetId, string errorCode)
    {
        var fixture = new Fixture();
        var arguments = FeasibleCandidate.Replace(Baseline, windowId).Replace("\"P-101\"]", $"\"{assetId}\"]");

        var result = await fixture.Dispatcher.ExecuteAsync(toolName, arguments, CancellationToken.None);

        AssertFailure(result, errorCode);
    }

    [Fact]
    public async Task CalculateCost_ValidCandidate_MatchesTheCalculatorWithStoredRates_AndKeepsTheUnknownCurrency()
    {
        var fixture = new Fixture();

        var result = await fixture.Dispatcher.ExecuteAsync(CostCalculationTool.ToolName, FeasibleCandidate, CancellationToken.None);

        var data = Assert.IsType<CostCalculationToolResult>(AssertSuccess(result));
        var direct = new CandidateCostCalculator().CalculateMetrics(
            fixture.ReadCandidate(FeasibleCandidate),
            fixture.Technicians.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(direct, data.Metrics);
        // 42 x 1h + (42 + 39.50) x 3h + 37 x 2h
        Assert.Equal(360.50m, data.Metrics.TotalLabourCostAmount);
        Assert.Equal("UNKNOWN", data.Metrics.TotalLabourCostCurrency);
        Assert.Equal(360, data.Metrics.CompletionTimeMinutes);
        Assert.True(data.Metrics.HasValidCosts);
    }

    [Fact]
    public async Task CalculateCost_CandidateFailingValidation_IsRefused_WithTheValidatorsViolations()
    {
        var fixture = new Fixture();

        var result = await fixture.Dispatcher.ExecuteAsync(CostCalculationTool.ToolName, InvalidCandidate, CancellationToken.None);

        AssertFailure(result, CostCalculationTool.InvalidCandidate);
        Assert.Contains("TECHNICIAN_NOT_FOUND", result.ErrorMessage);
        Assert.Contains("DUPLICATE_TECHNICIAN_ASSIGNMENT", result.ErrorMessage);
    }

    [Fact]
    public async Task CalculateCost_StoredNegativeRate_IsReportedAsTheCalculatorReportsIt_NotNormalised()
    {
        var fixture = new Fixture(technicians => technicians
            .Select(t => t.Id == "TECH-003" ? t with { HourlyRate = -1m } : t)
            .ToArray());

        var result = await fixture.Dispatcher.ExecuteAsync(CostCalculationTool.ToolName, FeasibleCandidate, CancellationToken.None);

        var data = Assert.IsType<CostCalculationToolResult>(AssertSuccess(result));
        Assert.False(data.Metrics.HasValidCosts);
        Assert.Contains("TECH-003", data.Metrics.CostWarning);
        Assert.Equal(286.50m, data.Metrics.TotalLabourCostAmount);
    }

    [Theory]
    [InlineData(ScheduleValidationTool.ToolName)]
    [InlineData(CostCalculationTool.ToolName)]
    public async Task ScheduleTools_CancellationDuringValidation_PropagatesThroughTheDispatcher(string toolName)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.Queries.Setup(q => q.GetTechniciansAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<TechnicianReadModel>>(fixture.Technicians);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Dispatcher.ExecuteAsync(toolName, FeasibleCandidate, cancellation.Token));
    }

    [Theory]
    [InlineData(ScheduleValidationTool.ToolName)]
    [InlineData(CostCalculationTool.ToolName)]
    public async Task ScheduleTools_DataFailure_BecomesExecutionFailure(string toolName)
    {
        var fixture = new Fixture();
        fixture.Queries.Setup(q => q.GetTechniciansAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database detail"));

        var result = await fixture.Dispatcher.ExecuteAsync(toolName, FeasibleCandidate, CancellationToken.None);

        AssertFailure(result, PlanningToolErrors.ToolExecutionFailed);
        Assert.DoesNotContain("database detail", result.ErrorMessage);
    }

    [Fact]
    public void ScheduleTools_OfferASchemaRequiringTheCandidate_AndRejectingUnknownFields()
    {
        var fixture = new Fixture();

        foreach (var tool in fixture.Dispatcher.Tools)
        {
            var schema = tool.ParametersSchema;
            Assert.Equal(["assetIds", "windowId", "placements"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.False(schema.GetProperty("properties").GetProperty("placements").GetProperty("items").GetProperty("additionalProperties").GetBoolean());
        }
    }

    private sealed class Fixture
    {
        public Fixture(Func<TechnicianReadModel[], TechnicianReadModel[]>? adjustTechnicians = null)
        {
            Technicians = (adjustTechnicians ?? (t => t))(
            [
                Technician("TECH-001", 42m, "MECHANICAL", "ROTATING-EQUIPMENT"),
                Technician("TECH-002", 39.5m, "MECHANICAL", "ROTATING-EQUIPMENT"),
                Technician("TECH-003", 37m, "MECHANICAL", "INSTRUMENTATION")
            ]);

            Queries = new Mock<IMaintenancePlannerQueries>();
            Queries.Setup(q => q.GetAssetsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new AssetReadModel("P-101", "PLANT-01", "Cooling Water Pump P-101", "CP-100", 1)]);
            Queries.Setup(q => q.GetMaintenanceWindowsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new MaintenanceWindowReadModel(Baseline, "Baseline", WindowStart, WindowStart.AddHours(8), "Asia/Kolkata")]);
            Queries.Setup(q => q.GetTaskCatalogueAsync("P-101", It.IsAny<CancellationToken>())).ReturnsAsync(
            [
                new MaintenanceTaskReadModel("P101-PREP", "P-101", "Preparation", 60, "MECHANICAL", 1, null, 0m, null, "MAN-P101-001", "Preparation"),
                new MaintenanceTaskReadModel("P101-BEARING-REPLACEMENT", "P-101", "Bearing replacement", 180, "ROTATING-EQUIPMENT", 2, "BR-101", 1m, "P101-PREP", "MAN-P101-001", "Bearing replacement"),
                new MaintenanceTaskReadModel("P101-VERIFICATION", "P-101", "Verification", 120, "INSTRUMENTATION", 1, null, 0m, "P101-BEARING-REPLACEMENT", "MAN-P101-001", "Verification")
            ]);
            Queries.Setup(q => q.GetTechniciansAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Technicians);

            var bearing = new SparePartReadModel("BR-101", "Bearing", "each", 1m, 0m, 1m);
            Provider = new Mock<ISparePartReadModelProvider>();
            Provider.Setup(p => p.GetSparePartsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([bearing]);
            Provider.Setup(p => p.GetCompatibleSparePartsAsync("P-101", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([bearing]);

            Validator = new PlanningCandidateValidator(Provider.Object, Queries.Object);
            Dispatcher = new PlanningToolDispatcher(
            [
                new ScheduleValidationTool(Validator, Queries.Object),
                new CostCalculationTool(Validator, new CandidateCostCalculator(), Queries.Object)
            ]);
        }

        public TechnicianReadModel[] Technicians { get; }
        public Mock<IMaintenancePlannerQueries> Queries { get; }
        public Mock<ISparePartReadModelProvider> Provider { get; }
        public PlanningCandidateValidator Validator { get; }
        public PlanningToolDispatcher Dispatcher { get; }

        /// <summary>The candidate in the arguments, built independently of the tools' argument reader.</summary>
        public PlanningCandidate ReadCandidate(string arguments)
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

        public Task<PlanningCandidateValidationResult> ValidateDirectlyAsync(string arguments) =>
            Validator.ValidateAsync(
                new PlanningCandidateValidationRequest(["P-101"], Baseline, ReadCandidate(arguments)),
                CancellationToken.None);

        private static TechnicianReadModel Technician(string id, decimal rate, params string[] skills) =>
            new(id, id, rate, skills, [new TechnicianAvailabilityReadModel(WindowStart, WindowStart.AddHours(8))]);
    }

    private static object AssertSuccess(PlanningToolResult result)
    {
        Assert.True(result.Ok, $"{result.ErrorCode}: {result.ErrorMessage}");
        return result.Data!;
    }

    private static void AssertFailure(PlanningToolResult result, string errorCode)
    {
        Assert.False(result.Ok);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Null(result.Data);
    }
}
