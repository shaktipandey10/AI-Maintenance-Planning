using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT09JobRecoveryTests
{
    [Fact]
    public async Task AT09_CheckpointPersistenceAndRecovery_JobRecoveredWithCheckpoint()
    {
        // AT09: Interrupted job recovery with checkpoint reuse
        // 1. Create queued job and claim it
        // 2. Persist interpretation checkpoint
        // 3. Simulate interruption (lease expires)
        // 4. Reclaim the job
        // 5. Verify checkpoint is restored on reclaim
        // 6. Verify stale worker cannot renew lease
        // 7. Verify job can be completed after recovery

        await using var fixture = await JobRecoveryFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        var workerToken1 = Guid.NewGuid();
        var workerToken2 = Guid.NewGuid();

        // Step 1: Create and claim a queued planning job
        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Plan bearing replacement.","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Queued,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        var planningJobs = fixture.ServiceProvider.GetRequiredService<IPlanningJobs>();
        var leaseDuration = TimeSpan.FromSeconds(30);

        var claimed = await planningJobs.ClaimNextAsync(
            workerToken1,
            DateTimeOffset.UtcNow,
            leaseDuration,
            CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(jobId, claimed.Id);
        var originalVersion = claimed.ConcurrencyVersion;

        // Step 2: Persist interpretation checkpoint
        var checkpointJson = """
        {
          "interpretationPayload": {
            "maintenanceIntent": "Replace bearing",
            "scope": "P-101",
            "proposedAssetIds": ["P-101"],
            "proposedTaskIds": ["P101-BEARING-REPLACEMENT"],
            "missingInformation": [],
            "assumptions": [],
            "scopeResolution": "Resolved",
            "scopeMappings": [{"taskId": "P101-BEARING-REPLACEMENT", "assetId": "P-101"}]
          },
          "savedAtUtc": "2026-10-24T00:00:00Z"
        }
        """;

        var checkpointSaved = await planningJobs.SaveCheckpointAsync(
            jobId,
            workerToken1,
            originalVersion,
            checkpointJson,
            CancellationToken.None);

        Assert.True(checkpointSaved);

        // Verify checkpoint was persisted
        await using (var dbContext = fixture.CreateDbContext())
        {
            var jobAfterCheckpoint = await dbContext.PlanningJobs.SingleAsync(j => j.Id == jobId);
            Assert.NotEmpty(jobAfterCheckpoint.CheckpointPayload);
            Assert.Contains("interpretationPayload", jobAfterCheckpoint.CheckpointPayload);
        }

        // Step 3: Simulate interruption - worker stops without completing the job
        // The lease expires naturally (we don't renew it)

        // Step 4 & 5: After some time, recover and reclaim the expired job
        // Advance time past the lease expiration
        var recoverTime = DateTimeOffset.UtcNow.AddSeconds(leaseDuration.TotalSeconds + 5);

        var recoveredClaim = await planningJobs.ClaimNextAsync(
            workerToken2,  // Different worker for recovery
            recoverTime,
            leaseDuration,
            CancellationToken.None);

        Assert.NotNull(recoveredClaim);
        Assert.Equal(jobId, recoveredClaim.Id);
        Assert.NotEmpty(recoveredClaim.CheckpointPayload);
        Assert.Contains("interpretationPayload", recoveredClaim.CheckpointPayload);
        Assert.True(recoveredClaim.ConcurrencyVersion > originalVersion, "Version should increment on reclaim");

        // Step 6: Verify stale worker cannot renew lease
        var staleRenewal = await planningJobs.RenewLeaseAsync(
            jobId,
            workerToken1,  // Original (now stale) lease token
            originalVersion,
            recoverTime,
            leaseDuration,
            CancellationToken.None);

        Assert.Null(staleRenewal);

        // Step 7: Complete the job from recovery
        var resultPayload = """
        {
          "outcome": "CandidatesGenerated",
          "result": {
            "candidates": [],
            "assumptions": [],
            "evidenceReferences": [],
            "interpretation": null
          }
        }
        """;

        var completed = await planningJobs.CompleteAsync(
            jobId,
            workerToken2,
            recoveredClaim.ConcurrencyVersion,
            resultPayload,
            recoverTime,
            CancellationToken.None);

        Assert.True(completed);

        // Verify final state
        await using (var dbContext = fixture.CreateDbContext())
        {
            var finalJob = await dbContext.PlanningJobs.SingleAsync(j => j.Id == jobId);
            Assert.Equal(PlanningJobStatus.Succeeded, finalJob.Status);
            Assert.NotEmpty(finalJob.ResultPayload);
            Assert.NotNull(finalJob.CompletedAtUtc);
            // Checkpoint should still be present after completion (for audit/recovery history)
            Assert.NotEmpty(finalJob.CheckpointPayload);
        }
    }

    [Fact]
    public async Task AT09_LeaseExpirationPreventsStaleWorkerCompletion()
    {
        // Verify that a worker whose lease expires cannot persist results

        await using var fixture = await JobRecoveryFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        var workerToken = Guid.NewGuid();

        // Create and claim job
        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Plan work.","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Queued,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        var planningJobs = fixture.ServiceProvider.GetRequiredService<IPlanningJobs>();
        var leaseDuration = TimeSpan.FromSeconds(30);

        var claimed = await planningJobs.ClaimNextAsync(
            workerToken,
            DateTimeOffset.UtcNow,
            leaseDuration,
            CancellationToken.None);

        Assert.NotNull(claimed);
        var originalVersion = claimed.ConcurrencyVersion;

        // Don't renew the lease - let it expire
        var expirationTime = DateTimeOffset.UtcNow.AddSeconds(leaseDuration.TotalSeconds + 1);

        // Try to complete with the stale lease
        var resultPayload = """{"outcome": "CandidatesGenerated", "result": {}}""";
        var completed = await planningJobs.CompleteAsync(
            jobId,
            workerToken,
            originalVersion,
            resultPayload,
            expirationTime,
            CancellationToken.None);

        // Should fail because lease is expired
        Assert.False(completed, "Stale worker should not be able to complete job");

        // Verify job is still Running (not completed by stale worker)
        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = await dbContext.PlanningJobs.SingleAsync(j => j.Id == jobId);
            Assert.Equal(PlanningJobStatus.Running, job.Status);
            Assert.Null(job.ResultPayload);
            Assert.Null(job.CompletedAtUtc);
        }
    }

    private sealed class JobRecoveryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;

        private JobRecoveryFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            IServiceProvider serviceProvider)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            ServiceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider { get; }

        public static async Task<JobRecoveryFixture> CreateAsync()
        {
            var connectionString =
                $"Data Source=AT09-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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

            var services = new ServiceCollection();
            services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
            services.AddScoped<IPlanningJobs, PlanningJobs>();

            var serviceProvider = services.BuildServiceProvider();

            return new JobRecoveryFixture(keeperConnection, connectionString, serviceProvider);
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
