using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.IntegrationTests;

public sealed class P101SqliteSeedTests
{
    [Fact]
    public async Task InitializeAsync_PersistsP101AnchorDataExactlyOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var dbContext = new MaintenancePlannerDbContext(options);

        await DatabaseInitializer.InitializeAsync(dbContext);
        await DatabaseInitializer.InitializeAsync(dbContext);

        var asset = await dbContext.Assets.SingleAsync();
        var part = await dbContext.SpareParts.SingleAsync();
        var tasks = await dbContext.MaintenanceTaskDefinitions
            .OrderBy(task => task.DurationMinutes)
            .ToListAsync();

        Assert.Equal("P-101", asset.Id);
        Assert.Equal("BR-101", part.Id);
        Assert.Equal(1m, part.OnHandQuantity);
        Assert.Equal(3, tasks.Count);
        Assert.Equal(new[] { 60, 120, 180 }, tasks.Select(task => task.DurationMinutes));
        Assert.Equal("P101-PREP", tasks.Single(task => task.Id == "P101-BEARING-REPLACEMENT").PredecessorTaskId);
        Assert.Equal("P101-BEARING-REPLACEMENT", tasks.Single(task => task.Id == "P101-VERIFICATION").PredecessorTaskId);
        Assert.Equal("BR-101", tasks.Single(task => task.Id == "P101-BEARING-REPLACEMENT").RequiredPartId);
    }

    [Fact]
    public async Task InitializeAsync_UpgradesExistingP101DatabaseWithoutDuplicatingSeedData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var dbContext = new MaintenancePlannerDbContext(options);
        await dbContext.Database.MigrateAsync("20261022083910_InitialSchemaBaseline");

        var seed = P101AnchorSeed.Create();
        await dbContext.Assets.AddAsync(seed.Asset);
        await dbContext.SpareParts.AddAsync(seed.SparePart);
        await dbContext.MaintenanceTaskDefinitions.AddRangeAsync(seed.Tasks);
        await dbContext.Technicians.AddRangeAsync(seed.Technicians);
        await dbContext.MaintenanceWindows.AddRangeAsync(seed.MaintenanceWindows);
        await dbContext.SaveChangesAsync();
        await dbContext.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");

        await DatabaseInitializer.InitializeAsync(dbContext);

        Assert.Equal(1, await dbContext.Assets.CountAsync());
        Assert.Equal(1, await dbContext.SpareParts.CountAsync());
        Assert.Equal(3, await dbContext.MaintenanceTaskDefinitions.CountAsync());
        Assert.Equal(3, await dbContext.Technicians.CountAsync());
        Assert.Equal(6, await dbContext.TechnicalSkills.CountAsync());
        Assert.Equal(2, await dbContext.MaintenanceWindows.CountAsync());

        var compatiblePart = await dbContext.SpareParts
            .Include(part => part.CompatibleAssets)
            .SingleAsync(part => part.Id == "BR-101");
        Assert.Equal("P-101", Assert.Single(compatiblePart.CompatibleAssets).Id);

        var historyRecord = new MaintenanceHistoryRecord
        {
            Id = "HISTORY-MIGRATION-TEST",
            AssetId = "P-101",
            PerformedAt = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(5.5)),
            Summary = "Synthetic history row for migration persistence test.",
            Outcome = "Completed",
            SourceDocumentId = "HIST-P101-TEST",
            SourceReference = "Record 1"
        };
        await dbContext.MaintenanceHistoryRecords.AddAsync(historyRecord);
        await dbContext.SaveChangesAsync();

        var persistedHistory = await dbContext.MaintenanceHistoryRecords
            .Include(record => record.Asset)
            .SingleAsync(record => record.Id == historyRecord.Id);
        Assert.Equal("P-101", persistedHistory.Asset.Id);
    }
}
