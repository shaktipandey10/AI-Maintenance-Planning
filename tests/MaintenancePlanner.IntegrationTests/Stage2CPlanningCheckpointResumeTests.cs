using System.Text.Json;
using MaintenancePlanner.Api.Workers;
using MaintenancePlanner.Application.Locks;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Locks;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// Stage 2C: durable interpretation checkpoint and resume, exercised through the real worker, processor, planning
/// workflow, scheduler, repository and checkpoint writer on temporary file-backed SQLite databases. Only the request
/// interpreter (a counting stand-in for Ollama) and the manual-evidence retriever (which can block, to interrupt the
/// job right after its checkpoint is committed) are test doubles.
/// </summary>
public sealed class Stage2CPlanningCheckpointResumeTests
{
    private const string RequestText = "Replace the P-101 bearing.";

    [Fact]
    public async Task FreshJob_WritesValidatedCheckpointOnce_AndSucceeds()
    {
        var databasePath = NewTempDatabasePath();
        try
        {
            var interpreter = new CountingInterpreter();
            var evidence = new GatedEvidenceRetriever(blockUntilCancelled: false);
            await using var host = await CheckpointHost.CreateAsync(databasePath, interpreter, evidence);
            var job = await host.CreateJobAsync(Guid.NewGuid());

            var completed = await host.WaitForJobAsync(job.Id, j => j.Status == PlanningJobStatus.Succeeded);

            Assert.Equal(1, interpreter.Calls);
            Assert.Equal(1, completed.AttemptCount);
            Assert.Equal("CandidatesGenerated", Outcome(completed.ResultPayload));
            AssertValidCheckpointFor(completed);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task RestartAfterCheckpoint_ResumesWithoutReinterpreting_AndSucceeds()
    {
        var databasePath = NewTempDatabasePath();
        try
        {
            var interpreter = new CountingInterpreter();
            var operationId = Guid.NewGuid();
            PlanningJobReadModel job;
            string checkpointBeforeRestart;

            // First host: interpretation completes and is checkpointed; evidence retrieval then blocks
            var blockingEvidence = new GatedEvidenceRetriever(blockUntilCancelled: true);
            await using (var firstHost = await CheckpointHost.CreateAsync(databasePath, interpreter, blockingEvidence))
            {
                job = await firstHost.CreateJobAsync(operationId);
                await blockingEvidence.Reached.WaitAsync(TimeSpan.FromSeconds(10));

                var interrupted = await firstHost.ReadJobAsync(job.Id);
                Assert.Equal(PlanningJobStatus.Running, interrupted.Status);
                Assert.Equal(1, interpreter.Calls);
                AssertValidCheckpointFor(interrupted);
                checkpointBeforeRestart = interrupted.CheckpointPayload!;

                await firstHost.StopAsync();

                // Graceful stop released the job; the checkpoint survives it
                var released = await firstHost.ReadJobAsync(job.Id);
                Assert.Equal(PlanningJobStatus.Queued, released.Status);
                Assert.Equal(0, released.AttemptCount);
                Assert.Equal(checkpointBeforeRestart, released.CheckpointPayload);
            }

            // Second host on the same database file
            var evidence = new GatedEvidenceRetriever(blockUntilCancelled: false);
            await using (var secondHost = await CheckpointHost.CreateAsync(databasePath, interpreter, evidence))
            {
                var completed = await secondHost.WaitForJobAsync(job.Id, j => j.Status == PlanningJobStatus.Succeeded);

                Assert.Equal(1, interpreter.Calls); // interpretation was not repeated
                Assert.True(evidence.Calls > 0); // downstream planning ran on resume
                Assert.Equal("CandidatesGenerated", Outcome(completed.ResultPayload));
                Assert.NotEmpty(Candidates(completed.ResultPayload));
                Assert.Equal(1, completed.AttemptCount);
                Assert.Equal(checkpointBeforeRestart, completed.CheckpointPayload); // not overwritten on resume

                await secondHost.AssertSingleJobAndNoApprovalSideEffectsAsync(operationId, job.Id);
            }
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task CrashEquivalentAfterCheckpoint_LeaseExpiryReclaimResumesWithoutReinterpreting()
    {
        // Simulates the persisted state a killed process leaves behind (Running, expired lease of a dead owner,
        // checkpoint committed). It does not terminate a real process.
        var databasePath = NewTempDatabasePath();
        try
        {
            var interpreter = new CountingInterpreter();
            var operationId = Guid.NewGuid();
            PlanningJobReadModel job;
            string checkpoint;

            var blockingEvidence = new GatedEvidenceRetriever(blockUntilCancelled: true);
            await using (var firstHost = await CheckpointHost.CreateAsync(databasePath, interpreter, blockingEvidence))
            {
                job = await firstHost.CreateJobAsync(operationId);
                await blockingEvidence.Reached.WaitAsync(TimeSpan.FromSeconds(10));
                checkpoint = (await firstHost.ReadJobAsync(job.Id)).CheckpointPayload!;
                await firstHost.StopAsync();

                await using var dbContext = firstHost.CreateDbContext();
                await dbContext.PlanningJobs
                    .Where(existing => existing.Id == job.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(entity => entity.Status, PlanningJobStatus.Running)
                        .SetProperty(entity => entity.AttemptCount, 1)
                        .SetProperty(entity => entity.CurrentStep, "Processing")
                        .SetProperty(entity => entity.WorkerLeaseToken, Guid.NewGuid())
                        .SetProperty(entity => entity.ConcurrencyVersion, entity => entity.ConcurrencyVersion + 1)
                        .SetProperty(
                            entity => entity.LeaseExpiresAtUnixMilliseconds,
                            DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds()));
            }

            var evidence = new GatedEvidenceRetriever(blockUntilCancelled: false);
            await using (var secondHost = await CheckpointHost.CreateAsync(databasePath, interpreter, evidence))
            {
                var completed = await secondHost.WaitForJobAsync(job.Id, j => j.Status == PlanningJobStatus.Succeeded);

                Assert.Equal(1, interpreter.Calls);
                Assert.Equal(2, completed.AttemptCount); // a lease-expiry reclaim still counts an attempt
                Assert.Equal("CandidatesGenerated", Outcome(completed.ResultPayload));
                Assert.Equal(checkpoint, completed.CheckpointPayload);
                await secondHost.AssertSingleJobAndNoApprovalSideEffectsAsync(operationId, job.Id);
            }
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task CheckpointWrite_AdvancesSharedVersion_SoRenewalAndCompletionDoNotConflict()
    {
        var databasePath = NewTempDatabasePath();
        try
        {
            await using var host = await CheckpointHost.CreateAsync(
                databasePath, new CountingInterpreter(), new GatedEvidenceRetriever(false), startWorker: false);
            var job = await host.CreateJobAsync(Guid.NewGuid());
            await using var dbContext = host.CreateDbContext();
            var planningJobs = new PlanningJobs(dbContext);
            var claimed = (await planningJobs.ClaimNextAsync(
                Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), CancellationToken.None))!;
            var execution = new PlanningJobExecutionContext();
            execution.Begin(claimed);
            var writer = new PlanningJobCheckpointWriter(planningJobs, execution);

            // Interleave twenty lease renewals with the checkpoint write, as the worker and processor would
            await using var renewalDb = host.CreateDbContext();
            var renewalJobs = new PlanningJobs(renewalDb);
            var renewals = Enumerable.Range(0, 20).Select(_ => Task.Run(() => execution.AdvanceAsync(
                version => renewalJobs.RenewLeaseAsync(
                    claimed.Id, claimed.LeaseToken, version, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30),
                    CancellationToken.None),
                CancellationToken.None))).ToArray();
            var checkpointWrite = Task.Run(() => writer.SaveValidatedInterpretationAsync(
                CountingInterpreter.Interpretation(), CancellationToken.None));
            await Task.WhenAll(renewals.Cast<Task>().Append(checkpointWrite));

            Assert.All(renewals, renewal => Assert.NotNull(renewal.Result));
            Assert.Equal(claimed.ConcurrencyVersion + 21, execution.Version);

            var completed = await execution.FinishAsync(
                version => renewalJobs.CompleteAsync(
                    claimed.Id, claimed.LeaseToken, version, """{"outcome":"CandidatesGenerated"}""",
                    DateTimeOffset.UtcNow, CancellationToken.None),
                CancellationToken.None);

            Assert.True(completed);
            var persisted = await host.ReadJobAsync(claimed.Id);
            Assert.Equal(PlanningJobStatus.Succeeded, persisted.Status);
            Assert.Equal(claimed.ConcurrencyVersion + 22, persisted.ConcurrencyVersion);
            AssertValidCheckpointFor(persisted);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task StaleWorker_CannotOverwriteCheckpoint_OrCompleteJobReclaimedByAnotherWorker()
    {
        var databasePath = NewTempDatabasePath();
        try
        {
            await using var host = await CheckpointHost.CreateAsync(
                databasePath, new CountingInterpreter(), new GatedEvidenceRetriever(false), startWorker: false);
            var job = await host.CreateJobAsync(Guid.NewGuid());
            var leaseStart = DateTimeOffset.UtcNow;

            await using var workerADb = host.CreateDbContext();
            var jobsA = new PlanningJobs(workerADb);
            var claimA = (await jobsA.ClaimNextAsync(
                Guid.NewGuid(), leaseStart, TimeSpan.FromMilliseconds(1), CancellationToken.None))!;
            var executionA = new PlanningJobExecutionContext();
            executionA.Begin(claimA);

            await using var workerBDb = host.CreateDbContext();
            var jobsB = new PlanningJobs(workerBDb);
            var claimB = (await jobsB.ClaimNextAsync(
                Guid.NewGuid(), leaseStart.AddMilliseconds(2), TimeSpan.FromSeconds(30), CancellationToken.None))!;
            var executionB = new PlanningJobExecutionContext();
            executionB.Begin(claimB);
            await new PlanningJobCheckpointWriter(jobsB, executionB)
                .SaveValidatedInterpretationAsync(CountingInterpreter.Interpretation(), CancellationToken.None);
            var checkpointB = (await host.ReadJobAsync(job.Id)).CheckpointPayload;

            await Assert.ThrowsAsync<PlanningJobLeaseLostException>(() =>
                new PlanningJobCheckpointWriter(jobsA, executionA)
                    .SaveValidatedInterpretationAsync(CountingInterpreter.Interpretation(), CancellationToken.None));
            var staleCompleted = await executionA.FinishAsync(
                version => jobsA.CompleteAsync(
                    claimA.Id, claimA.LeaseToken, version, """{"outcome":"stale"}""",
                    DateTimeOffset.UtcNow, CancellationToken.None),
                CancellationToken.None);

            Assert.False(staleCompleted);
            var afterStale = await host.ReadJobAsync(job.Id);
            Assert.Equal(PlanningJobStatus.Running, afterStale.Status);
            Assert.Equal(claimB.LeaseToken, afterStale.WorkerLeaseToken);
            Assert.Equal(checkpointB, afterStale.CheckpointPayload);
            Assert.Null(afterStale.ResultPayload);

            // The owner completes with the version advanced by its own checkpoint write
            Assert.True(await executionB.FinishAsync(
                version => jobsB.CompleteAsync(
                    claimB.Id, claimB.LeaseToken, version, """{"outcome":"owner"}""",
                    DateTimeOffset.UtcNow, CancellationToken.None),
                CancellationToken.None));
            var final = await host.ReadJobAsync(job.Id);
            Assert.Equal(PlanningJobStatus.Succeeded, final.Status);
            Assert.Equal("""{"outcome":"owner"}""", final.ResultPayload);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    public static IEnumerable<object[]> UnusableCheckpoints()
    {
        yield return ["another job", (Func<Guid, string, string>)((_, request) =>
            PlanningInterpretationCheckpoint.Serialize(Guid.NewGuid(), request, CountingInterpreter.Interpretation(), DateTimeOffset.UtcNow))];
        yield return ["another request", (Func<Guid, string, string>)((jobId, _) =>
            PlanningInterpretationCheckpoint.Serialize(jobId, RequestPayload("Something else."), CountingInterpreter.Interpretation(), DateTimeOffset.UtcNow))];
        yield return ["legacy format", (Func<Guid, string, string>)((_, _) =>
            """{"interpretationPayload":"{\"maintenanceIntent\":\"Replace bearing\"}","savedAtUtc":"2026-10-24T00:00:00Z"}""")];
        yield return ["malformed", (Func<Guid, string, string>)((_, _) => "{ not json")];
    }

    [Theory]
    [MemberData(nameof(UnusableCheckpoints))]
    public async Task UnusableCheckpoint_FailsJobOnceWithInvalidCheckpoint_WithoutInterpreting(
        string description,
        Func<Guid, string, string> checkpointFor)
    {
        var databasePath = NewTempDatabasePath();
        try
        {
            var interpreter = new CountingInterpreter();
            await using var host = await CheckpointHost.CreateAsync(
                databasePath, interpreter, new GatedEvidenceRetriever(false), startWorker: false);
            var job = await host.CreateJobAsync(Guid.NewGuid());
            await using (var dbContext = host.CreateDbContext())
            {
                var checkpoint = checkpointFor(job.Id, RequestPayload(RequestText));
                await dbContext.PlanningJobs
                    .Where(existing => existing.Id == job.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.CheckpointPayload, checkpoint));
            }

            await host.StartWorkerAsync();
            var failed = await host.WaitForJobAsync(job.Id, j => j.Status == PlanningJobStatus.Failed);

            Assert.True(failed.FailureCode == "INVALID_CHECKPOINT", $"{description}: {failed.FailureCode} {failed.ErrorMessage}");
            Assert.Equal(1, failed.AttemptCount); // permanent: not retried with the same checkpoint
            Assert.Equal(0, interpreter.Calls);
            Assert.Null(failed.ResultPayload);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static void AssertValidCheckpointFor(PlanningJob job)
    {
        Assert.False(string.IsNullOrEmpty(job.CheckpointPayload));
        var readable = PlanningInterpretationCheckpoint.TryRead(
            job.CheckpointPayload!, job.Id, job.RequestPayload, out var interpretation, out var error);
        Assert.True(readable, error);
        Assert.Equal(new[] { "P101-BEARING-REPLACEMENT" }, interpretation!.ProposedTaskIds);

        using var document = JsonDocument.Parse(job.CheckpointPayload!);
        var root = document.RootElement;
        Assert.Equal(PlanningInterpretationCheckpoint.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(PlanningInterpretationCheckpoint.InterpretationValidatedStage, root.GetProperty("stage").GetString());
        Assert.Equal(job.Id, root.GetProperty("jobId").GetGuid());
    }

    private static string Outcome(string? resultPayload)
    {
        using var document = JsonDocument.Parse(resultPayload!);
        return document.RootElement.GetProperty("outcome").GetString()!;
    }

    private static List<JsonElement> Candidates(string? resultPayload)
    {
        using var document = JsonDocument.Parse(resultPayload!);
        return document.RootElement.GetProperty("result").GetProperty("candidates")
            .EnumerateArray().Select(candidate => candidate.Clone()).ToList();
    }

    private static string RequestPayload(string requestText) =>
        JsonSerializer.Serialize(
            new PlanningJobRequestPayload(requestText, ["P-101"], "MW-P101-BASELINE-20261024"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string NewTempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"planning-checkpoint-{Guid.NewGuid():N}.db");

    private static void DeleteDatabaseFiles(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm", $"{databasePath}-journal" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>Deterministic stand-in for the Ollama interpreter that counts its calls.</summary>
    private sealed class CountingInterpreter : IPlanningRequestInterpreter
    {
        private int _calls;

        public int Calls => _calls;

        public static PlanningInterpretation Interpretation() =>
            new("Bearing replacement", "Replace the P-101 bearing", ["P-101"], ["P101-BEARING-REPLACEMENT"], [], [])
            {
                ScopeResolution = PlanningScopeResolution.Resolved,
                ScopeMappings = [new PlanningRequestScopeMapping("P101-BEARING-REPLACEMENT", "P-101", null)]
            };

        public Task<PlanningInterpretation> InterpretAsync(
            PlanningRequest request,
            IReadOnlyList<AssetReadModel> authoritativeAssets,
            IReadOnlyList<MaintenanceTaskReadModel> authoritativeTasks,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Interpretation());
        }
    }

    /// <summary>
    /// Returns P-101 manual evidence. Evidence retrieval is the first step after the interpretation checkpoint, so a
    /// blocking instance holds the job just after its checkpoint is committed, until processing is cancelled.
    /// </summary>
    private sealed class GatedEvidenceRetriever(bool blockUntilCancelled) : IManualEvidenceRetriever
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => _calls;

        public Task Reached => _reached.Task;

        public async Task<IReadOnlyList<PlanningEvidenceReference>> SearchAsync(
            string query,
            string? documentId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _reached.TrySetResult();
            if (blockUntilCancelled)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return
            [
                new PlanningEvidenceReference(
                    PlanningEvidenceSourceKind.MaintenanceManual,
                    "MAN-P101-001:Planning assumptions: Bearing replacement",
                    "MAN-P101-001",
                    "1.0",
                    null,
                    "Planning assumptions: Bearing replacement",
                    "Schedule bearing replacement after preparation. Planning estimate: 180 minutes.")
            ];
        }
    }

    private sealed class CheckpointHost : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly WebApplication _app;
        private bool _stopped;

        private CheckpointHost(string connectionString, WebApplication app)
        {
            _connectionString = connectionString;
            _app = app;
        }

        public static async Task<CheckpointHost> CreateAsync(
            string databasePath,
            IPlanningRequestInterpreter interpreter,
            IManualEvidenceRetriever evidenceRetriever,
            bool startWorker = true)
        {
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
            builder.Services.AddApprovalServices(); // queries, spare-part provider, candidate validator, approvals
            builder.Services.AddScoped<CandidateCostCalculator>();
            builder.Services.AddScoped<DeterministicPlanningScheduler>();
            builder.Services.AddSingleton(evidenceRetriever);
            builder.Services.AddSingleton(interpreter);
            builder.Services.AddScoped<IPlanningWorkflow, PlanningWorkflow>();
            builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
            builder.Services.AddScoped<ITaskLockService, TaskLockService>();
            builder.Services.AddScoped<IPlanningJobProcessor, PlanningJobProcessor>();
            builder.Services.AddScoped<PlanningJobExecutionContext>();
            builder.Services.AddScoped<IPlanningInterpretationCheckpointSink, PlanningJobCheckpointWriter>();
            // A generous lease, so a slow (cold or heavily loaded) run does not lose it by accident; the crash test
            // expires its lease explicitly and the restart test relies on the graceful release.
            builder.Services.Configure<PlanningJobWorkerOptions>(options =>
            {
                options.PollInterval = TimeSpan.FromMilliseconds(10);
                options.LeaseDuration = TimeSpan.FromSeconds(10);
            });
            if (startWorker)
                builder.Services.AddHostedService<PlanningJobWorker>();

            var app = builder.Build();
            await app.StartAsync();
            return new CheckpointHost(connectionString, app);
        }

        private PlanningJobWorker? _manualWorker;

        public async Task StartWorkerAsync()
        {
            _manualWorker = new PlanningJobWorker(
                _app.Services.GetRequiredService<IServiceScopeFactory>(),
                _app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlanningJobWorkerOptions>>(),
                _app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PlanningJobWorker>>());
            await _manualWorker.StartAsync(CancellationToken.None);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        public async Task<PlanningJobReadModel> CreateJobAsync(Guid operationId)
        {
            await using var dbContext = CreateDbContext();
            return (await new PlanningJobs(dbContext).CreateAsync(
                operationId, RequestPayload(RequestText), CancellationToken.None)).Job;
        }

        public async Task<PlanningJob> ReadJobAsync(Guid jobId)
        {
            await using var dbContext = CreateDbContext();
            return await dbContext.PlanningJobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
        }

        public async Task<PlanningJob> WaitForJobAsync(Guid jobId, Func<PlanningJob, bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var job = await ReadJobAsync(jobId);
                if (condition(job))
                    return job;

                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }

        public async Task AssertSingleJobAndNoApprovalSideEffectsAsync(Guid operationId, Guid jobId)
        {
            await using var dbContext = CreateDbContext();
            Assert.Equal(jobId, (await dbContext.PlanningJobs.SingleAsync()).Id);
            var resubmitted = await new PlanningJobs(dbContext).CreateAsync(
                operationId, RequestPayload(RequestText), CancellationToken.None);
            Assert.Equal(jobId, resubmitted.Job.Id);
            Assert.False(resubmitted.PayloadConflict);
            Assert.Empty(await dbContext.Drafts.ToListAsync());
            Assert.Empty(await dbContext.Reservations.ToListAsync());
            Assert.Empty(await dbContext.LockedTaskAssignments.ToListAsync());
        }

        public async Task StopAsync()
        {
            if (_manualWorker is not null)
                await _manualWorker.StopAsync(CancellationToken.None);
            await _app.StopAsync();
            _stopped = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stopped)
                await StopAsync();
            await _app.DisposeAsync();
        }
    }
}
