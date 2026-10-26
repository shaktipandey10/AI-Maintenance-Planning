using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MaintenancePlanner.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    private const string BaselineMigrationId = "20261022083910_InitialSchemaBaseline";
    private const string BaselineProductVersion = "10.0.13";

    public static async Task InitializeAsync(
        MaintenancePlannerDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        await AdoptExistingSchemaAsync(dbContext, cancellationToken);
        await dbContext.Database.MigrateAsync(cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var seed = P101AnchorSeed.Create();
        if (!await dbContext.Assets.AnyAsync(asset => asset.Id == seed.Asset.Id, cancellationToken))
        {
            await dbContext.Assets.AddAsync(seed.Asset, cancellationToken);
        }

        if (!await dbContext.SpareParts.AnyAsync(part => part.Id == seed.SparePart.Id, cancellationToken))
            await dbContext.SpareParts.AddAsync(seed.SparePart, cancellationToken);

        foreach (var task in seed.Tasks)
        {
            if (!await dbContext.MaintenanceTaskDefinitions.AnyAsync(existing => existing.Id == task.Id, cancellationToken))
                await dbContext.MaintenanceTaskDefinitions.AddAsync(task, cancellationToken);
        }

        foreach (var technician in seed.Technicians)
        {
            if (!await dbContext.Technicians.AnyAsync(existing => existing.Id == technician.Id, cancellationToken))
                await dbContext.Technicians.AddAsync(technician, cancellationToken);
        }

        foreach (var window in seed.MaintenanceWindows)
        {
            if (!await dbContext.MaintenanceWindows.AnyAsync(existing => existing.Id == window.Id, cancellationToken))
                await dbContext.MaintenanceWindows.AddAsync(window, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var part = await dbContext.SpareParts.SingleAsync(
            existing => existing.Id == seed.SparePart.Id,
            cancellationToken);
        var hasCompatibility = await dbContext.Entry(part)
            .Collection(existing => existing.CompatibleAssets)
            .Query()
            .AnyAsync(asset => asset.Id == seed.Asset.Id, cancellationToken);

        if (!hasCompatibility)
        {
            var asset = await dbContext.Assets.SingleAsync(
                existing => existing.Id == seed.Asset.Id,
                cancellationToken);
            part.CompatibleAssets.Add(asset);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task AdoptExistingSchemaAsync(
        MaintenancePlannerDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN (
                    'Assets',
                    'MaintenanceTaskDefinitions',
                    'MaintenanceWindows',
                    'SpareParts',
                    'Technicians',
                    'TechnicalSkills',
                    'TechnicianAvailabilities'
                  );
                """;

            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 7)
                return;

            command.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = '__EFMigrationsHistory';
                """;

            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 0)
                return;

            command.CommandText = """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ($migrationId, $productVersion);
                """;
            var migrationId = command.CreateParameter();
            migrationId.ParameterName = "$migrationId";
            migrationId.Value = BaselineMigrationId;
            command.Parameters.Add(migrationId);

            var productVersion = command.CreateParameter();
            productVersion.ParameterName = "$productVersion";
            productVersion.Value = BaselineProductVersion;
            command.Parameters.Add(productVersion);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }
}