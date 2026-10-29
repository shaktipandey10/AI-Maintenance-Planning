using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Planning.Tools;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>Every AI03 planning tool over the real query services and a seeded in-memory SQLite database.</summary>
internal sealed class AI03ToolFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private AI03ToolFixture(SqliteConnection connection, MaintenancePlannerDbContext dbContext)
    {
        _connection = connection;
        DbContext = dbContext;
        Queries = new MaintenancePlannerQueries(dbContext);
        Provider = new ScenarioAwareSparePartReadModelProvider(Queries);
        Validator = new PlanningCandidateValidator(Provider, Queries);
        Proposals = new ModelCandidateProposals();
        Dispatcher = new PlanningToolDispatcher(
        [
            new StockLookupTool(Provider, Queries),
            new TechnicianAvailabilityTool(Queries),
            new TaskCatalogueTool(Queries),
            new ScheduleValidationTool(Validator, Queries),
            new CostCalculationTool(Validator, new CandidateCostCalculator(), Queries),
            new SaveCandidateTool(Proposals, Validator, new CandidateCostCalculator(), Queries)
        ]);
    }

    public ModelCandidateProposals Proposals { get; }

    public MaintenancePlannerDbContext DbContext { get; }
    public MaintenancePlannerQueries Queries { get; }
    public ScenarioAwareSparePartReadModelProvider Provider { get; }
    public PlanningCandidateValidator Validator { get; }
    public PlanningToolDispatcher Dispatcher { get; }

    public static async Task<AI03ToolFixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(connection).Options;
        var dbContext = new MaintenancePlannerDbContext(options);
        await DatabaseInitializer.InitializeAsync(dbContext);
        return new AI03ToolFixture(connection, dbContext);
    }

    /// <summary>Row counts of every table a planning or approval write would touch, plus each part's stock.</summary>
    public async Task<string> SnapshotAsync()
    {
        var stock = await DbContext.SpareParts.AsNoTracking()
            .OrderBy(part => part.Id)
            .Select(part => $"{part.Id}:{part.OnHandQuantity}/{part.ReservedQuantity}")
            .ToListAsync();
        return string.Join(";",
            $"jobs={await DbContext.PlanningJobs.CountAsync()}",
            $"drafts={await DbContext.Drafts.CountAsync()}",
            $"reservations={await DbContext.Reservations.CountAsync()}",
            $"locks={await DbContext.LockedTaskAssignments.CountAsync()}",
            $"edits={await DbContext.EditedTaskPlacements.CountAsync()}",
            $"progress={await DbContext.DraftTaskProgress.CountAsync()}",
            $"actions={await DbContext.BusinessActionRecords.CountAsync()}",
            $"stock={string.Join(",", stock)}");
    }

    public async ValueTask DisposeAsync()
    {
        await DbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
