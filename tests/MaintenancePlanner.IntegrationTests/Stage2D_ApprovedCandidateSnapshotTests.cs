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
using Xunit;

namespace MaintenancePlanner.IntegrationTests;

public sealed class Stage2D_ApprovedCandidateSnapshotTests
{
    // Approval revalidates the selected candidate, so approvable payloads carry schedulable P-101 placements.
    private static readonly string StableIdPlacementsJson = JsonSerializer.Serialize(
        ApprovalTestSupport.StableIdPlacements(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static readonly string LowestCostPlacementsJson = JsonSerializer.Serialize(
        ApprovalTestSupport.LowestCostPlacements(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task Stage2D1_ApprovalPersistsApprovedCandidate_SnapshotCaptured()
    {
        // Verify: Approval persists the candidate actually used for reservations and locks
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        var candidatePayload = $$"""
            {
              "outcome": "CandidatesGenerated",
              "result": {
                "candidates": [
                  {
                    "taskPlacements": {{StableIdPlacementsJson}},
                    "rationale": "Test candidate",
                    "evidenceReferences": [],
                    "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                    "dependencyTaskIds": []
                  }
                ],
                "assumptions": [],
                "evidenceReferences": [],
                "interpretation": {
                  "maintenanceIntent": "Test",
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
            """;

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithPayloadAsync(dbContext, jobId, candidatePayload);
            // Verify payload was persisted
            var job = await dbContext.PlanningJobs.FirstAsync(j => j.Id == jobId);
            Assert.NotNull(job.ResultPayload);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        // If approval failed, get the reason
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Approval failed: {result.FailureReason ?? "unknown"}");
        }

        // Verify snapshot was persisted
        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.FirstAsync(d => d.Id == result.DraftId!.Value);
            Assert.NotNull(draft.ApprovedCandidatePayload);
            Assert.NotEmpty(draft.ApprovedCandidatePayload);

            // Verify it's valid JSON
            var doc = JsonDocument.Parse(draft.ApprovedCandidatePayload);
            Assert.NotNull(doc);
        }
    }

    [Fact]
    public async Task Stage2D1_ApprovedCandidateIsDeserializable_CanParseSnapshot()
    {
        // Verify: Snapshot content can be deserialized back into the expected candidate structure
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        var candidatePayload = $$"""
            {
              "outcome": "CandidatesGenerated",
              "result": {
                "candidates": [
                  {
                    "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                    "costEstimate": 500.00,
                    "taskPlacements": {{StableIdPlacementsJson}}
                  }
                ]
              }
            }
            """;

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithPayloadAsync(dbContext, jobId, candidatePayload);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        Assert.True(result.Succeeded);

        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.FirstAsync(d => d.Id == result.DraftId!.Value);

            // Deserialize and verify structure
            var candidateDoc = JsonDocument.Parse(draft.ApprovedCandidatePayload!);
            var root = candidateDoc.RootElement;

            // Should have the expected candidate structure
            Assert.True(root.TryGetProperty("requestedTaskIds", out var taskIds));
            Assert.True(root.TryGetProperty("taskPlacements", out var placements));
            Assert.Equal(1, taskIds.GetArrayLength());
        }
    }

    [Fact]
    public async Task Stage2D1_SourceCandidateReordering_DoesNotAffectApprovedSnapshot()
    {
        // Verify: Reordering or modifying the source candidate list after approval
        // does not change the persisted snapshot of the explicitly selected candidate
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        var originalPayload = $$"""
            {
              "outcome": "CandidatesGenerated",
              "result": {
                "candidates": [
                  {
                    "id": "candidate-1",
                    "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                    "costEstimate": 500.00,
                    "taskPlacements": {{StableIdPlacementsJson}}
                  },
                  {
                    "id": "candidate-2",
                    "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                    "costEstimate": 600.00,
                    "taskPlacements": {{LowestCostPlacementsJson}}
                  }
                ]
              }
            }
            """;

        Guid draftId;
        string capturedSnapshot;

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithPayloadAsync(dbContext, jobId, originalPayload);
        }

        // The job has two candidates, so the candidate to approve is selected explicitly
        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result = await approvalService.ApproveDraftAsync(jobId, candidateIndex: 0, CancellationToken.None);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Approval failed: {result.FailureReason ?? "unknown"}");
        }
        Assert.True(result.Succeeded);
        draftId = result.DraftId!.Value;

        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.FirstAsync(d => d.Id == draftId);
            capturedSnapshot = draft.ApprovedCandidatePayload!;

