using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// F03: the V1 → V2 replacement lifecycle on a temporary file-backed SQLite database seeded with the single BR-101
/// unit. V1 is the stable-ID plan (TECH-001 prepares and leads the bearing replacement); V2 is the lowest-cost plan
/// (TECH-003 prepares, TECH-002 leads the bearing replacement), so V1's and V2's locks are distinguishable.
/// </summary>
public sealed class F03ReplacementLifecycleFileDatabaseTests
{
    [Fact]
    public async Task SuccessfulReplacement_KeepsV1HistoricalLocks_AndAddsV2Locks()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var (v1JobId, v2JobId) = await fixture.AddV1AndV2JobsAsync();
        var v1 = await fixture.ApproveAsync(v1JobId);
        Assert.True(v1.Succeeded, v1.FailureReason);
        var v1LocksBefore = (await fixture.LocksAsync(v1.DraftId!.Value)).Select(FileDatabaseFixture.Describe).ToList();

        var v2 = await fixture.ApproveAsync(v2JobId);

        Assert.True(v2.Succeeded, v2.FailureReason);
        Assert.Equal(3, v1LocksBefore.Count);
        Assert.Equal(v1LocksBefore, (await fixture.LocksAsync(v1.DraftId.Value)).Select(FileDatabaseFixture.Describe));
        Assert.Equal(
            ["P101-BEARING-REPLACEMENT:TECH-002", "P101-PREP:TECH-003", "P101-VERIFICATION:TECH-003"],
            (await fixture.LocksAsync(v2.DraftId!.Value)).Select(l => $"{l.TaskId}:{l.LockedTechnicianId}"));
        await using var dbContext = fixture.CreateDbContext();
        Assert.Equal(6, await dbContext.LockedTaskAssignments.CountAsync());
        Assert.Equal(DraftStatus.Superseded, (await dbContext.Drafts.SingleAsync(d => d.Id == v1.DraftId)).Status);
        Assert.Equal(DraftStatus.Approved, (await dbContext.Drafts.SingleAsync(d => d.Id == v2.DraftId)).Status);
    }

    [Fact]
    public async Task ReplacementOfV1WithConsumedPart_KeepsConsumedReservation_AndNeedsAFreshUnitForV2()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var (v1JobId, v2JobId) = await fixture.AddV1AndV2JobsAsync();
        var v1 = await fixture.ApproveAsync(v1JobId);
        Assert.True(v1.Succeeded, v1.FailureReason);
        await using (var dbContext = fixture.CreateDbContext())
        {
            var completion = await new PlanProgressService(dbContext)
                .MarkTaskCompleteAsync(v1.DraftId!.Value, "BR-101", CancellationToken.None);
            Assert.True(completion.Succeeded, completion.FailureReason);
        }

        var consumedReservation = await fixture.SingleReservationAsync(v1.DraftId!.Value);
        Assert.NotNull(consumedReservation.CompletedAtUtc);

        // The only bearing on hand was consumed by V1, so V2's bearing replacement cannot be covered
        var ledgerBefore = await fixture.LedgerAsync();
        var shortOfStock = await fixture.ApproveAsync(v2JobId);

        Assert.False(shortOfStock.Succeeded);
        Assert.Equal("INSUFFICIENT_INVENTORY", shortOfStock.ConflictData!.Reason);
        Assert.Equal(ledgerBefore, await fixture.LedgerAsync());

        // A second bearing is received; V2 can now reserve it while V1's consumed reservation is kept
        await using (var dbContext = fixture.CreateDbContext())
        {
            (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).OnHandQuantity = 2m;
            await dbContext.SaveChangesAsync();
        }

        var v2 = await fixture.ApproveAsync(v2JobId);

        Assert.True(v2.Succeeded, v2.FailureReason);
        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.Equal(DraftStatus.Superseded, (await dbContext.Drafts.SingleAsync(d => d.Id == v1.DraftId)).Status);
            var v1Reservation = await dbContext.Reservations.SingleAsync(r => r.DraftId == v1.DraftId);
            Assert.Equal(
                (consumedReservation.Id, ReservationStatus.Confirmed, consumedReservation.CompletedAtUtc),
                (v1Reservation.Id, v1Reservation.Status, v1Reservation.CompletedAtUtc));

            var v2Reservation = await dbContext.Reservations.SingleAsync(r => r.DraftId == v2.DraftId);
            Assert.Equal(("BR-101", 1m, ReservationStatus.Confirmed),
                (v2Reservation.SparePartId, v2Reservation.QuantityReserved, v2Reservation.Status));
            Assert.Null(v2Reservation.CompletedAtUtc);

            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            var confirmedTotal = (await dbContext.Reservations
                    .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                    .ToListAsync())
                .Sum(r => r.QuantityReserved);
            Assert.Equal((2m, 2m, 0m), (br101.OnHandQuantity, br101.ReservedQuantity, br101.AvailableQuantity));
            Assert.Equal(br101.ReservedQuantity, confirmedTotal);
        }
    }

    [Fact]
    public async Task CancellingV2AfterReplacement_ReleasesV2Stock_AndLeavesV1SupersededWithItsLocks()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var (v1JobId, v2JobId) = await fixture.AddV1AndV2JobsAsync();
        var v1 = await fixture.ApproveAsync(v1JobId);
        var v2 = await fixture.ApproveAsync(v2JobId);
        Assert.True(v1.Succeeded, v1.FailureReason);
        Assert.True(v2.Succeeded, v2.FailureReason);
        var v1AfterReplacement = await fixture.SnapshotDraftAsync(v1.DraftId!.Value);

        var cancellation = await fixture.CancelAsync(v2.DraftId!.Value);

        Assert.True(cancellation.Succeeded, cancellation.FailureReason);
        Assert.Equal(v1AfterReplacement, await fixture.SnapshotDraftAsync(v1.DraftId.Value));
        Assert.StartsWith($"{DraftStatus.Superseded}|", v1AfterReplacement);
        await using var dbContext = fixture.CreateDbContext();
        Assert.Equal(DraftStatus.Cancelled, (await dbContext.Drafts.SingleAsync(d => d.Id == v2.DraftId)).Status);
        Assert.All(
            await dbContext.Reservations.Where(r => r.DraftId == v2.DraftId).ToListAsync(),
            r => Assert.Equal(ReservationStatus.Cancelled, r.Status));
        Assert.False(await dbContext.Reservations.AnyAsync(r => r.Status == ReservationStatus.Confirmed));
        Assert.Equal(0m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
        Assert.Equal(3, (await fixture.LocksAsync(v1.DraftId.Value)).Count);
    }

    [Fact]
    public async Task CancellingDraftWithConsumedPart_KeepsConsumedReservation_AndReleasesOnlyUnusedStock()
    {
        // FR08: cancelling a plan releases only unused reservations. A reservation is consumed once
        // mark-task-complete has stamped its CompletedAtUtc.
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        await fixture.AddJobAsync(jobId, null, ApprovalTestSupport.StableIdCandidate());
        var approval = await fixture.ApproveAsync(jobId);
        Assert.True(approval.Succeeded, approval.FailureReason);
        var draftId = approval.DraftId!.Value;
        await using (var dbContext = fixture.CreateDbContext())
        {
            var completion = await new PlanProgressService(dbContext)
                .MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);
            Assert.True(completion.Succeeded, completion.FailureReason);
        }

        var consumed = await fixture.SingleReservationAsync(draftId);
        Assert.Equal(("BR-101", 1m, ReservationStatus.Confirmed),
            (consumed.SparePartId, consumed.QuantityReserved, consumed.Status));
        Assert.NotNull(consumed.CompletedAtUtc);

        var cancellation = await fixture.CancelAsync(draftId);

        Assert.True(cancellation.Succeeded, cancellation.FailureReason);
        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.Equal(DraftStatus.Cancelled, (await dbContext.Drafts.SingleAsync(d => d.Id == draftId)).Status);
            var reservation = await dbContext.Reservations.SingleAsync(r => r.DraftId == draftId);
            Assert.Equal((consumed.Id, ReservationStatus.Confirmed, consumed.CompletedAtUtc),
                (reservation.Id, reservation.Status, reservation.CompletedAtUtc));

            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            var confirmedTotal = (await dbContext.Reservations
                    .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                    .ToListAsync())
                .Sum(r => r.QuantityReserved);
            Assert.Equal(confirmedTotal, br101.ReservedQuantity);
            Assert.Equal((1m, 1m, 0m), (br101.OnHandQuantity, br101.ReservedQuantity, br101.AvailableQuantity));
        }
    }

    [Fact]
    public async Task CancellingDraftWithUnusedPart_ReleasesItsReservation()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        await fixture.AddJobAsync(jobId, null, ApprovalTestSupport.StableIdCandidate());
        var approval = await fixture.ApproveAsync(jobId);
        Assert.True(approval.Succeeded, approval.FailureReason);
        var draftId = approval.DraftId!.Value;
        Assert.Null((await fixture.SingleReservationAsync(draftId)).CompletedAtUtc);

        var cancellation = await fixture.CancelAsync(draftId);

        Assert.True(cancellation.Succeeded, cancellation.FailureReason);
        await using var dbContext = fixture.CreateDbContext();
        Assert.Equal(DraftStatus.Cancelled, (await dbContext.Drafts.SingleAsync(d => d.Id == draftId)).Status);
        Assert.Equal(ReservationStatus.Cancelled, (await dbContext.Reservations.SingleAsync(r => r.DraftId == draftId)).Status);
        var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
        Assert.Equal((1m, 0m, 1m), (br101.OnHandQuantity, br101.ReservedQuantity, br101.AvailableQuantity));
    }

    [Fact]
    public async Task SecondReplacementFromSupersededV1_IsRejectedByServiceAndReplanEndpoint_AndNothingIsPersisted()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var (v1JobId, v2aJobId) = await fixture.AddV1AndV2JobsAsync();
        var v2bJobId = Guid.NewGuid();
        await fixture.AddJobAsync(v2bJobId, v1JobId, ApprovalTestSupport.StableIdCandidate());
        var v1 = await fixture.ApproveAsync(v1JobId);
        var v2a = await fixture.ApproveAsync(v2aJobId);
        Assert.True(v1.Succeeded, v1.FailureReason);
        Assert.True(v2a.Succeeded, v2a.FailureReason);
        var ledgerBefore = await fixture.LedgerAsync();

        var v2b = await fixture.ApproveAsync(v2bJobId);

        using var replanRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/planning-jobs/{v1JobId}/replan")
        {
            Content = JsonContent.Create(new
            {
                requestText = "Revise the P-101 plan again from the superseded V1",
                assetIds = new[] { "P-101" },
                windowId = ApprovalTestSupport.BaselineWindowId
            })
        };
        replanRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var replan = await fixture.Client.SendAsync(replanRequest);

        Assert.False(v2b.Succeeded);
        Assert.Equal(("PlanningJob", "NO_APPROVED_ORIGINAL_DRAFT", v1JobId.ToString()),
            (v2b.ConflictData!.ResourceType, v2b.ConflictData.Reason, v2b.ConflictData.ResourceId));
        Assert.Equal(HttpStatusCode.Conflict, replan.StatusCode);
        Assert.Contains("no approved draft", await replan.Content.ReadAsStringAsync());
        Assert.Equal(ledgerBefore, await fixture.LedgerAsync());
        await using var dbContext = fixture.CreateDbContext();
        Assert.False(await dbContext.Drafts.AnyAsync(d => d.PlanningJobId == v2bJobId));
        Assert.Equal(2, await dbContext.PlanningJobs.CountAsync(j => j.OriginalJobId == v1JobId));
    }

    [Fact]
    public async Task FailedReplacement_StockShortfall_PersistsNothingForV2_AndLeavesV1Unchanged()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var (v1JobId, v2JobId) = await fixture.AddV1AndV2JobsAsync();
        var v1 = await fixture.ApproveAsync(v1JobId);
        Assert.True(v1.Succeeded, v1.FailureReason);

        // A stock correction removes the only bearing, so even V1's released unit cannot cover V2
        await using (var dbContext = fixture.CreateDbContext())
        {
            (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).OnHandQuantity = 0m;
            await dbContext.SaveChangesAsync();
        }

        var ledgerBefore = await fixture.LedgerAsync();
        var v2 = await fixture.ApproveAsync(v2JobId);

        Assert.False(v2.Succeeded);
        Assert.Equal("INSUFFICIENT_INVENTORY", v2.ConflictData!.Reason);
        await fixture.AssertReplacementLeftNoTraceAsync(ledgerBefore, v1.DraftId!.Value, v2JobId);
    }

    [Fact]
    public async Task FailedReplacement_AfterItsWritesReachTheDatabase_IsRolledBack_AndLeavesV1Unchanged()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var (v1JobId, v2JobId) = await fixture.AddV1AndV2JobsAsync();
        var v1 = await fixture.ApproveAsync(v1JobId);
        Assert.True(v1.Succeeded, v1.FailureReason);
        var ledgerBefore = await fixture.LedgerAsync();

        // Fails V2's approval after SaveChanges has written V1's supersession, the reservation swap, V2's draft
        // and locks inside the approval transaction, but before that transaction commits
        var failAfterSave = new FailAfterSaveInterceptor();
        await using var failingServices = fixture.CreateServices(failAfterSave);
        await using var scope = failingServices.CreateAsyncScope();
        var approvalService = scope.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => approvalService.ApproveDraftAsync(v2JobId, CancellationToken.None));

        Assert.True(failAfterSave.RowsWritten > 0);
        await fixture.AssertReplacementLeftNoTraceAsync(ledgerBefore, v1.DraftId!.Value, v2JobId);
    }

    [Fact]
    public async Task CancellingV1_WhileItsReplacementV2IsApproved_KeepsDraftAndStockStateConsistent()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var (v1JobId, v2JobId) = await fixture.AddV1AndV2JobsAsync();
        var v1 = await fixture.ApproveAsync(v1JobId);
        Assert.True(v1.Succeeded, v1.FailureReason);

        // Cancellation reads V1 and its reservations before it begins its transaction. Hold it at the start of
        // that transaction, let V2's approval supersede V1 and commit, then let the cancellation continue.
        var gate = new PauseAtTransactionStartInterceptor();
        await using var cancellingServices = fixture.CreateServices(gate);
        await using var cancellingScope = cancellingServices.CreateAsyncScope();
        var cancellation = cancellingScope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
            .CancelDraftAsync(v1.DraftId!.Value, CancellationToken.None);
        DraftApprovalResult v2;
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            v2 = await fixture.ApproveAsync(v2JobId);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        var cancellationResult = await cancellation.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(v2.Succeeded, v2.FailureReason);
        await using var dbContext = fixture.CreateDbContext();
        var v1Status = (await dbContext.Drafts.SingleAsync(d => d.Id == v1.DraftId)).Status;
        var v2Status = (await dbContext.Drafts.SingleAsync(d => d.Id == v2.DraftId)).Status;
        var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
        var confirmedTotal = (await dbContext.Reservations
                .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                .ToListAsync())
            .Sum(r => r.QuantityReserved);

        // V2 committed first, so V1 must stay superseded and the stock counter must match V2's confirmed unit
        var expected = $"V1={DraftStatus.Superseded}|V2={DraftStatus.Approved}|reserved=1|confirmed=1";
        var observed = $"V1={v1Status}|V2={v2Status}|reserved={br101.ReservedQuantity:0.###}|confirmed={confirmedTotal:0.###}";
        Assert.True(expected == observed,
            $"Expected {expected} but observed {observed}; V1 cancellation succeeded={cancellationResult.Succeeded}");
    }

    /// <summary>Holds the first transaction its DbContext begins until released.</summary>
    private sealed class PauseAtTransactionStartInterceptor : DbTransactionInterceptor
    {
        private int _paused;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    /// <summary>Throws after SaveChanges has written its rows, while the caller's transaction is still open.</summary>
    private sealed class FailAfterSaveInterceptor : SaveChangesInterceptor
    {
        public int RowsWritten { get; private set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            RowsWritten = result;
            throw new InvalidOperationException("Simulated failure after the approval's writes, before commit");
        }
    }

    private sealed class FileDatabaseFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private FileDatabaseFixture(string databasePath, string connectionString, WebApplication app)
        {
            _databasePath = databasePath;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<FileDatabaseFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"maintenance-planner-f03-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
            await using (var dbContext = new MaintenancePlannerDbContext(
                             new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(connectionString).Options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(connectionString));
            builder.Services.AddApprovalServices();
            builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
            var app = builder.Build();
            app.MapPlanningJobEndpoints();
            app.MapApprovalEndpoints();
            await app.StartAsync();
            return new FileDatabaseFixture(databasePath, connectionString, app);
        }

        /// <summary>Approval services on the same database whose DbContext runs the given interceptor.</summary>
        public ServiceProvider CreateServices(IInterceptor interceptor)
        {
            var services = new ServiceCollection();
            services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(_connectionString).AddInterceptors(interceptor));
            services.AddApprovalServices();
            return services.BuildServiceProvider();
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        public async Task AddJobAsync(Guid jobId, Guid? originalJobId, object candidate)
        {
            await using var dbContext = CreateDbContext();
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId, originalJobId, candidate);
        }

        public async Task<(Guid V1JobId, Guid V2JobId)> AddV1AndV2JobsAsync()
        {
            var v1JobId = Guid.NewGuid();
            var v2JobId = Guid.NewGuid();
            await AddJobAsync(v1JobId, null, ApprovalTestSupport.StableIdCandidate());
            await AddJobAsync(v2JobId, v1JobId, ApprovalTestSupport.LowestCostCandidate());
            return (v1JobId, v2JobId);
        }

        public async Task<DraftApprovalResult> ApproveAsync(Guid jobId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
                .ApproveDraftAsync(jobId, CancellationToken.None);
        }

        public async Task<CancellationResult> CancelAsync(Guid draftId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
                .CancelDraftAsync(draftId, CancellationToken.None);
        }

        public async Task<IReadOnlyList<LockedTaskAssignment>> LocksAsync(Guid draftId)
        {
            await using var dbContext = CreateDbContext();
            return (await dbContext.LockedTaskAssignments.AsNoTracking().Where(l => l.DraftId == draftId).ToListAsync())
                .OrderBy(l => l.TaskId)
                .ToList();
        }

        public async Task<Reservation> SingleReservationAsync(Guid draftId)
        {
            await using var dbContext = CreateDbContext();
            return await dbContext.Reservations.AsNoTracking().SingleAsync(r => r.DraftId == draftId);
        }

        /// <summary>A draft's status, approval time, approved snapshot, reservations and locks.</summary>
        public async Task<string> SnapshotDraftAsync(Guid draftId)
        {
            await using var dbContext = CreateDbContext();
            var draft = await dbContext.Drafts.AsNoTracking().Include(d => d.Reservations).SingleAsync(d => d.Id == draftId);
            var reservations = string.Join(",", draft.Reservations.OrderBy(r => r.Id).Select(Describe));
            var locks = string.Join(",", (await LocksAsync(draftId)).Select(Describe));
            return $"{draft.Status}|{draft.ApprovedAtUtc:O}|{draft.ApprovedCandidatePayload}|{reservations}|{locks}";
        }

        /// <summary>Every draft, reservation and lock plus BR-101's quantities, for before/after comparison.</summary>
        public async Task<string> LedgerAsync()
        {
            await using var dbContext = CreateDbContext();
            var drafts = (await dbContext.Drafts.AsNoTracking().ToListAsync())
                .OrderBy(d => d.Id)
                .Select(d => $"{d.Id}:{d.PlanningJobId}:{d.Status}:{d.ApprovedAtUtc:O}:{d.ApprovedCandidatePayload}");
            var reservations = (await dbContext.Reservations.AsNoTracking().ToListAsync()).OrderBy(r => r.Id).Select(Describe);
            var locks = (await dbContext.LockedTaskAssignments.AsNoTracking().ToListAsync()).OrderBy(l => l.Id).Select(Describe);
            var br101 = await dbContext.SpareParts.AsNoTracking().SingleAsync(p => p.Id == "BR-101");
            return string.Join("\n", drafts.Concat(reservations).Concat(locks)) +
                   $"\nBR-101 onHand={br101.OnHandQuantity:0.###} reserved={br101.ReservedQuantity:0.###}";
        }

        public async Task AssertReplacementLeftNoTraceAsync(string ledgerBefore, Guid v1DraftId, Guid v2JobId)
        {
            Assert.Equal(ledgerBefore, await LedgerAsync());
            await using var dbContext = CreateDbContext();
            Assert.False(await dbContext.Drafts.AnyAsync(d => d.PlanningJobId == v2JobId));
            Assert.Equal(DraftStatus.Approved, (await dbContext.Drafts.SingleAsync(d => d.Id == v1DraftId)).Status);
            Assert.All(await dbContext.Reservations.ToListAsync(), r => Assert.Equal(v1DraftId, r.DraftId));
            Assert.All(await dbContext.LockedTaskAssignments.ToListAsync(), l => Assert.Equal(v1DraftId, l.DraftId));
            Assert.Equal(3, await dbContext.LockedTaskAssignments.CountAsync());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                try
                {
                    File.Delete(_databasePath + suffix);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of a temp file; a leftover file does not affect test outcomes
                }
            }
        }

        private static string Describe(Reservation r) =>
            $"{r.Id}:{r.DraftId}:{r.SparePartId}:{r.QuantityReserved:0.###}:{r.Status}:{r.ConfirmedAtUtc:O}:{r.CompletedAtUtc:O}";

        public static string Describe(LockedTaskAssignment l) =>
            $"{l.Id}:{l.DraftId}:{l.TaskId}:{l.LockedTechnicianId}:{l.CreatedAtUtc:O}";
    }
}
