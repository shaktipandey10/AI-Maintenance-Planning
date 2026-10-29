using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// Stage 1: approval of the explicitly selected, revalidated candidate and atomic V1→V2 replacement
/// against the single seeded BR-101 unit.
/// </summary>
public sealed class Stage1ApprovalReplacementTests
{
    [Fact]
    public async Task Replacement_V2ReusesV1sLastUnit_V1SupersededAndReservationsSwappedAtomically()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, v1JobId);
            await ApprovalTestSupport.AddSucceededJobAsync(
                dbContext, v2JobId, v1JobId, ApprovalTestSupport.LowestCostCandidate());
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var v1 = await approvalService.ApproveDraftAsync(v1JobId, CancellationToken.None);
        Assert.True(v1.Succeeded, v1.FailureReason);

        var v2 = await approvalService.ApproveDraftReplacementAsync(v2JobId, CancellationToken.None);

        Assert.True(v2.Succeeded, v2.FailureReason);
        Assert.NotEqual(v1.DraftId, v2.DraftId);
        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1Draft = await dbContext.Drafts.Include(d => d.Reservations).SingleAsync(d => d.Id == v1.DraftId);
            Assert.Equal(DraftStatus.Superseded, v1Draft.Status);
            Assert.All(v1Draft.Reservations, r => Assert.Equal(ReservationStatus.Cancelled, r.Status));

            var v2Draft = await dbContext.Drafts.Include(d => d.Reservations).SingleAsync(d => d.Id == v2.DraftId);
            Assert.Equal(DraftStatus.Approved, v2Draft.Status);
            var v2Reservation = Assert.Single(v2Draft.Reservations);
            Assert.Equal(ReservationStatus.Confirmed, v2Reservation.Status);
            Assert.Equal("BR-101", v2Reservation.SparePartId);

            var confirmed = await dbContext.Reservations
                .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Equal(v2.DraftId, Assert.Single(confirmed).DraftId);

            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.OnHandQuantity);
            Assert.Equal(1m, br101.ReservedQuantity);
        }
    }

    [Fact]
    public async Task SelectedCandidate2_IsTheCandidateApprovedSnapshottedAndLocked()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        var job = ApprovalTestSupport.CreateSucceededJob(
            jobId, null, ApprovalTestSupport.StableIdCandidate(), ApprovalTestSupport.LowestCostCandidate());
        await using (var dbContext = fixture.CreateDbContext())
        {
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result = await approvalService.ApproveDraftAsync(jobId, candidateIndex: 1, CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.Include(d => d.Reservations).SingleAsync(d => d.Id == result.DraftId);
            Assert.Equal(DraftStatus.Approved, draft.Status);
            Assert.Equal(ApprovalTestSupport.CandidateJson(job, 1), draft.ApprovedCandidatePayload);
            Assert.NotEqual(ApprovalTestSupport.CandidateJson(job, 0), draft.ApprovedCandidatePayload);

            // Candidate 2 prepares with TECH-003; candidate 1 would have locked TECH-001.
            var prepLock = await dbContext.LockedTaskAssignments.SingleAsync(l => l.DraftId == draft.Id && l.TaskId == "P101-PREP");
            Assert.Equal("TECH-003", prepLock.LockedTechnicianId);
            Assert.Equal(3, await dbContext.LockedTaskAssignments.CountAsync(l => l.DraftId == draft.Id));

            var reservation = Assert.Single(draft.Reservations);
            Assert.Equal(("BR-101", 1m, ReservationStatus.Confirmed),
                (reservation.SparePartId, reservation.QuantityReserved, reservation.Status));
        }
    }

    [Fact]
    public async Task MultipleCandidatesWithoutSelection_IsRejectedAndNothingIsReserved()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddSucceededJobAsync(
                dbContext, jobId, null, ApprovalTestSupport.StableIdCandidate(), ApprovalTestSupport.LowestCostCandidate());
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var unselected = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        var outOfRange = await approvalService.ApproveDraftAsync(jobId, candidateIndex: 2, CancellationToken.None);

        Assert.False(unselected.Succeeded);
        Assert.Equal("CANDIDATE_SELECTION_REQUIRED", unselected.ConflictData!.Reason);
        Assert.False(outOfRange.Succeeded);
        Assert.Equal("CANDIDATE_NOT_FOUND", outOfRange.ConflictData!.Reason);
        await AssertNothingApprovedAsync(fixture);
    }

    [Fact]
    public async Task StaleCandidate_TechnicianNoLongerAvailable_IsRejectedAtApproval()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId);

            // After planning, TECH-002 (assigned to the bearing replacement) loses all availability.
            dbContext.TechnicianAvailabilities.RemoveRange(
                dbContext.TechnicianAvailabilities.Where(a => a.TechnicianId == "TECH-002"));
            await dbContext.SaveChangesAsync();
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("CANDIDATE_NO_LONGER_VALID", result.ConflictData!.Reason);
        Assert.Equal("TECHNICIAN_UNAVAILABLE", result.ConflictData.ResourceId);
        await AssertNothingApprovedAsync(fixture);
    }

    [Fact]
    public async Task InfeasibleOrEmptyCandidate_IsRejectedAtApproval()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var invalidJobId = Guid.NewGuid();
        var emptyJobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddSucceededJobAsync(
                dbContext, invalidJobId, null, ApprovalTestSupport.InvalidDurationCandidate());
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, emptyJobId, null, new
            {
                taskPlacements = Array.Empty<object>(),
                rationale = "Hand-built candidate without placements",
                evidenceReferences = Array.Empty<object>(),
                requestedTaskIds = new[] { "P101-BEARING-REPLACEMENT" },
                dependencyTaskIds = Array.Empty<string>()
            });
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var invalid = await approvalService.ApproveDraftAsync(invalidJobId, CancellationToken.None);
        var empty = await approvalService.ApproveDraftAsync(emptyJobId, CancellationToken.None);

        Assert.False(invalid.Succeeded);
        Assert.Equal("CANDIDATE_NO_LONGER_VALID", invalid.ConflictData!.Reason);
        Assert.Contains("TASK_DURATION_MISMATCH", invalid.FailureReason);
        Assert.False(empty.Succeeded);
        Assert.Equal("CANDIDATE_HAS_NO_TASKS", empty.ConflictData!.Reason);
        await AssertNothingApprovedAsync(fixture);
    }

    [Fact]
    public async Task FailedReplacement_StockShortfall_LeavesV1AndItsReservationUnchanged()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, v1JobId);
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, v2JobId, v1JobId);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var v1 = await approvalService.ApproveDraftAsync(v1JobId, CancellationToken.None);
        Assert.True(v1.Succeeded, v1.FailureReason);
        var before = await SnapshotAsync(fixture, v1.DraftId!.Value);

        // A stock correction removes the only bearing, so even V1's released unit cannot cover V2.
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            br101.OnHandQuantity = 0m;
            await dbContext.SaveChangesAsync();
        }

        var v2 = await approvalService.ApproveDraftReplacementAsync(v2JobId, CancellationToken.None);

        Assert.False(v2.Succeeded);
        Assert.Equal("INSUFFICIENT_INVENTORY", v2.ConflictData!.Reason);
        Assert.Equal(before, await SnapshotAsync(fixture, v1.DraftId.Value));
        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.False(await dbContext.Drafts.AnyAsync(d => d.PlanningJobId == v2JobId));
        }
    }

    [Fact]
    public async Task FailedReplacement_InvalidV2Candidate_LeavesV1AndItsReservationUnchanged()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, v1JobId);
            await ApprovalTestSupport.AddSucceededJobAsync(
                dbContext, v2JobId, v1JobId, ApprovalTestSupport.InvalidDurationCandidate());
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var v1 = await approvalService.ApproveDraftAsync(v1JobId, CancellationToken.None);
        Assert.True(v1.Succeeded, v1.FailureReason);
        var before = await SnapshotAsync(fixture, v1.DraftId!.Value);

        var v2 = await approvalService.ApproveDraftAsync(v2JobId, CancellationToken.None);

        Assert.False(v2.Succeeded);
        Assert.Equal("CANDIDATE_NO_LONGER_VALID", v2.ConflictData!.Reason);
        Assert.Equal(before, await SnapshotAsync(fixture, v1.DraftId.Value));
    }

    [Fact]
    public async Task RepeatedApprovalAndReplacementRequests_DoNotDuplicateDraftsOrReservations()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, v1JobId);
            await ApprovalTestSupport.AddSucceededJobAsync(
                dbContext, v2JobId, v1JobId, ApprovalTestSupport.StableIdCandidate(), ApprovalTestSupport.LowestCostCandidate());
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var v1First = await approvalService.ApproveDraftAsync(v1JobId, CancellationToken.None);
        var v1Repeat = await approvalService.ApproveDraftAsync(v1JobId, candidateIndex: 0, CancellationToken.None);
        Assert.True(v1First.Succeeded, v1First.FailureReason);
        Assert.Equal(v1First.DraftId, v1Repeat.DraftId);

        var v2Results = new List<DraftApprovalResult>();
        for (var attempt = 0; attempt < 3; attempt++)
            v2Results.Add(await approvalService.ApproveDraftReplacementAsync(v2JobId, candidateIndex: 1, CancellationToken.None));
        var v2Unspecified = await approvalService.ApproveDraftAsync(v2JobId, CancellationToken.None);
        var v2Mismatch = await approvalService.ApproveDraftAsync(v2JobId, candidateIndex: 0, CancellationToken.None);
        var v1AfterReplacement = await approvalService.ApproveDraftAsync(v1JobId, CancellationToken.None);

        Assert.All(v2Results, r => Assert.True(r.Succeeded, r.FailureReason));
        Assert.Single(v2Results.Select(r => r.DraftId).Distinct());
        Assert.Equal(v2Results[0].DraftId, v2Unspecified.DraftId);
        Assert.Equal("CANDIDATE_SELECTION_MISMATCH", v2Mismatch.ConflictData!.Reason);
        Assert.Equal("DRAFT_SUPERSEDED", v1AfterReplacement.ConflictData!.Reason);

        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.Equal(1, await dbContext.Drafts.CountAsync(d => d.PlanningJobId == v2JobId));
            Assert.Equal(2, await dbContext.Reservations.CountAsync());
            var confirmed = await dbContext.Reservations.SingleAsync(r => r.Status == ReservationStatus.Confirmed);
            Assert.Equal(v2Results[0].DraftId, confirmed.DraftId);
            Assert.Equal(1m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
            Assert.Equal(3, await dbContext.LockedTaskAssignments.CountAsync(l => l.DraftId == v2Results[0].DraftId));
        }
    }

    [Fact]
    public async Task CompetingApprovalsForLastUnit_ReplacementAndIndependentPlanInParallel_OnlyReplacementGetsIt()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        var otherJobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, v1JobId);
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, v2JobId, v1JobId);
            // The independent plan uses its own crew, so it competes with V2 only for the last BR-101,
            // not for technician time (approval rejects cross-plan technician overlaps first).
            await AddTwoPersonCrewAsync(dbContext, "TECH-004", "TECH-005");
            await ApprovalTestSupport.AddSucceededJobAsync(
                dbContext, otherJobId, null, TwoPersonCrewCandidate("TECH-004", "TECH-005"));
        }

        var v1 = await fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>()
            .ApproveDraftAsync(v1JobId, CancellationToken.None);
        Assert.True(v1.Succeeded, v1.FailureReason);

        var results = await Task.WhenAll(
            ApproveInNewScopeAsync(fixture, v2JobId),
            ApproveInNewScopeAsync(fixture, otherJobId));

        Assert.True(results[0].Succeeded, results[0].FailureReason);
        Assert.False(results[1].Succeeded);
        Assert.Equal("INSUFFICIENT_INVENTORY", results[1].ConflictData!.Reason);
        await using (var dbContext = fixture.CreateDbContext())
        {
            var confirmed = await dbContext.Reservations
                .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Equal(results[0].DraftId, Assert.Single(confirmed).DraftId);
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.ReservedQuantity);
            Assert.Equal(0m, br101.AvailableQuantity);
            Assert.False(await dbContext.Drafts.AnyAsync(d => d.PlanningJobId == otherJobId));
        }
    }

    [Fact]
    public async Task CompetingIndependentApprovalsForLastUnit_InParallel_ExactlyOneSucceeds()
    {
        await using var fixture = await ApprovalFixture.CreateAsync();
        var jobIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await using (var dbContext = fixture.CreateDbContext())
        {
            // Each plan has its own crew, so the four compete only for the last BR-101,
            // not for technician time (approval rejects cross-plan technician overlaps first).
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobIds[0]);
            (string Lead, string Second)[] extraCrews =
                [("TECH-004", "TECH-005"), ("TECH-006", "TECH-007"), ("TECH-008", "TECH-009")];
            for (var i = 0; i < extraCrews.Length; i++)
            {
                await AddTwoPersonCrewAsync(dbContext, extraCrews[i].Lead, extraCrews[i].Second);
                await ApprovalTestSupport.AddSucceededJobAsync(
                    dbContext, jobIds[i + 1], null, TwoPersonCrewCandidate(extraCrews[i].Lead, extraCrews[i].Second));
            }
        }

        var results = await Task.WhenAll(jobIds.Select(jobId => ApproveInNewScopeAsync(fixture, jobId)));

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded),
            r => Assert.Equal("INSUFFICIENT_INVENTORY", r.ConflictData!.Reason));
        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.Single(await dbContext.Drafts.ToListAsync());
            Assert.Single(await dbContext.Reservations.ToListAsync());
            Assert.Equal(1m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
        }
    }

    /// <summary>
    /// Adds a crew that can run the whole P-101 chain on its own during the baseline window: the lead prepares and
    /// joins the bearing replacement; the second joins the bearing replacement and verifies.
    /// </summary>
    private static async Task AddTwoPersonCrewAsync(MaintenancePlannerDbContext dbContext, string lead, string second)
    {
        await dbContext.Technicians.AddRangeAsync(
            CrewTechnician(lead, "MECHANICAL", "ROTATING-EQUIPMENT"),
            CrewTechnician(second, "ROTATING-EQUIPMENT", "INSTRUMENTATION"));
        await dbContext.SaveChangesAsync();
    }

    private static Technician CrewTechnician(string id, params string[] skills) => new()
    {
        Id = id,
        Name = $"Test technician {id}",
        HourlyRate = 40m,
        TechnicalSkills = skills.Select(skill => new TechnicalSkill { TechnicianId = id, SkillCode = skill }).ToList(),
        TechnicianAvailabilities =
        [
            new TechnicianAvailability
            {
                TechnicianId = id,
                AvailableFrom = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
                AvailableTo = new DateTimeOffset(2026, 10, 24, 16, 0, 0, TimeSpan.FromHours(5.5))
            }
        ]
    };

    /// <summary>The same P-101 timings as the stable-ID plan (requires one BR-101), staffed by a two-person crew.</summary>
    private static object TwoPersonCrewCandidate(string lead, string second) => new
    {
        taskPlacements = new[]
        {
            ApprovalTestSupport.Placement("P101-PREP", 8, 9, lead),
            ApprovalTestSupport.Placement("P101-BEARING-REPLACEMENT", 9, 12, lead, second),
            ApprovalTestSupport.Placement("P101-VERIFICATION", 12, 14, second)
        },
        rationale = $"Plan staffed by {lead} and {second}",
        evidenceReferences = Array.Empty<object>(),
        requestedTaskIds = new[] { "P101-BEARING-REPLACEMENT" },
        dependencyTaskIds = new[] { "P101-PREP", "P101-VERIFICATION" }
    };

    private static async Task<DraftApprovalResult> ApproveInNewScopeAsync(ApprovalFixture fixture, Guid jobId)
    {
        await Task.Yield();
        await using var scope = fixture.ServiceProvider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
            .ApproveDraftAsync(jobId, CancellationToken.None);
    }

    private static async Task AssertNothingApprovedAsync(ApprovalFixture fixture)
    {
        await using var dbContext = fixture.CreateDbContext();
        Assert.Empty(await dbContext.Drafts.ToListAsync());
        Assert.Empty(await dbContext.Reservations.ToListAsync());
        Assert.Empty(await dbContext.LockedTaskAssignments.ToListAsync());
        Assert.Equal(0m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
    }

    private static async Task<string> SnapshotAsync(ApprovalFixture fixture, Guid draftId)
    {
        await using var dbContext = fixture.CreateDbContext();
        var draft = await dbContext.Drafts.AsNoTracking().Include(d => d.Reservations).SingleAsync(d => d.Id == draftId);
        var reserved = (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity;
        var reservations = string.Join(",", draft.Reservations
            .OrderBy(r => r.Id)
            .Select(r => $"{r.Id}:{r.SparePartId}:{r.QuantityReserved}:{r.Status}:{r.CompletedAtUtc:O}"));
        return $"{draft.Status}|{draft.ApprovedAtUtc:O}|{draft.ApprovedCandidatePayload}|{reservations}|reserved={reserved}";
    }

    internal sealed class ApprovalFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;
        private readonly ServiceProvider _serviceProvider;

        private ApprovalFixture(SqliteConnection keeperConnection, string connectionString, ServiceProvider serviceProvider)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            _serviceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider => _serviceProvider;

        public static async Task<ApprovalFixture> CreateAsync()
        {
            var connectionString =
                $"Data Source=Stage1-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();
            await using (var dbContext = new MaintenancePlannerDbContext(
                             new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                                 .UseSqlite(keeperConnection)
                                 .Options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            var services = new ServiceCollection();
            services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(connectionString));
            services.AddApprovalServices();

            return new ApprovalFixture(keeperConnection, connectionString, services.BuildServiceProvider());
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        public async ValueTask DisposeAsync()
        {
            await _serviceProvider.DisposeAsync();
            await _keeperConnection.DisposeAsync();
        }
    }
}
