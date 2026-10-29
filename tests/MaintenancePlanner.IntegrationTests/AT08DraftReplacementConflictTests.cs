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

        // Step 2: Draft V2 as a replacement planning job of V1, then force a reservation conflict.
        // V2 may reuse the unit V1 releases, so the conflict is a stock correction that removes the only BR-101.
        var v2JobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, v2JobId, originalJobId: jobId);
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            br101.OnHandQuantity = 0m;
            await dbContext.SaveChangesAsync();
        }

        // Step 3: Approve V2; the replacement needs 1 BR-101 but 0 exist even after releasing V1's unit
        var v2Result = await approvalService.ApproveDraftReplacementAsync(v2JobId, CancellationToken.None);

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

        // Step 5: Verify no V2 draft was created and V1 is the only approved draft
        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.False(await dbContext.Drafts.AnyAsync(d => d.PlanningJobId == v2JobId));

            var approvedDrafts = await dbContext.Drafts
                .Where(d => d.Status == DraftStatus.Approved)
                .ToListAsync();
            Assert.Single(approvedDrafts); // Only one approved draft (V1)
            Assert.Equal(v1DraftId, approvedDrafts[0].Id);
        }

        // Step 6: Verify inventory and reservations consistency
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(0m, br101.OnHandQuantity); // Stock correction applied
            Assert.Equal(1m, br101.ReservedQuantity); // Only V1's reservation

            var allConfirmedReservations = await dbContext.Reservations
                .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Single(allConfirmedReservations); // Only V1's reservation confirmed
            Assert.Equal(v1DraftId, allConfirmedReservations[0].DraftId);
        }
    }

    // Approval revalidates the candidate, so the job carries a schedulable P-101 candidate requiring one BR-101.
    private static Task CreatePlanningJobWithBR101RequirementAsync(
        MaintenancePlannerDbContext dbContext,
        Guid jobId) =>
        ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId);

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
            services.AddApprovalServices();

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
