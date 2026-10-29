using System.Text.Json;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Locks;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Locks;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// F06 Part B (FR08): replanning an approved V1 preserves its completed work; FR07: the replacement may plan with the
/// parts V1 holds unconsumed (which its approval releases), while no other plan may. V2 is planned through the real
/// processor, workflow, scheduler and validator, and approved through the real approval service, on a temporary
/// file-backed SQLite database; only the request interpreter and the manual-evidence retriever are stand-ins.
/// V1 is the stable-ID plan: P101-PREP (TECH-001) → P101-BEARING-REPLACEMENT (TECH-001, TECH-002; one BR-101) →
/// P101-VERIFICATION (TECH-003).
/// </summary>
public sealed class F06CompletedWorkReplanningFileDatabaseTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public async Task Replanning_DoesNotScheduleTasksCompletedOnV1_AndV2ReservesNoPartForThem()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        await fixture.CompleteTaskAsync(v1DraftId, "P101-PREP");
        // Completing the bearing replacement consumes V1's BR-101 reservation
        await fixture.CompleteTaskAsync(v1DraftId, "P101-BEARING-REPLACEMENT");
        var v1History = await fixture.DraftHistoryAsync(v1DraftId);

        var v2JobId = await fixture.ReplanAsync(v1JobId);

        var candidates = await fixture.CandidatePlacementsAsync(v2JobId);
        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate =>
        {
            var verification = Assert.Single(candidate);
            Assert.Equal("P101-VERIFICATION", verification.TaskId);
            Assert.Contains("TECH-003", verification.TechnicianIds); // V1's lock on the remaining task is kept
        });

        var v2 = await fixture.ApproveAsync(v2JobId, candidateIndex: 0);

        Assert.True(v2.Succeeded, v2.FailureReason);
        Assert.Equal(v1History, await fixture.DraftHistoryAsync(v1DraftId));
        await using var dbContext = fixture.CreateDbContext();
        Assert.Equal(DraftStatus.Superseded, (await dbContext.Drafts.SingleAsync(d => d.Id == v1DraftId)).Status);
        var consumed = await dbContext.Reservations.SingleAsync(r => r.DraftId == v1DraftId);
        Assert.Equal(ReservationStatus.Confirmed, consumed.Status);
        Assert.NotNull(consumed.CompletedAtUtc);
        Assert.Empty(await dbContext.Reservations.Where(r => r.DraftId == v2.DraftId).ToListAsync());
        Assert.Equal(
            ["P101-VERIFICATION:TECH-003"],
            (await dbContext.LockedTaskAssignments.Where(l => l.DraftId == v2.DraftId).ToListAsync())
                .Select(l => $"{l.TaskId}:{l.LockedTechnicianId}"));
        await fixture.AssertStockMatchesConfirmedReservationsAsync(onHand: 1m, reserved: 1m);

        // V2's export describes V2's own approved snapshot: only the remaining work
        using var export = JsonDocument.Parse(await fixture.ExportJsonAsync(v2.DraftId!.Value));
        Assert.Equal(v2.DraftId.ToString(), export.RootElement.GetProperty("draftId").GetString());
        Assert.Equal(
            ["P101-VERIFICATION:NotStarted"],
            export.RootElement.GetProperty("taskPlacements").EnumerateArray()
                .Select(p => $"{p.GetProperty("taskId").GetString()}:{p.GetProperty("status").GetString()}"));
    }

    [Fact]
    public async Task Replanning_TreatsACompletedPredecessorAsSatisfied_AndCancellingV2ReleasesOnlyItsOwnStock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        await fixture.CompleteTaskAsync(v1DraftId, "P101-PREP");

        // V1 holds the only bearing; the replacement may plan with it because its approval releases it (FR07)
        var v2JobId = await fixture.ReplanAsync(v1JobId);

        var candidates = await fixture.CandidatePlacementsAsync(v2JobId);
        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal(["P101-BEARING-REPLACEMENT", "P101-VERIFICATION"], candidate.Select(p => p.TaskId));
            var bearing = candidate[0];
            Assert.Equal(WindowStart, bearing.Start); // no PREP placement to wait for
            Assert.Contains("TECH-001", bearing.TechnicianIds); // V1's lock on the remaining task is kept
        });

        var v2 = await fixture.ApproveAsync(v2JobId, candidateIndex: 0);

        Assert.True(v2.Succeeded, v2.FailureReason);
        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.Equal(ReservationStatus.Cancelled, (await dbContext.Reservations.SingleAsync(r => r.DraftId == v1DraftId)).Status);
            var v2Reservation = await dbContext.Reservations.SingleAsync(r => r.DraftId == v2.DraftId);
            Assert.Equal(("BR-101", 1m, ReservationStatus.Confirmed),
                (v2Reservation.SparePartId, v2Reservation.QuantityReserved, v2Reservation.Status));
            Assert.Equal(TaskProgressStatus.Completed,
                (await dbContext.DraftTaskProgress.SingleAsync(p => p.DraftId == v1DraftId)).Status);
        }

        await fixture.AssertStockMatchesConfirmedReservationsAsync(onHand: 1m, reserved: 1m);

        var cancellation = await fixture.CancelAsync(v2.DraftId!.Value);

        Assert.True(cancellation.Succeeded, cancellation.FailureReason);
        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.Equal(DraftStatus.Superseded, (await dbContext.Drafts.SingleAsync(d => d.Id == v1DraftId)).Status);
            Assert.Equal(DraftStatus.Cancelled, (await dbContext.Drafts.SingleAsync(d => d.Id == v2.DraftId)).Status);
        }

        await fixture.AssertStockMatchesConfirmedReservationsAsync(onHand: 1m, reserved: 0m);
    }

    [Fact]
    public async Task StaleV2PlacingATaskCompletedOnV1AfterPlanning_IsRejectedAtApproval_AndNothingChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        // V2 was planned with the full scope before any V1 task was completed
        var v2JobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, v2JobId, v1JobId, ApprovalTestSupport.StableIdCandidate());
        await fixture.CompleteTaskAsync(v1DraftId, "P101-PREP");
        var ledgerBefore = await fixture.LedgerAsync();

        var v2 = await fixture.ApproveAsync(v2JobId, candidateIndex: 0);

        Assert.False(v2.Succeeded);
        Assert.Equal(("CANDIDATE_NO_LONGER_VALID", "TASK_ALREADY_COMPLETED"),
            (v2.ConflictData!.Reason, v2.ConflictData.ResourceId));
        Assert.Equal(ledgerBefore, await fixture.LedgerAsync());
        await using var verifyContext = fixture.CreateDbContext();
        Assert.False(await verifyContext.Drafts.AnyAsync(d => d.PlanningJobId == v2JobId));
        Assert.Equal(DraftStatus.Approved, (await verifyContext.Drafts.SingleAsync(d => d.Id == v1DraftId)).Status);
    }

    [Fact]
    public async Task CancellingV2_AfterReplacingV1WithConsumedPart_KeepsV1HistoryAndItsConsumedReservation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        await fixture.CompleteTaskAsync(v1DraftId, "P101-PREP");
        await fixture.CompleteTaskAsync(v1DraftId, "P101-BEARING-REPLACEMENT"); // consumes V1's BR-101 reservation
        var v2 = await fixture.ApproveAsync(await fixture.ReplanAsync(v1JobId), candidateIndex: 0);
        Assert.True(v2.Succeeded, v2.FailureReason);
        var v1History = await fixture.DraftHistoryAsync(v1DraftId);

        var cancellation = await fixture.CancelAsync(v2.DraftId!.Value);

        Assert.True(cancellation.Succeeded, cancellation.FailureReason);
        Assert.Equal(v1History, await fixture.DraftHistoryAsync(v1DraftId));
        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.Equal(DraftStatus.Superseded, (await dbContext.Drafts.SingleAsync(d => d.Id == v1DraftId)).Status);
            Assert.Equal(DraftStatus.Cancelled, (await dbContext.Drafts.SingleAsync(d => d.Id == v2.DraftId)).Status);
        }

        await fixture.AssertStockMatchesConfirmedReservationsAsync(onHand: 1m, reserved: 1m);
    }

    [Fact]
    public async Task FailedReplacement_WithACompletedPredecessor_LeavesV1AndAllResourcesUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        await fixture.CompleteTaskAsync(v1DraftId, "P101-PREP");
        var v2JobId = await fixture.ReplanAsync(v1JobId); // feasible with V1's releasable bearing (FR07)

        // A stock correction then removes the only bearing, so V2's remaining bearing replacement cannot be covered
        await fixture.SetBearingsOnHandAsync(0m);

        var ledgerBefore = await fixture.LedgerAsync();
        var v2 = await fixture.ApproveAsync(v2JobId, candidateIndex: 0);

        Assert.False(v2.Succeeded);
        Assert.Equal("INSUFFICIENT_INVENTORY", v2.ConflictData!.Reason);
        Assert.Equal(ledgerBefore, await fixture.LedgerAsync());
        await using var verifyContext = fixture.CreateDbContext();
        Assert.False(await verifyContext.Drafts.AnyAsync(d => d.PlanningJobId == v2JobId));
        Assert.Equal(DraftStatus.Approved, (await verifyContext.Drafts.SingleAsync(d => d.Id == v1DraftId)).Status);
    }

    [Fact]
    public async Task FR07_ReplanningTheFullScopeWhileV1HoldsTheOnlyBearing_IsFeasible_AndApprovalSwapsTheReservation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        await fixture.AssertStockMatchesConfirmedReservationsAsync(onHand: 1m, reserved: 1m); // available: 0

        var v2JobId = await fixture.ReplanAsync(v1JobId);

        var candidates = await fixture.CandidatePlacementsAsync(v2JobId);
        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate => Assert.Equal(
            ["P101-PREP", "P101-BEARING-REPLACEMENT", "P101-VERIFICATION"],
            candidate.Select(p => p.TaskId)));
        await using (var dbContext = fixture.CreateDbContext())
        {
            // Planning released nothing: V1 still holds its confirmed bearing
            Assert.Equal(ReservationStatus.Confirmed, (await dbContext.Reservations.SingleAsync()).Status);
        }

        var v2 = await fixture.ApproveAsync(v2JobId, candidateIndex: 0);

        Assert.True(v2.Succeeded, v2.FailureReason);
        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.Equal(DraftStatus.Superseded, (await dbContext.Drafts.SingleAsync(d => d.Id == v1DraftId)).Status);
            Assert.Equal(ReservationStatus.Cancelled, (await dbContext.Reservations.SingleAsync(r => r.DraftId == v1DraftId)).Status);
            var v2Reservation = await dbContext.Reservations.SingleAsync(r => r.DraftId == v2.DraftId);
            Assert.Equal(("BR-101", 1m, ReservationStatus.Confirmed),
                (v2Reservation.SparePartId, v2Reservation.QuantityReserved, v2Reservation.Status));
            Assert.Equal(2, await dbContext.Reservations.CountAsync());
        }

        await fixture.AssertStockMatchesConfirmedReservationsAsync(onHand: 1m, reserved: 1m);
    }

    [Fact]
    public async Task FR07_ReplanningCannotReuseABearingV1HasConsumed_AndLeavesV1Unchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        // The bearing is consumed through the spare-part path while the bearing task itself is not completed, so the
        // replacement still needs a bearing, and a consumed reservation is never released
        await fixture.MarkPartConsumedAsync(v1DraftId, "BR-101");
        var ledgerBefore = await fixture.LedgerAsync();

        var (_, resultPayload) = await fixture.PlanAsync(v1JobId);

        AssertInfeasibleForLackOfBearing(resultPayload);
        Assert.Equal(ledgerBefore, await fixture.LedgerAsync());
    }

    [Fact]
    public async Task FR07_AnUnrelatedPlan_CannotPlanWithTheBearingV1Holds()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ApproveV1Async();
        var ledgerBefore = await fixture.LedgerAsync();

        var (_, resultPayload) = await fixture.PlanAsync(originalJobId: null);

        AssertInfeasibleForLackOfBearing(resultPayload);
        Assert.Equal(ledgerBefore, await fixture.LedgerAsync());
    }

    [Fact]
    public async Task InProgressTaskOnV1_KeepsItsApprovedPlacementInV2_IsNotDuplicated_AndStaysInProgress()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        await fixture.CompleteTaskAsync(v1DraftId, "P101-PREP");
        await fixture.StartTaskAsync(v1DraftId, "P101-BEARING-REPLACEMENT"); // approved 09:00-12:00, TECH-001 + TECH-002
        var v1LocksAndProgress = await fixture.LocksAndProgressAsync(v1DraftId);

        var v2JobId = await fixture.ReplanAsync(v1JobId);

        var candidates = await fixture.CandidatePlacementsAsync(v2JobId);
        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal(["P101-BEARING-REPLACEMENT", "P101-VERIFICATION"], candidate.Select(p => p.TaskId));
            var bearing = candidate[0];
            // Kept where it is in progress, not moved to the window start that its completed predecessor would allow
            Assert.Equal((WindowStart.AddHours(1), WindowStart.AddHours(4)), (bearing.Start, bearing.End));
            Assert.Equal(["TECH-001", "TECH-002"], bearing.TechnicianIds);
            Assert.True(candidate[1].Start >= bearing.End);
        });

        var v2 = await fixture.ApproveAsync(v2JobId, candidateIndex: 0);

        Assert.True(v2.Succeeded, v2.FailureReason);
        Assert.Equal(v1LocksAndProgress, await fixture.LocksAndProgressAsync(v1DraftId));
        var v2Progress = await fixture.GetProgressAsync(v2.DraftId!.Value);
        Assert.Equal(
            ["P101-BEARING-REPLACEMENT:InProgress", "P101-VERIFICATION:NotStarted"],
            v2Progress.TaskStatuses.Select(t => $"{t.TaskId}:{t.Status}"));
        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1Bearing = await dbContext.DraftTaskProgress.SingleAsync(p => p.DraftId == v1DraftId && p.TaskId == "P101-BEARING-REPLACEMENT");
            Assert.Equal(v1Bearing.UpdatedAtUtc, v2Progress.TaskStatuses[0].UpdatedAtUtc);
            Assert.Equal("TECH-001", (await dbContext.LockedTaskAssignments
                .SingleAsync(l => l.DraftId == v2.DraftId && l.TaskId == "P101-BEARING-REPLACEMENT")).LockedTechnicianId);
            // The bearing is still needed by the work in progress: V1's unused reservation moves to V2
            Assert.Equal(ReservationStatus.Cancelled, (await dbContext.Reservations.SingleAsync(r => r.DraftId == v1DraftId)).Status);
            Assert.Equal(ReservationStatus.Confirmed, (await dbContext.Reservations.SingleAsync(r => r.DraftId == v2.DraftId)).Status);
        }

        await fixture.AssertStockMatchesConfirmedReservationsAsync(onHand: 1m, reserved: 1m);
    }

    [Fact]
    public async Task StaleV2_PlannedBeforeATaskWasStartedOnV1_IsRejectedAtApproval_AndNothingChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        await fixture.CompleteTaskAsync(v1DraftId, "P101-PREP");
        var v2JobId = await fixture.ReplanAsync(v1JobId); // the bearing replacement is not started: V2 moves it to 08:00
        await fixture.StartTaskAsync(v1DraftId, "P101-BEARING-REPLACEMENT"); // started at its approved 09:00 placement
        var ledgerBefore = await fixture.LedgerAsync();

        var v2 = await fixture.ApproveAsync(v2JobId, candidateIndex: 0);

        Assert.False(v2.Succeeded);
        Assert.Equal(("CANDIDATE_NO_LONGER_VALID", "IN_PROGRESS_TASK_NOT_PRESERVED"),
            (v2.ConflictData!.Reason, v2.ConflictData.ResourceId));
        Assert.Equal(ledgerBefore, await fixture.LedgerAsync());
    }

    [Fact]
    public async Task StaleV2_WhoseInProgressTaskWasCompletedOnV1AfterPlanning_IsRejectedAtApproval_AndNothingChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        await fixture.CompleteTaskAsync(v1DraftId, "P101-PREP");
        await fixture.StartTaskAsync(v1DraftId, "P101-BEARING-REPLACEMENT");
        var v2JobId = await fixture.ReplanAsync(v1JobId); // carries the in-progress bearing replacement
        // The bearing replacement is then finished on V1, which also consumes V1's bearing
        await fixture.FinishTaskAsync(v1DraftId, "P101-BEARING-REPLACEMENT");
        var ledgerBefore = await fixture.LedgerAsync();

        var v2 = await fixture.ApproveAsync(v2JobId, candidateIndex: 0);

        Assert.False(v2.Succeeded);
        Assert.Equal(("CANDIDATE_NO_LONGER_VALID", "TASK_ALREADY_COMPLETED"),
            (v2.ConflictData!.Reason, v2.ConflictData.ResourceId));
        Assert.Equal(ledgerBefore, await fixture.LedgerAsync());
    }

    [Fact]
    public async Task InProgressTaskThatCannotBeKeptAtItsPlacement_MakesReplanningInfeasible_AndV1IsUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        await fixture.CompleteTaskAsync(v1DraftId, "P101-PREP");
        await fixture.StartTaskAsync(v1DraftId, "P101-BEARING-REPLACEMENT");
        // TECH-002, in the in-progress bearing replacement's crew, is no longer available
        await fixture.RemoveAvailabilityAsync("TECH-002");
        var ledgerBefore = await fixture.LedgerAsync();

        var (_, resultPayload) = await fixture.PlanAsync(v1JobId);

        using var document = JsonDocument.Parse(resultPayload);
        Assert.NotEqual("CandidatesGenerated", document.RootElement.GetProperty("outcome").GetString());
        Assert.Contains(
            document.RootElement.GetProperty("result").GetProperty("violations").EnumerateArray(),
            violation => violation.GetProperty("taskId").GetString() == "P101-BEARING-REPLACEMENT");
        Assert.Equal(ledgerBefore, await fixture.LedgerAsync());
    }

    [Fact]
    public async Task FailedReplacement_WithAnInProgressTask_LeavesV1AndAllResourcesUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (v1JobId, v1DraftId) = await fixture.ApproveV1Async();
        await fixture.CompleteTaskAsync(v1DraftId, "P101-PREP");
        await fixture.StartTaskAsync(v1DraftId, "P101-BEARING-REPLACEMENT");
        var v2JobId = await fixture.ReplanAsync(v1JobId);
        await fixture.SetBearingsOnHandAsync(0m); // a stock correction removes the only bearing
        var ledgerBefore = await fixture.LedgerAsync();

        var v2 = await fixture.ApproveAsync(v2JobId, candidateIndex: 0);

        Assert.False(v2.Succeeded);
        Assert.Equal("INSUFFICIENT_INVENTORY", v2.ConflictData!.Reason);
        Assert.Equal(ledgerBefore, await fixture.LedgerAsync());
        await using var dbContext = fixture.CreateDbContext();
        Assert.False(await dbContext.Drafts.AnyAsync(d => d.PlanningJobId == v2JobId));
        Assert.Equal(DraftStatus.Approved, (await dbContext.Drafts.SingleAsync(d => d.Id == v1DraftId)).Status);
    }

    private static void AssertInfeasibleForLackOfBearing(string resultPayload)
    {
        using var document = JsonDocument.Parse(resultPayload);
        Assert.Equal("Infeasible", document.RootElement.GetProperty("outcome").GetString());
        var violations = document.RootElement.GetProperty("result").GetProperty("violations").EnumerateArray()
            .Select(v => $"{v.GetProperty("code").GetString()}:{v.GetProperty("resourceId").GetString()}");
        Assert.Contains("PART_UNAVAILABLE:BR-101", violations);
    }

    private sealed record Placement(string TaskId, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<string> TechnicianIds);

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly string _databasePath;
        private readonly string _connectionString;
        private readonly ServiceProvider _services;

        private Fixture(string databasePath, string connectionString, ServiceProvider services)
        {
            _databasePath = databasePath;
            _connectionString = connectionString;
            _services = services;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"maintenance-planner-f06b-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
            await using (var dbContext = new MaintenancePlannerDbContext(
                             new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(connectionString).Options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            var services = new ServiceCollection();
            services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(connectionString));
            services.AddApprovalServices(); // queries, spare-part provider, candidate validator, approvals
            services.AddScoped<CandidateCostCalculator>();
            services.AddScoped<DeterministicPlanningScheduler>();
            services.AddSingleton<IManualEvidenceRetriever>(new P101EvidenceRetriever());
            services.AddSingleton<IPlanningRequestInterpreter>(new BearingReplacementInterpreter());
            services.AddScoped<IPlanningWorkflow, PlanningWorkflow>();
            services.AddScoped<ITaskLockService, TaskLockService>();
            services.AddScoped<IPlanningJobProcessor, PlanningJobProcessor>();
            services.AddScoped<IPlanProgressService, PlanProgressService>();
            services.AddScoped<IPlanExportService, PlanExportService>();
            return new Fixture(databasePath, connectionString, services.BuildServiceProvider());
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        public async Task<(Guid JobId, Guid DraftId)> ApproveV1Async()
        {
            var jobId = Guid.NewGuid();
            await using (var dbContext = CreateDbContext())
                await ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId, null, ApprovalTestSupport.StableIdCandidate());
            var approval = await ApproveAsync(jobId, candidateIndex: 0);
            Assert.True(approval.Succeeded, approval.FailureReason);
            return (jobId, approval.DraftId!.Value);
        }

        public async Task<DraftApprovalResult> ApproveAsync(Guid jobId, int candidateIndex)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
                .ApproveDraftAsync(jobId, candidateIndex, CancellationToken.None);
        }

        public async Task<CancellationResult> CancelAsync(Guid draftId)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
                .CancelDraftAsync(draftId, CancellationToken.None);
        }

        public async Task StartTaskAsync(Guid draftId, string taskId)
        {
            await using var scope = _services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IPlanProgressService>()
                .UpdateTaskStatusAsync(draftId, taskId, TaskProgressStatus.InProgress, CancellationToken.None);
            Assert.True(result.Succeeded, result.FailureReason);
        }

        public async Task FinishTaskAsync(Guid draftId, string taskId)
        {
            await using var scope = _services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IPlanProgressService>()
                .UpdateTaskStatusAsync(draftId, taskId, TaskProgressStatus.Completed, CancellationToken.None);
            Assert.True(result.Succeeded, result.FailureReason);
        }

        public async Task<PlanProgress> GetProgressAsync(Guid draftId)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IPlanProgressService>()
                .GetPlanProgressAsync(draftId, CancellationToken.None);
        }

        /// <summary>A draft's lock and task-progress rows.</summary>
        public async Task<string> LocksAndProgressAsync(Guid draftId)
        {
            await using var dbContext = CreateDbContext();
            var locks = (await dbContext.LockedTaskAssignments.AsNoTracking().Where(l => l.DraftId == draftId).ToListAsync())
                .OrderBy(l => l.Id).Select(Describe);
            var progress = (await dbContext.DraftTaskProgress.AsNoTracking().Where(p => p.DraftId == draftId).ToListAsync())
                .OrderBy(p => p.Id).Select(Describe);
            return string.Join("\n", locks.Concat(progress));
        }

        public async Task RemoveAvailabilityAsync(string technicianId)
        {
            await using var dbContext = CreateDbContext();
            dbContext.TechnicianAvailabilities.RemoveRange(
                dbContext.TechnicianAvailabilities.Where(a => a.TechnicianId == technicianId));
            await dbContext.SaveChangesAsync();
        }

        public async Task CompleteTaskAsync(Guid draftId, string taskId)
        {
            foreach (var status in new[] { TaskProgressStatus.InProgress, TaskProgressStatus.Completed })
            {
                await using var scope = _services.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IPlanProgressService>()
                    .UpdateTaskStatusAsync(draftId, taskId, status, CancellationToken.None);
                Assert.True(result.Succeeded, result.FailureReason);
            }
        }

        public async Task SetBearingsOnHandAsync(decimal onHand)
        {
            await using var dbContext = CreateDbContext();
            (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).OnHandQuantity = onHand;
            await dbContext.SaveChangesAsync();
        }

        public async Task<string> ExportJsonAsync(Guid draftId)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IPlanExportService>()
                .ExportAsJsonAsync(draftId, CancellationToken.None);
        }

        /// <summary>Creates a replacement job for the original job, plans it, and asserts candidates were generated.</summary>
        public async Task<Guid> ReplanAsync(Guid originalJobId)
        {
            var (jobId, resultPayload) = await PlanAsync(originalJobId);
            using var document = JsonDocument.Parse(resultPayload);
            Assert.True(
                document.RootElement.GetProperty("outcome").GetString() == "CandidatesGenerated",
                $"Replanning did not generate candidates: {resultPayload}");
            return jobId;
        }

        public async Task MarkPartConsumedAsync(Guid draftId, string sparePartId)
        {
            await using var scope = _services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IPlanProgressService>()
                .MarkTaskCompleteAsync(draftId, sparePartId, CancellationToken.None);
            Assert.True(result.Succeeded, result.FailureReason);
        }

        /// <summary>
        /// Creates a planning job (a replacement when an original job is given), runs it through the real processor
        /// and stores its result; returns the job ID and the result payload whatever the outcome.
        /// </summary>
        public async Task<(Guid JobId, string ResultPayload)> PlanAsync(Guid? originalJobId)
        {
            var jobId = Guid.NewGuid();
            var requestPayload = ApprovalTestSupport.RequestPayload("Replan the remaining P-101 work.");
            await using (var dbContext = CreateDbContext())
            {
                await dbContext.PlanningJobs.AddAsync(new PlanningJob
                {
                    Id = jobId,
                    OperationId = Guid.NewGuid(),
                    OriginalJobId = originalJobId,
                    RequestPayload = requestPayload,
                    Status = PlanningJobStatus.Running,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
                await dbContext.SaveChangesAsync();
            }

            PlanningJobProcessingResult processed;
            await using (var scope = _services.CreateAsyncScope())
            {
                var leaseToken = Guid.NewGuid();
                processed = await scope.ServiceProvider.GetRequiredService<IPlanningJobProcessor>().ProcessAsync(
                    jobId,
                    leaseToken,
                    new ClaimedPlanningJob(jobId, requestPayload, 1, 2, 0, leaseToken, null),
                    CancellationToken.None);
            }

            await using (var dbContext = CreateDbContext())
            {
                var job = await dbContext.PlanningJobs.SingleAsync(j => j.Id == jobId);
                job.Status = PlanningJobStatus.Succeeded;
                job.ResultPayload = processed.ResultPayload;
                job.CompletedAtUtc = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync();
            }

            return (jobId, processed.ResultPayload);
        }

        public async Task<IReadOnlyList<IReadOnlyList<Placement>>> CandidatePlacementsAsync(Guid jobId)
        {
            await using var dbContext = CreateDbContext();
            var payload = (await dbContext.PlanningJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).ResultPayload!;
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.GetProperty("result").GetProperty("candidates").EnumerateArray()
                .Select(candidate => (IReadOnlyList<Placement>)candidate.GetProperty("taskPlacements")
                    .Deserialize<List<Placement>>(JsonOptions)!)
                .ToList();
        }

        /// <summary>A draft's approval time, approved snapshot, reservations, locks and task progress.</summary>
        public async Task<string> DraftHistoryAsync(Guid draftId)
        {
            await using var dbContext = CreateDbContext();
            var draft = await dbContext.Drafts.AsNoTracking().SingleAsync(d => d.Id == draftId);
            var reservations = (await dbContext.Reservations.AsNoTracking().Where(r => r.DraftId == draftId).ToListAsync())
                .OrderBy(r => r.Id).Select(Describe);
            var locks = (await dbContext.LockedTaskAssignments.AsNoTracking().Where(l => l.DraftId == draftId).ToListAsync())
                .OrderBy(l => l.Id).Select(Describe);
            var progress = (await dbContext.DraftTaskProgress.AsNoTracking().Where(p => p.DraftId == draftId).ToListAsync())
                .OrderBy(p => p.Id).Select(Describe);
            return string.Join("\n", new[] { $"{draft.ApprovedAtUtc:O}|{draft.ApprovedCandidatePayload}" }
                .Concat(reservations).Concat(locks).Concat(progress));
        }

        /// <summary>Every draft, reservation, lock and task-progress row plus BR-101's quantities.</summary>
        public async Task<string> LedgerAsync()
        {
            await using var dbContext = CreateDbContext();
            var drafts = (await dbContext.Drafts.AsNoTracking().ToListAsync())
                .OrderBy(d => d.Id)
                .Select(d => $"{d.Id}:{d.PlanningJobId}:{d.Status}:{d.ApprovedAtUtc:O}:{d.ApprovedCandidatePayload}");
            var reservations = (await dbContext.Reservations.AsNoTracking().ToListAsync()).OrderBy(r => r.Id).Select(Describe);
            var locks = (await dbContext.LockedTaskAssignments.AsNoTracking().ToListAsync()).OrderBy(l => l.Id).Select(Describe);
            var progress = (await dbContext.DraftTaskProgress.AsNoTracking().ToListAsync()).OrderBy(p => p.Id).Select(Describe);
            var br101 = await dbContext.SpareParts.AsNoTracking().SingleAsync(p => p.Id == "BR-101");
            return string.Join("\n", drafts.Concat(reservations).Concat(locks).Concat(progress)) +
                   $"\nBR-101 onHand={br101.OnHandQuantity:0.###} reserved={br101.ReservedQuantity:0.###}";
        }

        public async Task AssertStockMatchesConfirmedReservationsAsync(decimal onHand, decimal reserved)
        {
            await using var dbContext = CreateDbContext();
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            var confirmedTotal = (await dbContext.Reservations
                    .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                    .ToListAsync())
                .Sum(r => r.QuantityReserved);
            Assert.Equal((onHand, reserved, reserved), (br101.OnHandQuantity, br101.ReservedQuantity, confirmedTotal));
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
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

        private static string Describe(LockedTaskAssignment l) =>
            $"{l.Id}:{l.DraftId}:{l.TaskId}:{l.LockedTechnicianId}:{l.CreatedAtUtc:O}";

        private static string Describe(DraftTaskProgress p) =>
            $"{p.Id}:{p.DraftId}:{p.TaskId}:{p.Status}:{p.UpdatedAtUtc:O}";
    }

    /// <summary>Deterministic stand-in for the Ollama interpreter: the P-101 bearing replacement.</summary>
    private sealed class BearingReplacementInterpreter : IPlanningRequestInterpreter
    {
        public Task<PlanningInterpretation> InterpretAsync(
            PlanningRequest request,
            IReadOnlyList<AssetReadModel> authoritativeAssets,
            IReadOnlyList<MaintenanceTaskReadModel> authoritativeTasks,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new PlanningInterpretation("Bearing replacement", "Replace the P-101 bearing", ["P-101"], ["P101-BEARING-REPLACEMENT"], [], [])
                {
                    ScopeResolution = PlanningScopeResolution.Resolved,
                    ScopeMappings = [new PlanningRequestScopeMapping("P101-BEARING-REPLACEMENT", "P-101", null)]
                });
    }

    /// <summary>Returns P-101 manual evidence for every search.</summary>
    private sealed class P101EvidenceRetriever : IManualEvidenceRetriever
    {
        public Task<IReadOnlyList<PlanningEvidenceReference>> SearchAsync(
            string query,
            string? documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlanningEvidenceReference>>(
            [
                new PlanningEvidenceReference(
                    PlanningEvidenceSourceKind.MaintenanceManual,
                    "MAN-P101-001:Planning assumptions: Bearing replacement",
                    "MAN-P101-001",
                    "1.0",
                    null,
                    "Planning assumptions: Bearing replacement",
                    "Schedule bearing replacement after preparation. Planning estimate: 180 minutes.")
            ]);
    }
}
