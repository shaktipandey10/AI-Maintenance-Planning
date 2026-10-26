using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT08DraftReplacementConflictTests
{
    [Fact]
    public async Task AT08_ReplacementDraftV2FailsOnConflict_V1RemainsApprovedAndUnchanged()
    {
        // AT08: Create and approve V1, then create V2 replacement that fails on conflict.
        // V1 remains approved with original reservations intact.
        // V2 is not approved and has no confirmed reservations.
        // Inventory remains consistent.

        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        // Setup: Create planning job requiring BR-101 with quantity 1
        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithBR101RequirementAsync(dbContext, jobId);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Step 1: Approve V1 successfully
        var v1Result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.True(v1Result.Succeeded);
        var v1DraftId = v1Result.DraftId!.Value;

        // Verify V1 state
        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1Draft = await dbContext.Drafts.SingleAsync(d => d.Id == v1DraftId);
            Assert.Equal(DraftStatus.Approved, v1Draft.Status);
            Assert.NotNull(v1Draft.ApprovedAtUtc);

            var v1Reservations = await dbContext.Reservations
                .Where(r => r.DraftId == v1DraftId && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Single(v1Reservations);
            Assert.Equal("BR-101", v1Reservations[0].SparePartId);
            Assert.Equal(1m, v1Reservations[0].QuantityReserved);

            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.ReservedQuantity); // V1's reservation is confirmed
            Assert.Equal(1m, br101.OnHandQuantity); // Total on hand (1 unit seeded)
        }

        // Step 2: Reduce inventory to force V2 approval to fail
        // Reduce BR-101 on-hand from 10 to 1 (leaving 0 available since V1 reserves 1)
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            br101.OnHandQuantity = 1m; // Only 1 left, but 1 is already reserved for V1
            await dbContext.SaveChangesAsync();
        }

        // Step 3: Create V2 approval (replacement draft for same job)
        // This should fail because total reservations (V1=1 + V2=1) exceed available (0)
        var v2Result = await approvalService.ApproveDraftReplacementAsync(jobId, CancellationToken.None);

        // V2 should fail with conflict
        Assert.False(v2Result.Succeeded);
        Assert.NotNull(v2Result.ConflictData);
        Assert.Equal("SparePart", v2Result.ConflictData.ResourceType);
        Assert.Equal("INSUFFICIENT_INVENTORY", v2Result.ConflictData.Reason);
        Assert.Equal("BR-101", v2Result.ConflictData.ResourceId);

        // Step 4: Verify V1 remains approved and unchanged
        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1Draft = await dbContext.Drafts.SingleAsync(d => d.Id == v1DraftId);
            Assert.Equal(DraftStatus.Approved, v1Draft.Status);
            Assert.NotNull(v1Draft.ApprovedAtUtc);

            var v1Reservations = await dbContext.Reservations
                .Where(r => r.DraftId == v1DraftId && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Single(v1Reservations); // Still exactly 1 reservation
            Assert.Equal(1m, v1Reservations[0].QuantityReserved); // Quantity unchanged
        }

        // Step 5: Verify no new draft was created (or if created, it's not approved)
        await using (var dbContext = fixture.CreateDbContext())
        {
            var allDrafts = await dbContext.Drafts
                .Where(d => d.PlanningJobId == jobId)
                .ToListAsync();

            // Either still only V1, or V2 exists but is not approved
            var approvedDrafts = allDrafts.Where(d => d.Status == DraftStatus.Approved).ToList();
            Assert.Single(approvedDrafts); // Only one approved draft (V1)
            Assert.Equal(v1DraftId, approvedDrafts[0].Id);
        }

        // Step 6: Verify inventory and reservations consistency
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.OnHandQuantity); // Unchanged
            Assert.Equal(1m, br101.ReservedQuantity); // Only V1's reservation

            var allConfirmedReservations = await dbContext.Reservations
                .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Single(allConfirmedReservations); // Only V1's reservation confirmed
            Assert.Equal(v1DraftId, allConfirmedReservations[0].DraftId);
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

        private ApprovalTestFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            IServiceProvider serviceProvider)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            ServiceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider { get; }

        public static async Task<ApprovalTestFixture> CreateAsync()
        {
            var connectionString =
                $"Data Source=AT08-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();
            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            var services = new ServiceCollection();
            services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
            services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            services.AddScoped<IDraftApprovalService, DraftApprovalService>();

            var serviceProvider = services.BuildServiceProvider();

            return new ApprovalTestFixture(keeperConnection, connectionString, serviceProvider);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        public async ValueTask DisposeAsync()
        {
            await _keeperConnection.DisposeAsync();
        }
    }
}
