using MaintenancePlanner.Application.Planning.Tools;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>AI03 stage 1: the read-only tools against seeded SQLite data, through the real query services.</summary>
public sealed class AI03ReadToolsTests
{
    [Fact]
    public async Task StockLookup_ReturnsWhatTheWindowAwareProviderReturns_IncludingTheReducedWindowInventory()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();

        var baseline = Assert.IsType<StockLookupResult>((await fixture.Dispatcher.ExecuteAsync(
            StockLookupTool.ToolName, """{"windowId":"MW-P101-BASELINE-20261024","assetId":"P-101"}""", CancellationToken.None)).Data);
        var reduced = Assert.IsType<StockLookupResult>((await fixture.Dispatcher.ExecuteAsync(
            StockLookupTool.ToolName, """{"windowId":"MW-P101-REDUCED-20261024","partIds":["BR-101"]}""", CancellationToken.None)).Data);

        Assert.Equal(
            await fixture.Provider.GetCompatibleSparePartsAsync("P-101", "MW-P101-BASELINE-20261024", CancellationToken.None),
            baseline.Parts);
        Assert.Equal(1m, Assert.Single(baseline.Parts).AvailableQuantity);
        Assert.Equal(
            await fixture.Provider.GetSparePartsAsync("MW-P101-REDUCED-20261024", CancellationToken.None),
            reduced.Parts);
        Assert.Equal(0m, Assert.Single(reduced.Parts).AvailableQuantity);
    }

    [Fact]
    public async Task TechnicianAvailability_ReturnsTheSeededTechnicians_WithTheCommittedTimeLimitation()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();

        var result = await fixture.Dispatcher.ExecuteAsync(
            TechnicianAvailabilityTool.ToolName, """{"windowId":"MW-P101-BASELINE-20261024"}""", CancellationToken.None);

        var data = Assert.IsType<TechnicianAvailabilityToolResult>(result.Data);
        var expected = await fixture.Queries.GetTechniciansAsync(CancellationToken.None);
        Assert.Equal(expected.Select(t => t.Id), data.Technicians.Select(t => t.Id));
        Assert.All(data.Technicians, technician =>
        {
            var source = expected.Single(t => t.Id == technician.Id);
            Assert.Equal(source.Skills, technician.Skills);
            Assert.Equal(source.Availability, technician.DeclaredAvailability);
        });
        Assert.Equal(TechnicianAvailabilityTool.Limitation, data.Limitation);
    }

    [Fact]
    public async Task TaskCatalogue_ReturnsTheStoredCatalogueInPredecessorOrder()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();

        var result = await fixture.Dispatcher.ExecuteAsync(
            TaskCatalogueTool.ToolName, """{"assetId":"P-101"}""", CancellationToken.None);

        var data = Assert.IsType<TaskCatalogueToolResult>(result.Data);
        Assert.Equal(await fixture.Queries.GetTaskCatalogueAsync("P-101", CancellationToken.None), data.Tasks);
        Assert.Equal(["P101-PREP", "P101-BEARING-REPLACEMENT", "P101-VERIFICATION"], data.Tasks.Select(t => t.Id));
    }

    [Theory]
    [InlineData(TaskCatalogueTool.ToolName, """{"assetId":"P-999"}""", PlanningToolErrors.UnknownAsset)]
    [InlineData(StockLookupTool.ToolName, """{"assetId":"P-101","partIds":["PART-UNKNOWN"]}""", PlanningToolErrors.UnknownPart)]
    [InlineData(StockLookupTool.ToolName, """{"windowId":"MW-UNKNOWN"}""", PlanningToolErrors.UnknownWindow)]
    [InlineData(TechnicianAvailabilityTool.ToolName, """{"technicianIds":["TECH-404"]}""", PlanningToolErrors.UnknownTechnician)]
    public async Task UnknownIdentifiers_AreReportedAsErrors_AgainstRealData(string toolName, string arguments, string errorCode)
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();

        var result = await fixture.Dispatcher.ExecuteAsync(toolName, arguments, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(errorCode, result.ErrorCode);
    }

    [Fact]
    public async Task RunningEveryTool_IncludingFailingCalls_WritesNoDraftReservationLockOrBusinessAction()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();
        var before = await fixture.SnapshotAsync();

        string[][] calls =
        [
            [StockLookupTool.ToolName, "{}"],
            [StockLookupTool.ToolName, """{"windowId":"MW-P101-REDUCED-20261024","assetId":"P-101","partIds":["BR-101"]}"""],
            [StockLookupTool.ToolName, """{"partIds":["PART-UNKNOWN"]}"""],
            [TechnicianAvailabilityTool.ToolName, "{}"],
            [TechnicianAvailabilityTool.ToolName, """{"technicianIds":["TECH-001"],"windowId":"MW-P101-BASELINE-20261024"}"""],
            [TaskCatalogueTool.ToolName, """{"assetId":"P-101"}"""],
            [TaskCatalogueTool.ToolName, """{"assetId":"P-999"}"""],
            [TaskCatalogueTool.ToolName, "{not json"],
            ["approve_draft", """{"planningJobId":"00000000-0000-0000-0000-000000000001"}"""]
        ];
        foreach (var call in calls)
            await fixture.Dispatcher.ExecuteAsync(call[0], call[1], CancellationToken.None);

        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.False(fixture.DbContext.ChangeTracker.HasChanges());
    }
}
