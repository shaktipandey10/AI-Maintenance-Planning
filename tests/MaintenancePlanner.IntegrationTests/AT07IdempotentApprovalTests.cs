using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT07IdempotentApprovalTests
{
    [Fact]
    public async Task AT07_RepeatApprovalIsIdempotent_NoDuplicateDraftOrReservation()
    {
        // AT07: Repeat approval is idempotent - same draft ID returned, no duplicate reservation
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithBR101RequirementAsync(dbContext, jobId);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // First approval
        var result1 = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.True(result1.Succeeded);
        Assert.NotNull(result1.DraftId);
        var draftId = result1.DraftId.Value;

        // Verify state after first approval
        await using (var dbContext = fixture.CreateDbContext())
        {
            var drafts = await dbContext.Drafts.ToListAsync();
            Assert.Single(drafts);
            Assert.Equal(draftId, drafts[0].Id);
            Assert.Equal(DraftStatus.Approved, drafts[0].Status);

            var reservations = await dbContext.Reservations
                .Where(r => r.DraftId == draftId && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Single(reservations);
            Assert.Equal("BR-101", Assert.Single(reservations).SparePartId);
            Assert.Equal(1m, Assert.Single(reservations).QuantityReserved);

            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.ReservedQuantity);
        }

        // Second approval (repeat) - should be idempotent
        var result2 = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.True(result2.Succeeded);
        Assert.Equal(draftId, result2.DraftId); // Same draft ID returned

        // Verify no duplicates after repeat approval
        await using (var dbContext = fixture.CreateDbContext())
        {
            var drafts = await dbContext.Drafts.ToListAsync();
            Assert.Single(drafts); // Still only one draft
            Assert.Equal(draftId, drafts[0].Id);

            var reservations = await dbContext.Reservations
                .Where(r => r.DraftId == draftId && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Single(reservations); // Still only one reservation
            Assert.Equal(1m, Assert.Single(reservations).QuantityReserved);

            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.ReservedQuantity); // Inventory not reserved twice
        }

        // Third approval - verify idempotency holds
        var result3 = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.True(result3.Succeeded);
        Assert.Equal(draftId, result3.DraftId);

        await using (var dbContext = fixture.CreateDbContext())
        {
            var drafts = await dbContext.Drafts.ToListAsync();
            Assert.Single(drafts);

            var reservations = await dbContext.Reservations
                .Where(r => r.DraftId == draftId && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Single(reservations);

            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.ReservedQuantity);
        }
    }

    [Fact]
    public async Task AT07_RepeatCancellationIsIdempotent_InventoryReleasedOnce()
    {
        // AT07: Repeat cancellation is idempotent - inventory released exactly once
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithBR101RequirementAsync(dbContext, jobId);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Approve draft
        var approvalResult = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.True(approvalResult.Succeeded);
        var draftId = approvalResult.DraftId.Value;

        // Verify reservation before cancellation
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101Before = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101Before.ReservedQuantity);
        }

        // First cancellation
        var cancelResult1 = await approvalService.CancelDraftAsync(draftId, CancellationToken.None);
        Assert.True(cancelResult1.Succeeded);

        // Verify inventory released after first cancellation
        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.SingleAsync(d => d.Id == draftId);
            Assert.Equal(DraftStatus.Cancelled, draft.Status);

            var reservations = await dbContext.Reservations
                .Where(r => r.DraftId == draftId)
                .ToListAsync();
            Assert.All(reservations, r => Assert.Equal(ReservationStatus.Cancelled, r.Status));

            var br101After = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(0m, br101After.ReservedQuantity); // Released to 0
        }

        // Second cancellation (repeat) - should be idempotent
        var cancelResult2 = await approvalService.CancelDraftAsync(draftId, CancellationToken.None);
        Assert.True(cancelResult2.Succeeded); // Still succeeds

        // Verify inventory not released twice
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101AfterRepeat = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(0m, br101AfterRepeat.ReservedQuantity); // Still 0, not negative
        }

        // Third cancellation - verify idempotency holds
        var cancelResult3 = await approvalService.CancelDraftAsync(draftId, CancellationToken.None);
        Assert.True(cancelResult3.Succeeded);

        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101Final = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(0m, br101Final.ReservedQuantity);
        }
    }

    [Fact]
    public async Task AT07_ApprovalAndCancellationSequence_InventoryConsistent()
    {
        // AT07: Approve, cancel, verify inventory consistency across sequence
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithBR101RequirementAsync(dbContext, jobId);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Initial state
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.OnHandQuantity);
            Assert.Equal(0m, br101.ReservedQuantity);
            Assert.Equal(1m, br101.AvailableQuantity);
        }

        // Approve
        var approvalResult = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.True(approvalResult.Succeeded);
        var draftId = approvalResult.DraftId.Value;

        // After approval
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.OnHandQuantity);
            Assert.Equal(1m, br101.ReservedQuantity);
            Assert.Equal(0m, br101.AvailableQuantity);
        }

        // Cancel
        var cancelResult = await approvalService.CancelDraftAsync(draftId, CancellationToken.None);
        Assert.True(cancelResult.Succeeded);

        // After cancellation
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.OnHandQuantity);
            Assert.Equal(0m, br101.ReservedQuantity);
            Assert.Equal(1m, br101.AvailableQuantity);
        }

        // Approve again after cancellation (new draft)
        var reapprovalResult = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.False(reapprovalResult.Succeeded);
        Assert.Contains("cancelled", reapprovalResult.FailureReason, StringComparison.OrdinalIgnoreCase);

        // Cannot re-approve cancelled draft
        await using (var dbContext = fixture.CreateDbContext())
        {
            var drafts = await dbContext.Drafts.ToListAsync();
            Assert.Single(drafts); // Still only the original draft
            Assert.Equal(DraftStatus.Cancelled, Assert.Single(drafts).Status);
        }
    }

    [Fact]
    public async Task AT07_MultipleApprovalsAndCancellations_StatusConsistent()
    {
        // AT07: Multiple operations maintain consistent status
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithBR101RequirementAsync(dbContext, jobId);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Approve 5 times
        Guid? draftId = null;
        for (int i = 0; i < 5; i++)
        {
            var result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
            Assert.True(result.Succeeded);
            if (draftId is null)
                draftId = result.DraftId;
            else
                Assert.Equal(draftId, result.DraftId); // Always same draft
        }

        Assert.NotNull(draftId);

        // Verify draft status after multiple approvals
        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.SingleAsync(d => d.Id == draftId.Value);
            Assert.Equal(DraftStatus.Approved, draft.Status);

            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.ReservedQuantity); // Still 1, not 5
        }

        // Cancel 3 times
        for (int i = 0; i < 3; i++)
        {
            var result = await approvalService.CancelDraftAsync(draftId.Value, CancellationToken.None);
            Assert.True(result.Succeeded);
        }

        // Verify draft status after multiple cancellations
        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.SingleAsync(d => d.Id == draftId.Value);
            Assert.Equal(DraftStatus.Cancelled, draft.Status);

            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(0m, br101.ReservedQuantity); // Released, not negative
        }
    }

    private static async Task CreatePlanningJobWithBR101RequirementAsync(
        MaintenancePlannerDbContext dbContext,
        Guid jobId)
    {
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = """{"requestText":"Plan bearing replacement for P-101.","assetIds":["P-101"]}""",
            Status = Domain.Entities.PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ResultPayload = $$"""
                {
                  "outcome": "CandidatesGenerated",
                  "result": {
                    "candidates": [
                      {
                        "taskPlacements": [],
                        "rationale": "Test candidate requiring BR-101",
                        "evidenceReferences": [],
                        "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                        "dependencyTaskIds": []
                      }
                    ],
                    "assumptions": [],
                    "evidenceReferences": [],
                    "interpretation": {
                      "maintenanceIntent": "Replace bearing",
                      "scope": "P-101",
                      "proposedAssetIds": ["P-101"],
                      "proposedTaskIds": ["P101-BEARING-REPLACEMENT"],
                      "missingInformation": [],
                      "assumptions": [],
                      "scopeResolution": "Resolved",
                      "scopeMappings": []
                    }
                  }
                }
                """
        };
        await dbContext.PlanningJobs.AddAsync(planningJob);
        await dbContext.SaveChangesAsync();
    }

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
            var connectionString =
                $"Data Source=AT07-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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
            builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            builder.Services.AddScoped<IDraftApprovalService, DraftApprovalService>();

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
