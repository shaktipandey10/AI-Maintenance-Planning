using System.Text.Json;
using MaintenancePlanner.Application.Planning.Tools;
using MaintenancePlanner.Application.Queries;
using Moq;

namespace MaintenancePlanner.UnitTests;

public sealed class AI03PlanningToolsTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5));

    // Dispatcher

    [Theory]
    [InlineData("no_such_tool")]
    [InlineData("LOOKUP_STOCK")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Dispatcher_UnknownTool_ReturnsUnknownToolError(string? toolName)
    {
        var dispatcher = CreateDispatcher(out _, out _);

        var result = await dispatcher.ExecuteAsync(toolName, "{}", CancellationToken.None);

        AssertFailure(result, PlanningToolErrors.UnknownTool);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"P-101\"")]
    public async Task Dispatcher_MalformedOrNonObjectArguments_ReturnInvalidArguments_WithoutCallingTheTool(string arguments)
    {
        var tool = new FakeTool("fake", _ => PlanningToolResult.Success(new object()));
        var dispatcher = new PlanningToolDispatcher([tool]);

        var result = await dispatcher.ExecuteAsync("fake", arguments, CancellationToken.None);

        AssertFailure(result, PlanningToolErrors.InvalidArguments);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task Dispatcher_CancelledToken_Throws_WithoutCallingTheTool()
    {
        var tool = new FakeTool("fake", _ => PlanningToolResult.Success(new object()));
        var dispatcher = new PlanningToolDispatcher([tool]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.ExecuteAsync("fake", "{}", cancellation.Token));
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task Dispatcher_CancellationDuringTheTool_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = new FakeTool("fake", _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return PlanningToolResult.Success(new object());
        });
        var dispatcher = new PlanningToolDispatcher([tool]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.ExecuteAsync("fake", "{}", cancellation.Token));
    }

    [Fact]
    public async Task Dispatcher_ToolException_BecomesExecutionFailure_WithoutLeakingTheExceptionText()
    {
        var tool = new FakeTool("fake", _ => throw new InvalidOperationException("connection string secret"));
        var dispatcher = new PlanningToolDispatcher([tool]);

        var result = await dispatcher.ExecuteAsync("fake", "{}", CancellationToken.None);

        AssertFailure(result, PlanningToolErrors.ToolExecutionFailed);
        Assert.DoesNotContain("secret", result.ErrorMessage);
    }

    public static TheoryData<PlanningToolResult?> MalformedResults => new()
    {
        null,
        new PlanningToolResult(true, null, null, null),
        new PlanningToolResult(true, new object(), "CODE", null),
        new PlanningToolResult(false, null, null, "message"),
        new PlanningToolResult(false, null, "CODE", " "),
        new PlanningToolResult(false, new object(), "CODE", "message")
    };

    [Theory]
    [MemberData(nameof(MalformedResults))]
    public async Task Dispatcher_MalformedToolResult_BecomesInvalidToolResult(PlanningToolResult? malformed)
    {
        var dispatcher = new PlanningToolDispatcher([new FakeTool("fake", _ => malformed!)]);

        var result = await dispatcher.ExecuteAsync("fake", "{}", CancellationToken.None);

        AssertFailure(result, PlanningToolErrors.InvalidToolResult);
    }

    [Fact]
    public async Task Dispatcher_BlankArguments_AreAnEmptyObject()
    {
        JsonElement? received = null;
        var dispatcher = new PlanningToolDispatcher([new FakeTool("fake", arguments =>
        {
            received = arguments;
            return PlanningToolResult.Success(new object());
        })]);

        var result = await dispatcher.ExecuteAsync("fake", "  ", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(JsonValueKind.Object, received!.Value.ValueKind);
        Assert.Empty(received.Value.EnumerateObject());
    }

    [Fact]
    public void Dispatcher_DuplicateOrBlankToolNames_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new PlanningToolDispatcher(
            [new FakeTool("fake", _ => null!), new FakeTool("fake", _ => null!)]));
        Assert.Throws<InvalidOperationException>(() => new PlanningToolDispatcher([new FakeTool(" ", _ => null!)]));
    }

    // Argument validation, through the read tools

    [Theory]
    [InlineData(StockLookupTool.ToolName, """{"unexpected":"x"}""")]
    [InlineData(StockLookupTool.ToolName, """{"windowId":42}""")]
    [InlineData(StockLookupTool.ToolName, """{"windowId":" "}""")]
    [InlineData(StockLookupTool.ToolName, """{"windowId":" MW-P101-BASELINE-20261024"}""")]
    [InlineData(StockLookupTool.ToolName, """{"partIds":"BR-101"}""")]
    [InlineData(StockLookupTool.ToolName, """{"partIds":[]}""")]
    [InlineData(StockLookupTool.ToolName, """{"partIds":["BR-101","br-101"]}""")]
    [InlineData(StockLookupTool.ToolName, """{"partIds":["BR-101",7]}""")]
    [InlineData(TechnicianAvailabilityTool.ToolName, """{"technicianIds":[null]}""")]
    [InlineData(TaskCatalogueTool.ToolName, "{}")]
    [InlineData(TaskCatalogueTool.ToolName, """{"assetId":null}""")]
    [InlineData(TaskCatalogueTool.ToolName, """{"assetId":["P-101"]}""")]
    [InlineData(TaskCatalogueTool.ToolName, """{"assetId":"P-101","extra":true}""")]
    public async Task ReadTools_MalformedArguments_ReturnInvalidArguments_WithoutReadingData(string toolName, string arguments)
    {
        var dispatcher = CreateDispatcher(out var queries, out var provider);

        var result = await dispatcher.ExecuteAsync(toolName, arguments, CancellationToken.None);

        AssertFailure(result, PlanningToolErrors.InvalidArguments);
        Assert.Empty(queries.Invocations);
        Assert.Empty(provider.Invocations);
    }

    [Fact]
    public async Task ReadTools_OverlongIdentifierOrTooManyIdentifiers_AreRejected()
    {
        var dispatcher = CreateDispatcher(out _, out _);
        // Limits: identifiers of at most 128 characters, lists of at most 50 identifiers
        var overlong = new string('A', 129);
        var tooMany = Enumerable.Range(0, 51).Select(i => $"T-{i}").ToArray();

        AssertFailure(
            await dispatcher.ExecuteAsync(TaskCatalogueTool.ToolName, JsonSerializer.Serialize(new { assetId = overlong }), CancellationToken.None),
            PlanningToolErrors.InvalidArguments);
        AssertFailure(
            await dispatcher.ExecuteAsync(TechnicianAvailabilityTool.ToolName, JsonSerializer.Serialize(new { technicianIds = tooMany }), CancellationToken.None),
            PlanningToolErrors.InvalidArguments);
    }

    // Stock lookup

    [Fact]
    public async Task StockLookup_UsesTheWindowAwareProvider_WithTheStoredWindowAndAssetIds()
    {
        var dispatcher = CreateDispatcher(out _, out var provider);
        provider.Setup(p => p.GetCompatibleSparePartsAsync("P-101", "MW-P101-REDUCED-20261024", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SparePartReadModel("BR-101", "Bearing", "each", 1m, 1m, 0m)]);

        var result = await dispatcher.ExecuteAsync(
            StockLookupTool.ToolName,
            """{"windowId":"mw-p101-reduced-20261024","assetId":"p-101","partIds":["br-101"]}""",
            CancellationToken.None);

        var data = Assert.IsType<StockLookupResult>(AssertSuccess(result));
        Assert.Equal("MW-P101-REDUCED-20261024", data.WindowId);
        Assert.Equal("P-101", data.AssetId);
        Assert.Equal(0m, Assert.Single(data.Parts).AvailableQuantity);
    }

    [Theory]
    [InlineData("""{"windowId":"MW-UNKNOWN"}""", PlanningToolErrors.UnknownWindow)]
    [InlineData("""{"assetId":"P-999"}""", PlanningToolErrors.UnknownAsset)]
    [InlineData("""{"partIds":["BR-101","PART-UNKNOWN"]}""", PlanningToolErrors.UnknownPart)]
    public async Task StockLookup_UnknownIdentifiers_AreErrors_NotEmptyData(string arguments, string errorCode)
    {
        var dispatcher = CreateDispatcher(out _, out var provider);
        provider.Setup(p => p.GetSparePartsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SparePartReadModel("BR-101", "Bearing", "each", 1m, 0m, 1m)]);

        var result = await dispatcher.ExecuteAsync(StockLookupTool.ToolName, arguments, CancellationToken.None);

        AssertFailure(result, errorCode);
    }

    [Theory]
    [InlineData(1, 0, 2)]
    [InlineData(-1, 0, -1)]
    [InlineData(1, -1, 2)]
    public async Task StockLookup_InconsistentStockRecord_IsRejected(decimal onHand, decimal reserved, decimal available)
    {
        var dispatcher = CreateDispatcher(out _, out var provider);
        provider.Setup(p => p.GetSparePartsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SparePartReadModel("BR-101", "Bearing", "each", onHand, reserved, available)]);

        var result = await dispatcher.ExecuteAsync(StockLookupTool.ToolName, "{}", CancellationToken.None);

        AssertFailure(result, PlanningToolErrors.InvalidToolResult);
    }

    // Technician availability

    [Fact]
    public async Task TechnicianAvailability_ReturnsDeclaredAvailabilityInTheWindow_AndStatesTheLimitation()
    {
        var dispatcher = CreateDispatcher(out var queries, out _);
        queries.Setup(q => q.GetTechniciansAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new TechnicianReadModel("TECH-001", "Asha Rao", 42m, ["MECHANICAL"],
            [
                new TechnicianAvailabilityReadModel(WindowStart, WindowStart.AddHours(8)),
                new TechnicianAvailabilityReadModel(WindowStart.AddDays(2), WindowStart.AddDays(2).AddHours(8))
            ]),
            new TechnicianReadModel("TECH-002", "Kabir Shah", 39.5m, ["MECHANICAL"], [])
        ]);

        var result = await dispatcher.ExecuteAsync(
            TechnicianAvailabilityTool.ToolName,
            """{"technicianIds":["tech-001"],"windowId":"MW-P101-BASELINE-20261024"}""",
            CancellationToken.None);

        var data = Assert.IsType<TechnicianAvailabilityToolResult>(AssertSuccess(result));
        Assert.Equal(TechnicianAvailabilityTool.Limitation, data.Limitation);
        Assert.Contains("not subtracted", data.Limitation);
        var technician = Assert.Single(data.Technicians);
        Assert.Equal("TECH-001", technician.Id);
        Assert.Equal(WindowStart, Assert.Single(technician.DeclaredAvailability).AvailableFrom);
    }

    [Fact]
    public async Task TechnicianAvailability_UnknownTechnicianOrWindow_AreErrors()
    {
        var dispatcher = CreateDispatcher(out var queries, out _);
        queries.Setup(q => q.GetTechniciansAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TechnicianReadModel("TECH-001", "Asha Rao", 42m, [], [])]);

        AssertFailure(
            await dispatcher.ExecuteAsync(TechnicianAvailabilityTool.ToolName, """{"technicianIds":["TECH-404"]}""", CancellationToken.None),
            PlanningToolErrors.UnknownTechnician);
        AssertFailure(
            await dispatcher.ExecuteAsync(TechnicianAvailabilityTool.ToolName, """{"windowId":"MW-UNKNOWN"}""", CancellationToken.None),
            PlanningToolErrors.UnknownWindow);
    }

    [Fact]
    public async Task TechnicianAvailability_InvertedInterval_IsRejected()
    {
        var dispatcher = CreateDispatcher(out var queries, out _);
        queries.Setup(q => q.GetTechniciansAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new TechnicianReadModel("TECH-001", "Asha Rao", 42m, [],
                [new TechnicianAvailabilityReadModel(WindowStart, WindowStart)])
        ]);

        var result = await dispatcher.ExecuteAsync(TechnicianAvailabilityTool.ToolName, "{}", CancellationToken.None);

        AssertFailure(result, PlanningToolErrors.InvalidToolResult);
    }

    // Task catalogue

    [Fact]
    public async Task TaskCatalogue_ReturnsTheStoredCatalogue_ForTheStoredAssetId()
    {
        var dispatcher = CreateDispatcher(out var queries, out _);
        queries.Setup(q => q.GetTaskCatalogueAsync("P-101", It.IsAny<CancellationToken>()))
            .ReturnsAsync([CatalogueTask("P101-PREP", "P-101")]);

        var result = await dispatcher.ExecuteAsync(TaskCatalogueTool.ToolName, """{"assetId":"p-101"}""", CancellationToken.None);

        var data = Assert.IsType<TaskCatalogueToolResult>(AssertSuccess(result));
        Assert.Equal("P-101", data.AssetId);
        Assert.Equal("P101-PREP", Assert.Single(data.Tasks).Id);
    }

    [Fact]
    public async Task TaskCatalogue_UnknownAssetOrMissingCatalogue_IsUnknownAsset()
    {
        var dispatcher = CreateDispatcher(out var queries, out _);
        queries.Setup(q => q.GetTaskCatalogueAsync("P-101", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<MaintenanceTaskReadModel>?)null);

        AssertFailure(
            await dispatcher.ExecuteAsync(TaskCatalogueTool.ToolName, """{"assetId":"P-999"}""", CancellationToken.None),
            PlanningToolErrors.UnknownAsset);
        AssertFailure(
            await dispatcher.ExecuteAsync(TaskCatalogueTool.ToolName, """{"assetId":"P-101"}""", CancellationToken.None),
            PlanningToolErrors.UnknownAsset);
    }

    [Theory]
    [InlineData("P-102", 60, 1)]
    [InlineData("P-101", 0, 1)]
    [InlineData("P-101", 60, 0)]
    public async Task TaskCatalogue_InconsistentTaskRecord_IsRejected(string assetId, int durationMinutes, int crewSize)
    {
        var dispatcher = CreateDispatcher(out var queries, out _);
        queries.Setup(q => q.GetTaskCatalogueAsync("P-101", It.IsAny<CancellationToken>()))
            .ReturnsAsync([CatalogueTask("P101-PREP", assetId) with { DurationMinutes = durationMinutes, RequiredCrewSize = crewSize }]);

        var result = await dispatcher.ExecuteAsync(TaskCatalogueTool.ToolName, """{"assetId":"P-101"}""", CancellationToken.None);

        AssertFailure(result, PlanningToolErrors.InvalidToolResult);
    }

    [Fact]
    public void ReadTools_OfferAnObjectSchemaThatRejectsUnknownArguments()
    {
        var dispatcher = CreateDispatcher(out _, out _);

        Assert.Equal(
            [TaskCatalogueTool.ToolName, TechnicianAvailabilityTool.ToolName, StockLookupTool.ToolName],
            dispatcher.Tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.All(dispatcher.Tools, tool =>
        {
            Assert.Equal("object", tool.ParametersSchema.GetProperty("type").GetString());
            Assert.False(tool.ParametersSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        });
    }

    private static PlanningToolDispatcher CreateDispatcher(
        out Mock<IMaintenancePlannerQueries> queries,
        out Mock<ISparePartReadModelProvider> provider)
    {
        queries = new Mock<IMaintenancePlannerQueries>();
        queries.Setup(q => q.GetAssetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AssetReadModel("P-101", "PLANT-01", "Cooling Water Pump P-101", "CP-100", 1)]);
        queries.Setup(q => q.GetMaintenanceWindowsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new MaintenanceWindowReadModel("MW-P101-BASELINE-20261024", "Baseline", WindowStart, WindowStart.AddHours(8), "Asia/Kolkata"),
            new MaintenanceWindowReadModel("MW-P101-REDUCED-20261024", "Reduced", WindowStart, WindowStart.AddHours(4), "Asia/Kolkata")
        ]);
        provider = new Mock<ISparePartReadModelProvider>();

        // Setups above are not invocations; tests that assert no data was read start from a clean invocation list
        queries.Invocations.Clear();
        return new PlanningToolDispatcher(
        [
            new StockLookupTool(provider.Object, queries.Object),
            new TechnicianAvailabilityTool(queries.Object),
            new TaskCatalogueTool(queries.Object)
        ]);
    }

    private static MaintenanceTaskReadModel CatalogueTask(string id, string assetId) =>
        new(id, assetId, "Preparation", 60, "MECHANICAL", 1, null, 0m, null, "MAN-P101-001", "Preparation");

    private static object AssertSuccess(PlanningToolResult result)
    {
        Assert.True(result.Ok, $"{result.ErrorCode}: {result.ErrorMessage}");
        Assert.Null(result.ErrorCode);
        return result.Data!;
    }

    private static void AssertFailure(PlanningToolResult result, string errorCode)
    {
        Assert.False(result.Ok);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Null(result.Data);
    }

    private sealed class FakeTool(string name, Func<JsonElement, PlanningToolResult> execute) : IPlanningTool
    {
        public int Calls { get; private set; }
        public string Name => name;
        public string Description => "Fake tool";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public Task<PlanningToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(execute(arguments));
        }
    }
}
