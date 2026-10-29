using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// Stage 2A: approval rejects a plan that books a technician into an interval already committed by another
/// approved plan. Runs on a file-backed SQLite database (not the demo database) with BR-101 stock raised, so
/// technician time, not part scarcity, decides each outcome.
/// </summary>
public sealed class Stage2ATechnicianTimeConflictTests
{
    [Fact]
    public async Task SequentialOverlap_SecondPlanIsRejected_AndNothingIsPersistedForIt()
    {
        await using var fixture = await FileDbFixture.CreateAsync();
        var planA = Guid.NewGuid();
        var planB = Guid.NewGuid();
        await fixture.AddJobAsync(planA, ApprovalTestSupport.StableIdCandidate());
        // Same instants as plan A, expressed in UTC instead of +05:30
        await fixture.AddJobAsync(planB, StableIdCandidateInUtc());

        var a = await fixture.ApproveAsync(planA);
        Assert.True(a.Succeeded, a.FailureReason);
        var b = await fixture.ApproveAsync(planB);

        Assert.False(b.Succeeded);
        Assert.Equal("Technician", b.ConflictData!.ResourceType);
        Assert.Equal("TECHNICIAN_TIME_CONFLICT", b.ConflictData.Reason);
        Assert.Equal("TECH-001", b.ConflictData.ResourceId);
        Assert.Contains(a.DraftId!.Value.ToString(), b.FailureReason);

        await using var dbContext = fixture.CreateDbContext();
        Assert.False(await dbContext.Drafts.AnyAsync(d => d.PlanningJobId == planB));
        Assert.Equal(1, await dbContext.Drafts.CountAsync(d => d.ApprovedCandidatePayload != null));
        Assert.Equal(a.DraftId, (await dbContext.Reservations.SingleAsync()).DraftId);
        Assert.All(await dbContext.LockedTaskAssignments.ToListAsync(), l => Assert.Equal(a.DraftId, l.DraftId));
        Assert.Equal(3, await dbContext.LockedTaskAssignments.CountAsync());
        Assert.Equal(1m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
    }

    [Fact]
    public async Task ParallelOverlap_OnFileDatabase_AtMostOneApprovalCommitsTheTechnicianTime()
    {
        await using var fixture = await FileDbFixture.CreateAsync();
        var jobIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var jobId in jobIds)
            await fixture.AddJobAsync(jobId, ApprovalTestSupport.StableIdCandidate());

        var results = await Task.WhenAll(jobIds.Select(fixture.ApproveInNewScopeAsync));

        var winner = Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded),
            r => Assert.Equal("TECHNICIAN_TIME_CONFLICT", r.ConflictData!.Reason));

        await using var dbContext = fixture.CreateDbContext();
        var draft = Assert.Single(await dbContext.Drafts.ToListAsync());
        Assert.Equal((winner.DraftId!.Value, DraftStatus.Approved), (draft.Id, draft.Status));
        Assert.Equal(winner.DraftId, (await dbContext.Reservations.SingleAsync()).DraftId);
        Assert.Equal(3, await dbContext.LockedTaskAssignments.CountAsync(l => l.DraftId == winner.DraftId));
        Assert.Equal(3, await dbContext.LockedTaskAssignments.CountAsync());
        Assert.Equal(1m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
    }

    [Fact]
    public async Task ApprovalTransaction_OnFileDatabase_HoldsTheWriteLockFromBegin()
    {
        // Approval begins its transaction with DbContext.Database.BeginTransactionAsync. On SQLite that takes the
        // RESERVED (write) lock immediately, which is what serializes concurrent approvals' read-check-write.
        await using var fixture = await FileDbFixture.CreateAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MaintenancePlannerDbContext>();
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        await using var otherConnection = new SqliteConnection(fixture.ConnectionString(defaultTimeoutSeconds: 1));
        await otherConnection.OpenAsync();
        await using var command = otherConnection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";

        var exception = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(5, exception.SqliteErrorCode); // SQLITE_BUSY: the write lock is already held
    }

    [Fact]
    public async Task AdjacentIntervals_SameTechnician_BothApprove()
    {
        await using var fixture = await FileDbFixture.CreateAsync();
        var planA = Guid.NewGuid();
        var planB = Guid.NewGuid();
        await fixture.AddJobAsync(planA, ApprovalTestSupport.StableIdCandidate());
        // TECH-001's bearing work in plan A ends at 12:00 +05:30 (06:30Z); plan B starts it again exactly then
        await fixture.AddJobAsync(planB, PrepOnlyCandidate("TECH-001",
            new DateTimeOffset(2026, 10, 24, 6, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 24, 7, 30, 0, TimeSpan.Zero)));

        var a = await fixture.ApproveAsync(planA);
        var b = await fixture.ApproveAsync(planB);

        Assert.True(a.Succeeded, a.FailureReason);
        Assert.True(b.Succeeded, b.FailureReason);
    }

    [Fact]
    public async Task OverlappingIntervals_EntirelyDifferentCrews_BothApprove()
    {
        await using var fixture = await FileDbFixture.CreateAsync();
        var planA = Guid.NewGuid();
        var planB = Guid.NewGuid();
        await fixture.AddJobAsync(planA, ApprovalTestSupport.StableIdCandidate());
        await fixture.AddJobAsync(planB, Candidate(
            ApprovalTestSupport.Placement("P101-PREP", 8, 9, "TECH-004"),
            ApprovalTestSupport.Placement("P101-BEARING-REPLACEMENT", 9, 12, "TECH-004", "TECH-005"),
            ApprovalTestSupport.Placement("P101-VERIFICATION", 12, 14, "TECH-005")));

        var a = await fixture.ApproveAsync(planA);
        var b = await fixture.ApproveAsync(planB);

        Assert.True(a.Succeeded, a.FailureReason);
        Assert.True(b.Succeeded, b.FailureReason);
        await using var dbContext = fixture.CreateDbContext();
        Assert.Equal(2m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
    }

    [Fact]
    public async Task Replacement_V2ReusesV1sTechnicianTime_WhenNoOtherPlanConflicts()
    {
        await using var fixture = await FileDbFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        await fixture.AddJobAsync(v1JobId, ApprovalTestSupport.StableIdCandidate());
        await fixture.AddJobAsync(v2JobId, ApprovalTestSupport.StableIdCandidate(), originalJobId: v1JobId);

        var v1 = await fixture.ApproveAsync(v1JobId);
        var v2 = await fixture.ApproveAsync(v2JobId);

        Assert.True(v1.Succeeded, v1.FailureReason);
        Assert.True(v2.Succeeded, v2.FailureReason);
        await using var dbContext = fixture.CreateDbContext();
        Assert.Equal(DraftStatus.Superseded, (await dbContext.Drafts.SingleAsync(d => d.Id == v1.DraftId)).Status);
        Assert.Equal(DraftStatus.Approved, (await dbContext.Drafts.SingleAsync(d => d.Id == v2.DraftId)).Status);
        Assert.Equal(v2.DraftId, (await dbContext.Reservations.SingleAsync(r => r.Status == ReservationStatus.Confirmed)).DraftId);
    }

    [Fact]
    public async Task Replacement_V2ConflictingWithAnotherApprovedPlan_IsRejected_AndV1IsUnchanged()
    {
        await using var fixture = await FileDbFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var otherJobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        await fixture.AddJobAsync(v1JobId, ApprovalTestSupport.StableIdCandidate());
        // Plan C books TECH-003 14:00-15:00, directly after V1's verification (12:00-14:00)
        await fixture.AddJobAsync(otherJobId, PrepOnlyCandidate("TECH-003", At(14), At(15)));
        // V2 shifts the whole chain one hour later, so its verification (13:00-15:00) overlaps plan C
        await fixture.AddJobAsync(v2JobId, Candidate(
            ApprovalTestSupport.Placement("P101-PREP", 9, 10, "TECH-001"),
            ApprovalTestSupport.Placement("P101-BEARING-REPLACEMENT", 10, 13, "TECH-001", "TECH-002"),
            ApprovalTestSupport.Placement("P101-VERIFICATION", 13, 15, "TECH-003")), originalJobId: v1JobId);

        var v1 = await fixture.ApproveAsync(v1JobId);
        var other = await fixture.ApproveAsync(otherJobId);
        Assert.True(v1.Succeeded, v1.FailureReason);
        Assert.True(other.Succeeded, other.FailureReason);
        var v1Before = await fixture.SnapshotDraftAsync(v1.DraftId!.Value);

        var v2 = await fixture.ApproveAsync(v2JobId);

        Assert.False(v2.Succeeded);
        Assert.Equal("TECHNICIAN_TIME_CONFLICT", v2.ConflictData!.Reason);
        Assert.Equal("TECH-003", v2.ConflictData.ResourceId);
        Assert.Contains(other.DraftId!.Value.ToString(), v2.FailureReason);
        Assert.Equal(v1Before, await fixture.SnapshotDraftAsync(v1.DraftId.Value));
        await using var dbContext = fixture.CreateDbContext();
        Assert.False(await dbContext.Drafts.AnyAsync(d => d.PlanningJobId == v2JobId));
        Assert.Equal(1m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
    }

    [Fact]
    public async Task Cancellation_ReleasesTechnicianTime_ForALaterPlan()
    {
        await using var fixture = await FileDbFixture.CreateAsync();
        var planA = Guid.NewGuid();
        var planB = Guid.NewGuid();
        await fixture.AddJobAsync(planA, ApprovalTestSupport.StableIdCandidate());
        await fixture.AddJobAsync(planB, ApprovalTestSupport.StableIdCandidate());

        var a = await fixture.ApproveAsync(planA);
        Assert.True(a.Succeeded, a.FailureReason);
        Assert.True((await fixture.CancelAsync(a.DraftId!.Value)).Succeeded);
        var b = await fixture.ApproveAsync(planB);

        Assert.True(b.Succeeded, b.FailureReason);
    }

    [Fact]
    public async Task SecondCrewMember_InNewPlan_OverlapIsDetected()
    {
        await using var fixture = await FileDbFixture.CreateAsync();
        var planA = Guid.NewGuid();
        var planB = Guid.NewGuid();
        // Plan A books only TECH-002, 10:00-11:00
        await fixture.AddJobAsync(planA, PrepOnlyCandidate("TECH-002", At(10), At(11)));
        // Plan B's bearing crew is [TECH-001, TECH-002] 09:00-12:00; only its second member conflicts
        await fixture.AddJobAsync(planB, ApprovalTestSupport.StableIdCandidate());

        var a = await fixture.ApproveAsync(planA);
        Assert.True(a.Succeeded, a.FailureReason);
        var b = await fixture.ApproveAsync(planB);

        Assert.False(b.Succeeded);
        Assert.Equal("TECHNICIAN_TIME_CONFLICT", b.ConflictData!.Reason);
        Assert.Equal("TECH-002", b.ConflictData.ResourceId);
    }

    [Fact]
    public async Task SecondCrewMember_InCommittedPlan_OverlapIsDetected()
    {
        await using var fixture = await FileDbFixture.CreateAsync();
        var planA = Guid.NewGuid();
        var planB = Guid.NewGuid();
        // Plan A's bearing crew is [TECH-001, TECH-002] 09:00-12:00; TECH-002 is not the locked (first) technician
        await fixture.AddJobAsync(planA, ApprovalTestSupport.StableIdCandidate());
        await fixture.AddJobAsync(planB, PrepOnlyCandidate("TECH-002", At(10), At(11)));

        var a = await fixture.ApproveAsync(planA);
        Assert.True(a.Succeeded, a.FailureReason);
        var b = await fixture.ApproveAsync(planB);

        Assert.False(b.Succeeded);
        Assert.Equal("TECHNICIAN_TIME_CONFLICT", b.ConflictData!.Reason);
        Assert.Equal("TECH-002", b.ConflictData.ResourceId);
    }

    [Fact]
    public async Task RepeatedApprovalOfApprovedPlan_RemainsIdempotent_AndDoesNotConflictWithItself()
    {
        await using var fixture = await FileDbFixture.CreateAsync();
        var planA = Guid.NewGuid();
        await fixture.AddJobAsync(planA, ApprovalTestSupport.StableIdCandidate());

        var first = await fixture.ApproveAsync(planA);
        var repeat = await fixture.ApproveAsync(planA);
        var repeatWithIndex = await fixture.ApproveAsync(planA, candidateIndex: 0);

        Assert.True(first.Succeeded, first.FailureReason);
        Assert.True(repeat.Succeeded, repeat.FailureReason);
        Assert.True(repeatWithIndex.Succeeded, repeatWithIndex.FailureReason);
        Assert.Equal(first.DraftId, repeat.DraftId);
        Assert.Equal(first.DraftId, repeatWithIndex.DraftId);
        await using var dbContext = fixture.CreateDbContext();
        Assert.Single(await dbContext.Drafts.ToListAsync());
        Assert.Single(await dbContext.Reservations.ToListAsync());
        Assert.Equal(3, await dbContext.LockedTaskAssignments.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("""{"outcome":"CandidatesGenerated","result":{"candidates":[]}}""")]
    public async Task ApprovedDraftWithUnreadableSnapshot_BlocksApproval_Conservatively(string? legacySnapshot)
    {
        await using var fixture = await FileDbFixture.CreateAsync();
        var legacyJobId = Guid.NewGuid();
        var legacyDraftId = Guid.NewGuid();
        var planB = Guid.NewGuid();
        await fixture.AddJobAsync(legacyJobId, ApprovalTestSupport.StableIdCandidate());
        await fixture.AddJobAsync(planB, ApprovalTestSupport.StableIdCandidate());
        await using (var dbContext = fixture.CreateDbContext())
        {
            // An approved draft from before snapshots were stored reliably
            await dbContext.Drafts.AddAsync(new Draft
            {
                Id = legacyDraftId,
                PlanningJobId = legacyJobId,
                Status = DraftStatus.Approved,
                ApprovedAtUtc = DateTimeOffset.UtcNow,
                ApprovedCandidatePayload = legacySnapshot
            });
            await dbContext.SaveChangesAsync();
        }

        var b = await fixture.ApproveAsync(planB);

        Assert.False(b.Succeeded);
        Assert.Equal("Draft", b.ConflictData!.ResourceType);
        Assert.Equal("APPROVED_SNAPSHOT_UNAVAILABLE", b.ConflictData.Reason);
        Assert.Equal(legacyDraftId.ToString(), b.ConflictData.ResourceId);
        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.False(await dbContext.Drafts.AnyAsync(d => d.PlanningJobId == planB));
            Assert.Empty(await dbContext.Reservations.ToListAsync());
            Assert.Empty(await dbContext.LockedTaskAssignments.ToListAsync());
        }
    }

    private static DateTimeOffset At(int hour) => new(2026, 10, 24, hour, 0, 0, TimeSpan.FromHours(5.5));

    private static object Candidate(params object[] taskPlacements) => new
    {
        taskPlacements,
        rationale = "Stage 2A test plan",
        evidenceReferences = Array.Empty<object>(),
        requestedTaskIds = new[] { "P101-BEARING-REPLACEMENT" },
        dependencyTaskIds = new[] { "P101-PREP", "P101-VERIFICATION" }
    };

    // Preparation has no predecessor and needs no part, so it is a valid plan on its own.
    private static object PrepOnlyCandidate(string technicianId, DateTimeOffset start, DateTimeOffset end) => new
    {
        taskPlacements = new object[] { new { taskId = "P101-PREP", start, end, technicianIds = new[] { technicianId } } },
        rationale = "Stage 2A preparation-only plan",
        evidenceReferences = Array.Empty<object>(),
        requestedTaskIds = new[] { "P101-PREP" },
        dependencyTaskIds = Array.Empty<string>()
    };

    private static object StableIdCandidateInUtc()
    {
        static DateTimeOffset Utc(int hour) => At(hour).ToUniversalTime();
        return Candidate(
            new { taskId = "P101-PREP", start = Utc(8), end = Utc(9), technicianIds = new[] { "TECH-001" } },
            new { taskId = "P101-BEARING-REPLACEMENT", start = Utc(9), end = Utc(12), technicianIds = new[] { "TECH-001", "TECH-002" } },
            new { taskId = "P101-VERIFICATION", start = Utc(12), end = Utc(14), technicianIds = new[] { "TECH-003" } });
    }

    private sealed class FileDbFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly ServiceProvider _services;

        private FileDbFixture(string databasePath, ServiceProvider services)
        {
            _databasePath = databasePath;
            _services = services;
        }

        public IServiceProvider Services => _services;

        public static async Task<FileDbFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"maintenance-planner-stage2a-{Guid.NewGuid():N}.db");
            var connectionString = BuildConnectionString(databasePath, defaultTimeoutSeconds: null);

            await using (var dbContext = new MaintenancePlannerDbContext(
                             new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(connectionString).Options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);

                // Enough bearings that technician time, not stock, decides every outcome
                (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).OnHandQuantity = 10m;

                // Two extra qualified technicians, available for the whole baseline window, for disjoint crews
                await dbContext.Technicians.AddRangeAsync(
                    TestTechnician("TECH-004", "MECHANICAL", "ROTATING-EQUIPMENT"),
                    TestTechnician("TECH-005", "MECHANICAL", "ROTATING-EQUIPMENT", "INSTRUMENTATION"));
                await dbContext.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(connectionString));
            services.AddApprovalServices();
            return new FileDbFixture(databasePath, services.BuildServiceProvider());
        }

        public string ConnectionString(int? defaultTimeoutSeconds = null) =>
            BuildConnectionString(_databasePath, defaultTimeoutSeconds);

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(ConnectionString()).Options);

        public async Task AddJobAsync(Guid jobId, object candidate, Guid? originalJobId = null)
        {
            await using var dbContext = CreateDbContext();
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId, originalJobId, candidate);
        }

        public async Task<DraftApprovalResult> ApproveAsync(Guid jobId, int? candidateIndex = null)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
                .ApproveDraftAsync(jobId, candidateIndex, CancellationToken.None);
        }

        public async Task<DraftApprovalResult> ApproveInNewScopeAsync(Guid jobId)
        {
            await Task.Yield();
            return await ApproveAsync(jobId);
        }

        public async Task<CancellationResult> CancelAsync(Guid draftId)
        {
            await using var scope = _services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
                .CancelDraftAsync(draftId, CancellationToken.None);
        }

        /// <summary>A draft's status, approval time, snapshot, reservations and locks, for before/after comparison.</summary>
        public async Task<string> SnapshotDraftAsync(Guid draftId)
        {
            await using var dbContext = CreateDbContext();
            var draft = await dbContext.Drafts.AsNoTracking().Include(d => d.Reservations).SingleAsync(d => d.Id == draftId);
            var reservations = string.Join(",", draft.Reservations
                .OrderBy(r => r.Id)
                .Select(r => $"{r.Id}:{r.SparePartId}:{r.QuantityReserved}:{r.Status}:{r.CompletedAtUtc:O}"));
            var locks = string.Join(",", (await dbContext.LockedTaskAssignments.AsNoTracking()
                    .Where(l => l.DraftId == draftId)
                    .ToListAsync())
                .OrderBy(l => l.TaskId)
                .Select(l => $"{l.Id}:{l.TaskId}:{l.LockedTechnicianId}"));
            return $"{draft.Status}|{draft.ApprovedAtUtc:O}|{draft.ApprovedCandidatePayload}|{reservations}|{locks}";
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

        private static string BuildConnectionString(string databasePath, int? defaultTimeoutSeconds)
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
            if (defaultTimeoutSeconds is { } timeout)
                builder.DefaultTimeout = timeout;
            return builder.ToString();
        }

        private static Technician TestTechnician(string id, params string[] skills) => new()
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
                    AvailableFrom = At(8),
                    AvailableTo = At(16)
                }
            ]
        };
    }
}
