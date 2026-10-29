using System.Text.Json;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT05ReplanningTests
{
    [Fact]
    public async Task AT05_LockCreatedOnApproval_AndPersistedInDatabase()
    {
        // AT05: Lock is created on approval and persisted
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithTaskPlacementsAsync(dbContext, jobId);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        var result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.True(result.Succeeded);
        var draftId = result.DraftId!.Value;

        // Verify locks are persisted
        await using (var dbContext = fixture.CreateDbContext())
        {
            var locks = await dbContext.LockedTaskAssignments
                .Where(l => l.DraftId == draftId)
                .ToListAsync();
            Assert.NotEmpty(locks);
            Assert.Contains(locks, l => l.TaskId == "P101-PREP" && l.LockedTechnicianId == "TECH-001");
            Assert.Contains(locks, l => l.TaskId == "P101-BEARING-REPLACEMENT" && l.LockedTechnicianId == "TECH-001");
        }
    }

    [Fact]
    public async Task AT05_HistoricalLocksPreserved_WhenNewDraftApproved()
    {
        // AT05: Historical locks are not deleted when new draft is approved
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        // First approval
        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithTaskPlacementsAsync(dbContext, jobId);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result1 = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.True(result1.Succeeded);
        var draftId1 = result1.DraftId!.Value;

        // Verify first draft has locks
        await using (var dbContext = fixture.CreateDbContext())
        {
            var locks1 = await dbContext.LockedTaskAssignments
                .Where(l => l.DraftId == draftId1)
                .ToListAsync();
            Assert.NotEmpty(locks1);
        }

        // Second approval (idempotent - same draft)
        // Historical locks should be preserved even when we approve again
        var result2 = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.True(result2.Succeeded);
        Assert.Equal(draftId1, result2.DraftId); // Idempotent: same draft

        // Verify historical locks are unchanged
        await using (var dbContext = fixture.CreateDbContext())
        {
            var locks1Still = await dbContext.LockedTaskAssignments
                .Where(l => l.DraftId == draftId1)
                .ToListAsync();

            Assert.NotEmpty(locks1Still); // Original draft locks not deleted
            Assert.Equal(3, locks1Still.Count); // Still the original 3 task locks
        }
    }

    [Fact]
    public async Task AT05_IdempotentApproval_DoesNotDuplicateLocks()
    {
        // AT05: Idempotent approvals don't create duplicate locks
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithTaskPlacementsAsync(dbContext, jobId);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Approve twice
        var result1 = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        var result2 = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        Assert.True(result1.Succeeded);
        Assert.True(result2.Succeeded);
        Assert.Equal(result1.DraftId, result2.DraftId); // Same draft

        // Verify locks are not duplicated
        await using (var dbContext = fixture.CreateDbContext())
        {
            // Materialize the locks first, then group in memory (EF Core can't translate GroupBy.Count() to SQL)
            var locks = await dbContext.LockedTaskAssignments
                .Where(l => l.DraftId == result1.DraftId!.Value)
                .ToListAsync();

            var duplicateTasks = locks
                .GroupBy(l => l.TaskId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.Empty(duplicateTasks); // No duplicate task locks
        }
    }

    // Approval revalidates the candidate, so the job carries a schedulable P-101 plan in the baseline window:
    // PREP -> TECH-001, BEARING-REPLACEMENT -> TECH-001 + TECH-002, VERIFICATION -> TECH-003.
    // Locks take the first technician of each placement.
    private static Task CreatePlanningJobWithTaskPlacementsAsync(
        MaintenancePlannerDbContext dbContext,
        Guid jobId) =>
        ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId, null, new
        {
            taskPlacements = ApprovalTestSupport.StableIdPlacements(),
            rationale = "Test candidate for AT05",
            evidenceReferences = Array.Empty<object>(),
            requestedTaskIds = new[] { "P101-PREP", "P101-BEARING-REPLACEMENT", "P101-VERIFICATION" },
            dependencyTaskIds = Array.Empty<string>()
        });

    private sealed class ApprovalTestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private ApprovalTestFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            WebApplication app)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            _app = app;
            ServiceProvider = app.Services;
        }

        public IServiceProvider ServiceProvider { get; }

        public static async Task<ApprovalTestFixture> CreateAsync()
        {
            var connectionString = $"Data Source=AT05-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();

            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(keeperConnection)
                .Options;

            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
            builder.Services.AddApprovalServices();

            var app = builder.Build();

            return new ApprovalTestFixture(keeperConnection, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            await _keeperConnection.DisposeAsync();
        }
    }
}
