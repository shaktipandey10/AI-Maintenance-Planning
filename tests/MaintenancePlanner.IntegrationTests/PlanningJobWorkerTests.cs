using System.Text.Json;
using System.Net.Http.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Api.Workers;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class PlanningJobWorkerTests
{
    [Fact]
    public async Task Worker_ProcessesQueuedJobAndPersistsResultAndProgress()
    {
        var processor = new SuccessfulProcessor();
        await using var fixture = await WorkerFixture.CreateAsync(processor);

        var accepted = await fixture.SubmitJobAsync();
        var status = await fixture.WaitForStatusAsync(accepted.JobId, PlanningJobStatus.Succeeded);

        Assert.Equal(1, processor.CallCount);
        Assert.Equal(1, status.AttemptCount);
        Assert.Equal(2, status.MaxAttempts);
        Assert.Equal("Completed", status.CurrentStep);
        Assert.Equal("""{"plan":"test-plan"}""", status.ResultPayload);
        Assert.Null(status.FailureCode);
        Assert.Null(status.ErrorMessage);
        Assert.NotNull(status.CompletedAtUtc);
        await using var dbContext = fixture.CreateDbContext();
        var persisted = await dbContext.PlanningJobs.SingleAsync(job => job.Id == accepted.JobId);
        Assert.Null(persisted.LeaseExpiresAtUnixMilliseconds);
        Assert.Equal(2, persisted.MaxAttempts);
    }

    [Fact]
    public async Task Worker_PersistsTerminalFailureWithoutReportingSuccess()
    {
        await using var fixture = await WorkerFixture.CreateAsync(new FailingProcessor());

        var accepted = await fixture.SubmitJobAsync();
        var status = await fixture.WaitForStatusAsync(accepted.JobId, PlanningJobStatus.Failed);

        Assert.Equal(1, status.AttemptCount);
        Assert.Equal("Failed", status.CurrentStep);
        Assert.Null(status.ResultPayload);
        Assert.Equal("TEST_PROCESSING_FAILURE", status.FailureCode);
        Assert.Equal("The test processor failed.", status.ErrorMessage);
        Assert.NotNull(status.CompletedAtUtc);
    }

    [Fact]
    public async Task ProductionProcessor_FailsClearlyWhileWorkflowProviderIsUnavailable()
    {
        await using var fixture = await WorkerFixture.CreateAsync(new PlanningJobProcessor());

        var accepted = await fixture.SubmitJobAsync();
        var status = await fixture.WaitForStatusAsync(accepted.JobId, PlanningJobStatus.Failed);

        Assert.Equal("PLANNING_WORKFLOW_UNAVAILABLE", status.FailureCode);
        Assert.Contains("no supported request interpreter", status.ErrorMessage);
        Assert.Null(status.ResultPayload);
        Assert.NotEqual(PlanningJobStatus.Succeeded.ToString(), status.Status);
    }

    [Theory]
    [MemberData(nameof(WorkflowOutcomes))]
    public async Task Worker_PersistsEachTypedWorkflowBusinessOutcome(
        PlanningWorkflowResult result,
        string expectedOutcome)
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            new PlanningJobProcessor(new FixedWorkflow(result)));

        var accepted = await fixture.SubmitJobAsync();
        var status = await fixture.WaitForStatusAsync(accepted.JobId, PlanningJobStatus.Succeeded);

        using var payload = JsonDocument.Parse(status.ResultPayload!);
        Assert.Equal(expectedOutcome, payload.RootElement.GetProperty("outcome").GetString());
        Assert.Null(status.FailureCode);
        Assert.Null(status.ErrorMessage);
    }

    public static IEnumerable<object[]> WorkflowOutcomes
    {
        get
        {
            var interpretation = new PlanningInterpretation(
                "Bearing maintenance",
                "P-101 bearing",
                ["P-101"],
                ["P101-BEARING-REPLACEMENT"],
                [],
                [])
            {
                ScopeResolution = PlanningScopeResolution.Resolved
            };

            yield return [new PlanningCandidatesGeneratedResult([], [], []) { Interpretation = interpretation }, "CandidatesGenerated"];
            yield return [new PlanningClarificationRequiredResult(["Which task?"], [], []) { Interpretation = interpretation }, "ClarificationRequired"];
            yield return [new PlanningInfeasibleResult([], [], []) { Interpretation = interpretation }, "Infeasible"];
            yield return [new PlanningInvalidInputResult([], [], []) { Interpretation = interpretation }, "InvalidInput"];
            yield return [new PlanningDataUnavailableResult([], [], []) { Interpretation = interpretation }, "DataUnavailable"];
            yield return [new PlanningSearchLimitReachedResult([], [], []) { Interpretation = interpretation }, "SearchLimitReached"];
        }
    }

    [Fact]
    public async Task Worker_BoundsTransientRetriesByMaxAttempts()
    {
        var processor = new TransientFailingProcessor();
        await using var fixture = await WorkerFixture.CreateAsync(processor);

        var accepted = await fixture.SubmitJobAsync();
        var status = await fixture.WaitForStatusAsync(accepted.JobId, PlanningJobStatus.Failed);

        Assert.Equal(2, processor.CallCount);
        Assert.Equal(2, status.AttemptCount);
        Assert.Equal(status.MaxAttempts, status.AttemptCount);
        Assert.Equal("TEST_TRANSIENT_FAILURE", status.FailureCode);
        Assert.NotNull(status.CompletedAtUtc);
    }

    [Fact]
    public async Task Worker_ReclaimsExpiredRunningJobAfterRestart()
    {
        var processor = new SuccessfulProcessor();
        await using var fixture = await WorkerFixture.CreateAsync(processor, startWorker: false);
        var job = await fixture.CreateJobAsync();

        await using (var dbContext = fixture.CreateDbContext())
        {
            await dbContext.PlanningJobs
                .Where(existing => existing.Id == job.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(entity => entity.Status, PlanningJobStatus.Running)
                    .SetProperty(entity => entity.AttemptCount, 1)
                    .SetProperty(entity => entity.CurrentStep, "Processing")
                    .SetProperty(entity => entity.WorkerLeaseToken, Guid.NewGuid())
                    .SetProperty(
                        entity => entity.ConcurrencyVersion,
                        entity => entity.ConcurrencyVersion + 1)
                    .SetProperty(
                        entity => entity.LeaseExpiresAtUnixMilliseconds,
                        DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds()));
        }

        await fixture.StartWorkerAsync();
        var status = await fixture.WaitForStatusAsync(job.Id, PlanningJobStatus.Succeeded);

        Assert.Equal(1, processor.CallCount);
        Assert.Equal(2, status.AttemptCount);
        Assert.Equal("""{"plan":"test-plan"}""", status.ResultPayload);
    }

    [Fact]
    public async Task Worker_RecoversExpiredRunningJobAcrossFileBackedHostRestart()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"planning-job-restart-{Guid.NewGuid():N}.db");
        var interruptedProcessor = new BlockingProcessor();

        try
        {
            PlanningJobReadModel interruptedJob;
            await using (var firstHost = await WorkerFixture.CreateAsync(
                             interruptedProcessor,
                             startWorker: false,
                             databasePath: databasePath,
                             leaseDuration: TimeSpan.FromMilliseconds(300)))
            {
                interruptedJob = await firstHost.CreateJobAsync();
                await firstHost.StartWorkerAsync();
                await interruptedProcessor.WaitForStartAsync();

                await using var dbContext = firstHost.CreateDbContext();
                var interrupted = await dbContext.PlanningJobs.SingleAsync(job => job.Id == interruptedJob.Id);
                Assert.Equal(PlanningJobStatus.Running, interrupted.Status);
                Assert.Equal(1, interrupted.AttemptCount);
                Assert.NotNull(interrupted.WorkerLeaseToken);

                await firstHost.StopHostAsync();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(350));
            var recoveryProcessor = new SuccessfulProcessor();
            await using (var restartedHost = await WorkerFixture.CreateAsync(
                             recoveryProcessor,
                             databasePath: databasePath))
            {
                var recoveredStatus = await restartedHost.WaitForStatusAsync(
                    interruptedJob.Id,
                    PlanningJobStatus.Succeeded);

                Assert.Equal(1, interruptedProcessor.CallCount);
                Assert.Equal(1, recoveryProcessor.CallCount);
                Assert.Equal(2, recoveredStatus.AttemptCount);
                Assert.Equal("""{"plan":"test-plan"}""", recoveredStatus.ResultPayload);
                await using var dbContext = restartedHost.CreateDbContext();
                Assert.Single(await dbContext.PlanningJobs.ToListAsync());
            }
        }
        finally
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
            var walPath = $"{databasePath}-wal";
            if (File.Exists(walPath))
                File.Delete(walPath);
            var shmPath = $"{databasePath}-shm";
            if (File.Exists(shmPath))
                File.Delete(shmPath);
        }
    }

    [Fact]
    public async Task ClaimNextAsync_ConcurrentClaimsReturnJobToOnlyOneWorker()
    {
        await using var fixture = await WorkerFixture.CreateAsync(new SuccessfulProcessor(), startWorker: false);
        await fixture.CreateJobAsync();

        var claimTasks = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(async () =>
            {
                await using var dbContext = fixture.CreateDbContext();
                return await new PlanningJobs(dbContext).ClaimNextAsync(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None);
            }))
            .ToArray();

        var claims = await Task.WhenAll(claimTasks);

        Assert.Single(claims, claim => claim is not null);
    }

    [Fact]
    public async Task Worker_StaleLeaseOwnerCannotCompleteOrFailAfterReclaim()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            new SuccessfulProcessor(),
            startWorker: false);
        var job = await fixture.CreateJobAsync();
        var leaseStart = DateTimeOffset.UtcNow;

        await using var workerADb = fixture.CreateDbContext();
        var workerA = await new PlanningJobs(workerADb).ClaimNextAsync(
            Guid.NewGuid(),
            leaseStart,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        Assert.NotNull(workerA);

        await using (var expireDb = fixture.CreateDbContext())
        {
            await expireDb.PlanningJobs
                .Where(existing => existing.Id == job.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        entity => entity.LeaseExpiresAtUnixMilliseconds,
                        leaseStart.AddMilliseconds(1).ToUnixTimeMilliseconds())
                    .SetProperty(entity => entity.ConcurrencyVersion, entity => entity.ConcurrencyVersion + 1));
        }

        var workerBLeaseStart = leaseStart.AddMilliseconds(2);
        await using var workerBDb = fixture.CreateDbContext();
        var workerB = await new PlanningJobs(workerBDb).ClaimNextAsync(
            Guid.NewGuid(),
            workerBLeaseStart,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        Assert.NotNull(workerB);
        Assert.NotEqual(workerA.LeaseToken, workerB.LeaseToken);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            new PlanningJobs(workerBDb).CompleteAsync(
                job.Id,
                workerB.LeaseToken,
                workerA.ConcurrencyVersion,
                """{"plan":"stale-version"}""",
                workerBLeaseStart.AddMilliseconds(1),
                CancellationToken.None));

        var jobsA = new PlanningJobs(workerADb);
        Assert.False(await jobsA.CompleteAsync(
            job.Id,
            workerA.LeaseToken,
            workerA.ConcurrencyVersion,
            """{"plan":"stale-A"}""",
            workerBLeaseStart.AddMilliseconds(1),
            CancellationToken.None));
        Assert.False(await jobsA.FailAsync(
            job.Id,
            workerA.LeaseToken,
            workerA.ConcurrencyVersion,
            "STALE_FAILURE",
            "Stale worker failure.",
            isTransient: false,
            workerBLeaseStart.AddMilliseconds(1),
            CancellationToken.None));

        await using (var verifyDb = fixture.CreateDbContext())
        {
            var current = await verifyDb.PlanningJobs.SingleAsync(existing => existing.Id == job.Id);
            Assert.Equal(PlanningJobStatus.Running, current.Status);
            Assert.Equal(workerB.LeaseToken, current.WorkerLeaseToken);
            Assert.Equal(workerB.ConcurrencyVersion, current.ConcurrencyVersion);
            Assert.Null(current.ResultPayload);
            Assert.Null(current.FailureCode);
        }
    }

    [Fact]
    public async Task TrackedPlanningJobUpdate_IncrementsVersionAndRejectsStaleWrite()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            new SuccessfulProcessor(),
            startWorker: false);
        var job = await fixture.CreateJobAsync();
        await using var firstContext = fixture.CreateDbContext();
        await using var staleContext = fixture.CreateDbContext();
        var firstCopy = await firstContext.PlanningJobs.SingleAsync(existing => existing.Id == job.Id);
        var staleCopy = await staleContext.PlanningJobs.SingleAsync(existing => existing.Id == job.Id);

        firstCopy.ErrorMessage = "First update";
        await firstContext.SaveChangesAsync();

        staleCopy.ErrorMessage = "Stale update";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());

        await using var verifyContext = fixture.CreateDbContext();
        var persisted = await verifyContext.PlanningJobs.SingleAsync(existing => existing.Id == job.Id);
        Assert.Equal(1, persisted.ConcurrencyVersion);
        Assert.Equal("First update", persisted.ErrorMessage);
    }

    [Fact]
    public async Task AT09_RecoveryAfterInterruptionPreservesCheckpointWithoutDuplication()
    {
        // AT09 Test: Interrupt job recovery without duplication
        // Sequence:
        // 1. Create a queued job
        // 2. Claim it and persist Running status and worker lease
        // 3. Complete and persist the interpretation checkpoint
        // 4. Simulate interruption by expiring the lease
        // 5. Reclaim the same job
        // 6. Verify the completed interpretation checkpoint is not duplicated
        // 7. Verify exactly one job exists with the checkpoint preserved
        // 8. Run the recovery processor and verify checkpoint was used (no reinterpretation)
        // 9. Verify exactly one coherent result exists (no duplication)

        var checkpointCapturingProcessor = new CheckpointCapturingProcessor();
        await using var fixture = await WorkerFixture.CreateAsync(checkpointCapturingProcessor, startWorker: false);
        var createdJob = await fixture.CreateJobAsync();
        var leaseStart = DateTimeOffset.UtcNow;

        await using var initialDb = fixture.CreateDbContext();
        var planningJobs = new PlanningJobs(initialDb);

        // Step 1 & 2: Claim the job and get Running status
        var claimed = await planningJobs.ClaimNextAsync(
            Guid.NewGuid(),
            leaseStart,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(createdJob.Id, claimed.Id);
        Assert.Null(claimed.CheckpointPayload);
        Assert.Equal(1, claimed.AttemptCount);

        // Step 3: Complete and persist the interpretation checkpoint
        var interpretationCheckpoint = new PlanningJobInterpretationCheckpoint(
            """{"interpretation":"resolved-scope"}""",
            leaseStart.AddMilliseconds(100));
        var checkpointJson = System.Text.Json.JsonSerializer.Serialize(
            interpretationCheckpoint,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        var saved = await planningJobs.SaveCheckpointAsync(
            createdJob.Id,
            claimed.LeaseToken,
            claimed.ConcurrencyVersion,
            checkpointJson,
            CancellationToken.None);
        Assert.True(saved);

        // Verify checkpoint persisted
        await using (var checkDb = fixture.CreateDbContext())
        {
            var withCheckpoint = await checkDb.PlanningJobs.SingleAsync(j => j.Id == createdJob.Id);
            Assert.Equal(checkpointJson, withCheckpoint.CheckpointPayload);
            Assert.Equal(1, withCheckpoint.AttemptCount);
        }

        // Step 4: Simulate interruption by expiring the lease
        await using (var expireDb = fixture.CreateDbContext())
        {
            await expireDb.PlanningJobs
                .Where(j => j.Id == createdJob.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.LeaseExpiresAtUnixMilliseconds, leaseStart.AddSeconds(-1).ToUnixTimeMilliseconds())
                    .SetProperty(j => j.ConcurrencyVersion, j => j.ConcurrencyVersion + 1));
        }

        // Step 5: Recovery - reclaim the job with expired lease
        await using (var recoveryDb = fixture.CreateDbContext())
        {
            var reclaimed = await new PlanningJobs(recoveryDb).ClaimNextAsync(
                Guid.NewGuid(),
                leaseStart.AddSeconds(1),
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            // Step 6 & 7: Verify no duplication and checkpoint preserved
            Assert.NotNull(reclaimed);
            Assert.Equal(createdJob.Id, reclaimed.Id);
            Assert.Equal(checkpointJson, reclaimed.CheckpointPayload);
            Assert.Equal(2, reclaimed.AttemptCount);
        }

        // Step 8: Simulate recovery processor receiving the reclaimed job with checkpoint
        await using (var processorDb = fixture.CreateDbContext())
        {
            var reclaimedJob = await processorDb.PlanningJobs.SingleAsync(j => j.Id == createdJob.Id);
            var result = await checkpointCapturingProcessor.ProcessAsync(
                reclaimedJob.Id,
                reclaimedJob.WorkerLeaseToken!.Value,
                new ClaimedPlanningJob(
                    reclaimedJob.Id,
                    reclaimedJob.RequestPayload,
                    reclaimedJob.ConcurrencyVersion,
                    reclaimedJob.AttemptCount,
                    reclaimedJob.MaxAttempts,
                    reclaimedJob.WorkerLeaseToken!.Value,
                    reclaimedJob.CheckpointPayload),
                CancellationToken.None);

            // Verify checkpoint was captured (processed)
            Assert.NotNull(checkpointCapturingProcessor.CapturedCheckpoint);
            Assert.Equal(reclaimedJob.CheckpointPayload, checkpointCapturingProcessor.CapturedCheckpoint);
        }

        // Step 9: Verify exactly one job exists with exactly one result and checkpoint intact
        await using (var finalDb = fixture.CreateDbContext())
        {
            var allJobs = await finalDb.PlanningJobs.ToListAsync();
            Assert.Single(allJobs);
            var job = allJobs[0];
            Assert.Equal(createdJob.Id, job.Id);
            Assert.Equal(checkpointJson, job.CheckpointPayload);
            Assert.Equal(2, job.AttemptCount);
        }
    }

    private sealed class CheckpointCapturingProcessor : IPlanningJobProcessor
    {
        public string? CapturedCheckpoint { get; private set; }

        public Task<PlanningJobProcessingResult> ProcessAsync(
            Guid jobId,
            Guid leaseToken,
            ClaimedPlanningJob job,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(job.CheckpointPayload))
            {
                CapturedCheckpoint = job.CheckpointPayload;
            }
            return Task.FromResult(new PlanningJobProcessingResult("""{"plan":"test-plan"}"""));
        }
    }

    [Fact]
    public async Task ClarificationReset_ResetsAttemptCountAndAllowsRetry()
    {
        // Regression test for clarification retry fix:
        // When clarification is submitted, UpdateAndResetAsync should reset AttemptCount to 0
        // so the job can be claimed and retried with a fresh attempt budget.

        await using var fixture = await WorkerFixture.CreateAsync(
            new SuccessfulProcessor(),
            startWorker: true);  // Start worker immediately
        var job = await fixture.CreateJobAsync();

        // Simulate job after clarification submission (would normally come from UpdateAndResetAsync)
        await using (var db = fixture.CreateDbContext())
        {
            // Manually set job to Queued with AttemptCount=0 (as UpdateAndResetAsync should do)
            await db.PlanningJobs
                .Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, PlanningJobStatus.Queued)
                    .SetProperty(j => j.CurrentStep, "Queued")
                    .SetProperty(j => j.AttemptCount, 0)
                    .SetProperty(j => j.ResultPayload, (string?)null)
                    .SetProperty(j => j.CheckpointPayload, (string?)null)
                    .SetProperty(j => j.ConcurrencyVersion, j => j.ConcurrencyVersion + 1));
        }

        // Verify job completes successfully after reset
        var status = await fixture.WaitForStatusAsync(job.Id, PlanningJobStatus.Succeeded);

        Assert.Equal(1, status.AttemptCount);
        Assert.Equal("""{"plan":"test-plan"}""", status.ResultPayload);
    }

    [Fact]
    public async Task ExecutionMigration_PreservesExistingQueuedJobs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new MaintenancePlannerDbContext(options);
        await dbContext.Database.MigrateAsync("20261022092859_AddPlanningJobs");

        var jobId = Guid.NewGuid();
        var runningJobId = Guid.NewGuid();
        var createdAtUtc = DateTimeOffset.UtcNow;
        const string requestPayload = """{"requestText":"Persist across migration","assetIds":["P-101"]}""";
        var queuedStatus = PlanningJobStatus.Queued.ToString();
        var runningStatus = PlanningJobStatus.Running.ToString();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PlanningJobs"
                ("Id", "RequestPayload", "Status", "CreatedAtUtc")
            VALUES
                ({jobId}, {requestPayload}, {queuedStatus}, {createdAtUtc})
            """);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PlanningJobs"
                ("Id", "RequestPayload", "Status", "CreatedAtUtc")
            VALUES
                ({runningJobId}, {requestPayload}, {runningStatus}, {createdAtUtc})
            """);

        await dbContext.Database.MigrateAsync();

        var persistedQueued = await dbContext.PlanningJobs.SingleAsync(job => job.Id == jobId);
        Assert.Equal(PlanningJobStatus.Queued, persistedQueued.Status);
        Assert.Equal(0, persistedQueued.AttemptCount);
        Assert.Equal(3, persistedQueued.MaxAttempts);
        Assert.Equal("Queued", persistedQueued.CurrentStep);
        Assert.Equal(requestPayload, persistedQueued.RequestPayload);
        Assert.Equal(createdAtUtc, persistedQueued.CreatedAtUtc);
        Assert.Equal(0, persistedQueued.ConcurrencyVersion);
        Assert.Null(persistedQueued.ResultPayload);
        Assert.Null(persistedQueued.WorkerLeaseToken);
        Assert.Null(persistedQueued.LeaseExpiresAtUnixMilliseconds);

        var persistedRunning = await dbContext.PlanningJobs.SingleAsync(job => job.Id == runningJobId);
        Assert.Equal(PlanningJobStatus.Running, persistedRunning.Status);
        Assert.Equal(3, persistedRunning.MaxAttempts);

        var newJobId = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PlanningJobs"
                ("Id", "RequestPayload", "Status", "CreatedAtUtc")
            VALUES
                ({newJobId}, {requestPayload}, {queuedStatus}, {createdAtUtc})
            """);
        var newlyInserted = await dbContext.PlanningJobs.SingleAsync(job => job.Id == newJobId);
        Assert.Equal(2, newlyInserted.MaxAttempts);
    }

    private sealed class SuccessfulProcessor : IPlanningJobProcessor
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<PlanningJobProcessingResult> ProcessAsync(
            Guid jobId,
            Guid leaseToken,
            ClaimedPlanningJob job,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PlanningJobProcessingResult("""{"plan":"test-plan"}"""));
        }

    }

    private sealed class FixedWorkflow(PlanningWorkflowResult result) : IPlanningWorkflow
    {
        public Task<PlanningWorkflowResult> ExecuteAsync(
            PlanningRequest request,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? lockedTaskAssignments = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }

        public Task<PlanningWorkflowResult> ResumeFromCheckpointAsync(
            PlanningRequest request,
            PlanningInterpretation checkpoint,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? lockedTaskAssignments = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingProcessor : IPlanningJobProcessor
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => _callCount;

        public async Task<PlanningJobProcessingResult> ProcessAsync(
            Guid jobId,
            Guid leaseToken,
            ClaimedPlanningJob job,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking processor should only exit through cancellation.");
        }

        public Task WaitForStartAsync() => _started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FailingProcessor : IPlanningJobProcessor
    {
        public Task<PlanningJobProcessingResult> ProcessAsync(
            Guid jobId,
            Guid leaseToken,
            ClaimedPlanningJob job,
            CancellationToken cancellationToken) =>
            throw new PlanningJobProcessingException(
                "TEST_PROCESSING_FAILURE",
                "The test processor failed.");
    }

    private sealed class TransientFailingProcessor : IPlanningJobProcessor
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<PlanningJobProcessingResult> ProcessAsync(
            Guid jobId,
            Guid leaseToken,
            ClaimedPlanningJob job,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            throw new PlanningJobProcessingException(
                "TEST_TRANSIENT_FAILURE",
                "The test processor temporarily failed.",
                isTransient: true);
        }
    }

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;
        private readonly WebApplication _app;
        private bool _workerStarted;
        private PlanningJobWorker? _worker;

        private WorkerFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            WebApplication app)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<WorkerFixture> CreateAsync(
            IPlanningJobProcessor processor,
            bool startWorker = true,
            string? databasePath = null,
            TimeSpan? leaseDuration = null)
        {
            var connectionString = databasePath is null
                ? $"Data Source=PlanningJobWorker-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False"
                : $"Data Source={databasePath};Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();
            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(keeperConnection)
                .Options;

            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
            builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
            builder.Services.AddSingleton<IPlanningJobProcessor>(processor);
            builder.Services.AddScoped<IDraftApprovalService, DraftApprovalService>();
            builder.Services.Configure<PlanningJobWorkerOptions>(workerOptions =>
            {
                workerOptions.PollInterval = TimeSpan.FromMilliseconds(10);
                workerOptions.LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(3);
            });
            if (startWorker)
                builder.Services.AddHostedService<PlanningJobWorker>();

            var app = builder.Build();
            app.MapPlanningJobEndpoints();
            await app.StartAsync();

            return new WorkerFixture(keeperConnection, connectionString, app)
            {
                _workerStarted = startWorker
            };
        }

        public async Task StartWorkerAsync()
        {
            if (_workerStarted)
                return;

            _worker = new PlanningJobWorker(
                _app.Services.GetRequiredService<IServiceScopeFactory>(),
                _app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlanningJobWorkerOptions>>(),
                _app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PlanningJobWorker>>());
            await _worker.StartAsync(CancellationToken.None);
            _workerStarted = true;
        }

        public async Task StopHostAsync()
        {
            if (_worker is not null)
                await _worker.StopAsync(CancellationToken.None);
            await _app.StopAsync();
        }

        public async Task<PlanningJobReadModel> CreateJobAsync()
        {
            await using var dbContext = CreateDbContext();
            return (await new PlanningJobs(dbContext).CreateAsync(
                Guid.NewGuid(),
                """{"requestText":"Plan bearing replacement for P-101.","assetIds":["P-101"]}""",
                CancellationToken.None)).Job;
        }

        public async Task<PlanningJobAcceptedResponse> SubmitJobAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
            {
                Content = JsonContent.Create(new CreatePlanningJobRequest
                {
                    RequestText = "Plan bearing replacement for P-101.",
                    AssetIds = ["P-101"]
                })
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>())!;
        }

        public async Task<PlanningJobStatusResponse> WaitForStatusAsync(
            Guid jobId,
            PlanningJobStatus expectedStatus)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!timeout.IsCancellationRequested)
            {
                using var response = await Client.GetAsync(
                    $"/api/planning-jobs/{jobId}",
                    timeout.Token);
                response.EnsureSuccessStatusCode();
                var status = (await response.Content.ReadFromJsonAsync<PlanningJobStatusResponse>(
                    timeout.Token))!;
                if (string.Equals(status.Status, expectedStatus.ToString(), StringComparison.Ordinal))
                    return status;

                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }

            throw new TimeoutException($"Job {jobId} did not reach {expectedStatus}.");
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (_worker is not null)
                await _worker.StopAsync(CancellationToken.None);
            await _app.StopAsync();
            await _app.DisposeAsync();
            await _keeperConnection.DisposeAsync();
        }
    }
}
