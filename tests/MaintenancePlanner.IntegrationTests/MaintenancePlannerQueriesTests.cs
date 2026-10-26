using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.IntegrationTests;

public sealed class MaintenancePlannerQueriesTests
{
    [Fact]
    public async Task GetMaintenanceHistoryAsync_ReturnsRequestedAssetsAndOrdersDeterministically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var dbContext = new MaintenancePlannerDbContext(options);
        await DatabaseInitializer.InitializeAsync(dbContext);
        dbContext.Assets.Add(new Asset
        {
            Id = "P-OTHER",
            PlantId = "PLANT-01",
            Name = "Other asset",
            Model = "OTHER",
            Priority = 2
        });
        dbContext.MaintenanceHistoryRecords.AddRange(
            CreateHistory("HISTORY-B", "P-101", new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)),
            CreateHistory("HISTORY-A", "P-101", new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)),
            CreateHistory("HISTORY-NEW", "P-101", new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero)),
            CreateHistory("HISTORY-OTHER", "P-OTHER", new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero)));
        await dbContext.SaveChangesAsync();

        var queries = new MaintenancePlannerQueries(dbContext);
        var history = await queries.GetMaintenanceHistoryAsync(["P-101", "UNKNOWN"], CancellationToken.None);

        Assert.Equal(["HISTORY-NEW", "HISTORY-A", "HISTORY-B"], history.Select(record => record.Id));
        Assert.All(history, record => Assert.Equal("P-101", record.AssetId));
        Assert.Equal("Summary HISTORY-A", history[1].Summary);
        Assert.Equal("Outcome HISTORY-A", history[1].Outcome);
        Assert.Equal("DOC-HISTORY-A", history[1].SourceDocumentId);
        Assert.Equal("Reference HISTORY-A", history[1].SourceReference);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero),
            history[0].PerformedAt);
    }

    [Fact]
    public async Task GetMaintenanceHistoryAsync_EmptyOrUnknownAssetIds_ReturnsEmpty()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var dbContext = new MaintenancePlannerDbContext(options);
        await DatabaseInitializer.InitializeAsync(dbContext);
        var queries = new MaintenancePlannerQueries(dbContext);

        Assert.Empty(await queries.GetMaintenanceHistoryAsync([], CancellationToken.None));
        Assert.Empty(await queries.GetMaintenanceHistoryAsync(["UNKNOWN"], CancellationToken.None));
    }

    [Fact]
    public async Task GetCompatibleSparePartsAsync_ReturnsOnlyPersistedCompatibleParts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var dbContext = new MaintenancePlannerDbContext(options);
        await DatabaseInitializer.InitializeAsync(dbContext);
        var otherAsset = new Asset
        {
            Id = "P-OTHER",
            PlantId = "PLANT-01",
            Name = "Other asset",
            Model = "OTHER",
            Priority = 2
        };
        var otherPart = new SparePart
        {
            Id = "PART-OTHER",
            Name = "Other compatible part",
            Unit = "each",
            OnHandQuantity = 4
        };
        otherPart.CompatibleAssets.Add(otherAsset);
        dbContext.Assets.Add(otherAsset);
        dbContext.SpareParts.Add(otherPart);
        await dbContext.SaveChangesAsync();

        var queries = new MaintenancePlannerQueries(dbContext);
        var p101Parts = await queries.GetCompatibleSparePartsAsync("P-101", CancellationToken.None);
        var otherParts = await queries.GetCompatibleSparePartsAsync("P-OTHER", CancellationToken.None);

        Assert.Equal("BR-101", Assert.Single(p101Parts).Id);
        Assert.Equal("PART-OTHER", Assert.Single(otherParts).Id);
        Assert.Empty(await queries.GetCompatibleSparePartsAsync("UNKNOWN", CancellationToken.None));
    }

    private static MaintenanceHistoryRecord CreateHistory(string id, string assetId, DateTimeOffset performedAt) =>
        new()
        {
            Id = id,
            AssetId = assetId,
            PerformedAt = performedAt,
            Summary = $"Summary {id}",
            Outcome = $"Outcome {id}",
            SourceDocumentId = $"DOC-{id}",
            SourceReference = $"Reference {id}"
        };
}