            // Verify it captured the selected candidate (index 0, "candidate-1")
            var doc = JsonDocument.Parse(capturedSnapshot);
            var root = doc.RootElement;
            var costEst = root.GetProperty("costEstimate").GetDecimal();
            Assert.Equal(500.00m, costEst); // Selected candidate
        }

        // Now simulate the source being reordered
        var reorderedPayload = $$"""
            {
              "outcome": "CandidatesGenerated",
              "result": {
                "candidates": [
                  {
                    "id": "candidate-2",
                    "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                    "costEstimate": 600.00,
                    "taskPlacements": {{LowestCostPlacementsJson}}
                  },
                  {
                    "id": "candidate-1",
                    "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                    "costEstimate": 500.00,
                    "taskPlacements": {{StableIdPlacementsJson}}
                  }
                ]
              }
            }
            """;

        // Update the planning job's result payload
        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = await dbContext.PlanningJobs.FirstAsync(j => j.Id == jobId);
            job.ResultPayload = reorderedPayload;
            await dbContext.SaveChangesAsync();

            // Verify the draft's snapshot hasn't changed
            var draft = await dbContext.Drafts.FirstAsync(d => d.Id == draftId);
            Assert.Equal(capturedSnapshot, draft.ApprovedCandidatePayload);
        }
    }

    [Fact]
    public async Task Stage2D1_ApprovalFailure_RollsBackSnapshot()
    {
        // Verify: Approval failure rolls back the snapshot and approval state together
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        // An otherwise valid P-101 plan in the baseline window, plus one placement for a task that does not exist
        // in the authoritative catalogue. The unknown task is the only reason approval can fail.
        var placementsWithNonexistentTask = JsonSerializer.Serialize(
            ApprovalTestSupport.StableIdPlacements()
                .Append(ApprovalTestSupport.Placement("NONEXISTENT-TASK-999", 14, 15, "TECH-001"))
                .ToArray(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var payloadWithNonexistentTask = $$"""
            {
              "outcome": "CandidatesGenerated",
              "result": {
                "candidates": [
                  {
                    "requestedTaskIds": ["NONEXISTENT-TASK-999"],
                    "taskPlacements": {{placementsWithNonexistentTask}}
                  }
                ]
              }
            }
            """;

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithPayloadAsync(dbContext, jobId, payloadWithNonexistentTask);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        // Should fail because the plan contains a nonexistent task, not because of window or empty-plan checks
        Assert.False(result.Succeeded);
        Assert.NotNull(result.ConflictData);
        Assert.Equal("CANDIDATE_NO_LONGER_VALID", result.ConflictData.Reason);
        Assert.Equal("TASK_NOT_FOUND", result.ConflictData.ResourceId);
        Assert.Contains("NONEXISTENT-TASK-999", result.FailureReason);

        // Verify no draft (and therefore no approved snapshot), reservation or lock was persisted
        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.PlanningJobId == jobId);
            Assert.Null(draft);
            Assert.False(await dbContext.Drafts.AnyAsync(d => d.ApprovedCandidatePayload != null));
            Assert.Empty(await dbContext.Reservations.ToListAsync());
            Assert.Empty(await dbContext.LockedTaskAssignments.ToListAsync());
            Assert.Equal(0m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
        }
    }

    [Fact]
    public async Task Stage2D1_RepeatedApproval_DoesNotReplaceSnapshot()
    {
        // Verify: Repeated approval does not silently replace the existing approved snapshot
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        var candidatePayload = $$"""
            {
              "outcome": "CandidatesGenerated",
              "result": {
                "candidates": [
                  {
                    "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                    "timestamp": "2026-10-25T10:00:00Z",
                    "taskPlacements": {{StableIdPlacementsJson}}
                  }
                ]
              }
            }
            """;

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithPayloadAsync(dbContext, jobId, candidatePayload);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // First approval
        var result1 = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.True(result1.Succeeded);
        var draftId = result1.DraftId!.Value;

        string snapshot1;
        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.FirstAsync(d => d.Id == draftId);
            snapshot1 = draft.ApprovedCandidatePayload!;
        }

        // Second (repeated) approval - should be idempotent
        var result2 = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.True(result2.Succeeded);
        Assert.Equal(draftId, result2.DraftId!.Value); // Same draft returned

        // Verify snapshot wasn't replaced
        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.FirstAsync(d => d.Id == draftId);
            Assert.Equal(snapshot1, draft.ApprovedCandidatePayload);
        }
    }

    [Fact]
    public async Task Stage2D1_ApprovedCandidateSnapshot_PreservesExistingApprovalBehavior()
    {
        // Verify: Existing approval, reservation, and revision behavior remains unchanged
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        var candidatePayload = $$"""
            {
              "outcome": "CandidatesGenerated",
              "result": {
                "candidates": [
                  {
                    "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                    "taskPlacements": {{StableIdPlacementsJson}}
                  }
                ]
              }
            }
            """;

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithPayloadAsync(dbContext, jobId, candidatePayload);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        Assert.True(result.Succeeded);

        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts
                .Include(d => d.Reservations)
                .FirstAsync(d => d.Id == result.DraftId!.Value);

            // Standard approval behavior
            Assert.Equal(DraftStatus.Approved, draft.Status);
            Assert.NotNull(draft.ApprovedAtUtc);
            Assert.NotEmpty(draft.Reservations);

            // New behavior
            Assert.NotNull(draft.ApprovedCandidatePayload);

            // Verify reservations are still created correctly
            var br101Reservations = draft.Reservations
                .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                .ToList();
            Assert.Single(br101Reservations);
        }
    }

    private static async Task CreatePlanningJobWithPayloadAsync(
        MaintenancePlannerDbContext dbContext,
        Guid jobId,
        string resultPayload)
    {
        var planningJob = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = ApprovalTestSupport.RequestPayload("Test job"),
            Status = Domain.Entities.PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ResultPayload = resultPayload
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
                $"Data Source=Stage2D-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();
            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(keeperConnection)
                .Options;

            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);

                var br101 = await dbContext.SpareParts.FirstOrDefaultAsync(p => p.Id == "BR-101");
                if (br101 is not null)
                {
                    br101.OnHandQuantity = 10;
                    await dbContext.SaveChangesAsync();
                }
            }

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
            builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
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
