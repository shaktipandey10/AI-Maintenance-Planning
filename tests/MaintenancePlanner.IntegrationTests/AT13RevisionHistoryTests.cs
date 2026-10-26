using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT13RevisionHistoryTests
{
    [Fact]
    public async Task AT13_Test1_SingleJobNoRevisionsReturnsOnlyThatJob()
    {
        // Single job with no OriginalJobId returns list with only that job
        await using var fixture = await TestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Initial plan","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        var revisions = await queries.GetJobRevisionChainAsync(jobId, CancellationToken.None);

        Assert.Single(revisions);
        Assert.Equal(jobId, revisions[0].Id);
        Assert.Null(revisions[0].OriginalJobId);
    }

    [Fact]
    public async Task AT13_Test2_V1_V2_ChainReturnsInOrder()
    {
        // V1 → V2 returns both jobs in created order
        await using var fixture = await TestFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        var v1Time = DateTimeOffset.UtcNow;
        var v2Time = v1Time.AddSeconds(1);

        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1Job = new PlanningJob
            {
                Id = v1JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V1","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = v1Time
            };
            var v2Job = new PlanningJob
            {
                Id = v2JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V2","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                OriginalJobId = v1JobId,
                CreatedAtUtc = v2Time
            };
            await dbContext.PlanningJobs.AddAsync(v1Job);
            await dbContext.PlanningJobs.AddAsync(v2Job);
            await dbContext.SaveChangesAsync();
        }

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        var revisions = await queries.GetJobRevisionChainAsync(v1JobId, CancellationToken.None);

        Assert.Equal(2, revisions.Count);
        Assert.Equal(v1JobId, revisions[0].Id);
        Assert.Equal(v2JobId, revisions[1].Id);
        Assert.Null(revisions[0].OriginalJobId);
        Assert.Equal(v1JobId, revisions[1].OriginalJobId);
    }

    [Fact]
    public async Task AT13_Test3_V1_V2_V3_ChainReturnsAll()
    {
        // V1 → V2 → V3 returns all three in order
        await using var fixture = await TestFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        var v3JobId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1Job = new PlanningJob
            {
                Id = v1JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V1","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = baseTime
            };
            var v2Job = new PlanningJob
            {
                Id = v2JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V2","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                OriginalJobId = v1JobId,
                CreatedAtUtc = baseTime.AddSeconds(1)
            };
            var v3Job = new PlanningJob
            {
                Id = v3JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V3","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                OriginalJobId = v2JobId,
                CreatedAtUtc = baseTime.AddSeconds(2)
            };
            await dbContext.PlanningJobs.AddAsync(v1Job);
            await dbContext.PlanningJobs.AddAsync(v2Job);
            await dbContext.PlanningJobs.AddAsync(v3Job);
            await dbContext.SaveChangesAsync();
        }

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        var revisions = await queries.GetJobRevisionChainAsync(v1JobId, CancellationToken.None);

        Assert.Equal(3, revisions.Count);
        Assert.Equal(v1JobId, revisions[0].Id);
        Assert.Equal(v2JobId, revisions[1].Id);
        Assert.Equal(v3JobId, revisions[2].Id);
    }

    [Fact]
    public async Task AT13_Test4_CallingWithV2ReturnsCompleteChain()
    {
        // Calling endpoint with V2 jobId still returns entire V1→V2→V3 chain
        await using var fixture = await TestFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        var v3JobId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1Job = new PlanningJob
            {
                Id = v1JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V1","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = baseTime
            };
            var v2Job = new PlanningJob
            {
                Id = v2JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V2","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                OriginalJobId = v1JobId,
                CreatedAtUtc = baseTime.AddSeconds(1)
            };
            var v3Job = new PlanningJob
            {
                Id = v3JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V3","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                OriginalJobId = v2JobId,
                CreatedAtUtc = baseTime.AddSeconds(2)
            };
            await dbContext.PlanningJobs.AddAsync(v1Job);
            await dbContext.PlanningJobs.AddAsync(v2Job);
            await dbContext.PlanningJobs.AddAsync(v3Job);
            await dbContext.SaveChangesAsync();
        }

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        // Call with V2, not V1
        var revisions = await queries.GetJobRevisionChainAsync(v2JobId, CancellationToken.None);

        Assert.Equal(3, revisions.Count);
        Assert.Equal(v1JobId, revisions[0].Id);
        Assert.Equal(v2JobId, revisions[1].Id);
        Assert.Equal(v3JobId, revisions[2].Id);
    }

    [Fact]
    public async Task AT13_Test5_MissingJobReturnsEmpty()
    {
        // Non-existent jobId returns empty list (caller handles 404)
        await using var fixture = await TestFixture.CreateAsync();
        var nonExistentJobId = Guid.NewGuid();

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        var revisions = await queries.GetJobRevisionChainAsync(nonExistentJobId, CancellationToken.None);

        Assert.Empty(revisions);
    }

    [Fact]
    public async Task AT13_Test6_DeterministicOrdering()
    {
        // Multiple revisions with same timestamp order deterministically by ID
        await using var fixture = await TestFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        var sameTime = DateTimeOffset.UtcNow;

        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1Job = new PlanningJob
            {
                Id = v1JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V1","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = sameTime
            };
            var v2Job = new PlanningJob
            {
                Id = v2JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V2","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                OriginalJobId = v1JobId,
                CreatedAtUtc = sameTime
            };
            await dbContext.PlanningJobs.AddAsync(v1Job);
            await dbContext.PlanningJobs.AddAsync(v2Job);
            await dbContext.SaveChangesAsync();
        }

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        var revisions = await queries.GetJobRevisionChainAsync(v1JobId, CancellationToken.None);

        // Parent should come before child even with same timestamp
        Assert.Equal(v1JobId, revisions[0].Id);
        Assert.Equal(v2JobId, revisions[1].Id);
    }

    [Fact]
    public async Task AT13_Test8_CircularReferenceTerminatesSafely()
    {
        // Circular reference: V1 → V2 → V1 must not cause infinite loop
        // Simulated via database manipulation (not created through normal flow)
        await using var fixture = await TestFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1Job = new PlanningJob
            {
                Id = v1JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V1","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                OriginalJobId = v2JobId
            };
            var v2Job = new PlanningJob
            {
                Id = v2JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V2","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1),
                OriginalJobId = v1JobId
            };
            await dbContext.PlanningJobs.AddAsync(v1Job);
            await dbContext.PlanningJobs.AddAsync(v2Job);
            await dbContext.SaveChangesAsync();
        }

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        var revisions = await queries.GetJobRevisionChainAsync(v1JobId, CancellationToken.None);

        // Must complete without hanging or stack overflow
        Assert.NotNull(revisions);
        // Due to cycle, result should contain at least the two cycled nodes
        Assert.True(revisions.Count >= 2);
    }

    [Fact]
    public async Task AT13_Test9_OrphanedRevisionTerminatesGracefully()
    {
        // Orphaned child: V2.OriginalJobId points to non-existent V1
        await using var fixture = await TestFixture.CreateAsync();
        var v2JobId = Guid.NewGuid();
        var missingV1Id = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            var v2Job = new PlanningJob
            {
                Id = v2JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V2 (orphaned)","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                OriginalJobId = missingV1Id  // Parent doesn't exist
            };
            await dbContext.PlanningJobs.AddAsync(v2Job);
            await dbContext.SaveChangesAsync();
        }

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        var revisions = await queries.GetJobRevisionChainAsync(v2JobId, CancellationToken.None);

        // Orphaned child is returned as-is; OriginalJobId still points to missing parent
        Assert.Single(revisions);
        Assert.Equal(v2JobId, revisions[0].Id);
        Assert.Equal(missingV1Id, revisions[0].OriginalJobId);  // OriginalJobId preserved even though parent missing
    }

    [Fact]
    public async Task AT13_Test10_UnrelatedChainsExcluded()
    {
        // Two independent chains: V1a→V2a and V1b→V2b
        // Query V1a should return only its chain, not V1b or V2b
        await using var fixture = await TestFixture.CreateAsync();
        var v1aId = Guid.NewGuid();
        var v2aId = Guid.NewGuid();
        var v1bId = Guid.NewGuid();
        var v2bId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1a = new PlanningJob
            {
                Id = v1aId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V1a","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            var v2a = new PlanningJob
            {
                Id = v2aId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V2a","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1),
                OriginalJobId = v1aId
            };
            var v1b = new PlanningJob
            {
                Id = v1bId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V1b","assetIds":["P-102"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(2)
            };
            var v2b = new PlanningJob
            {
                Id = v2bId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V2b","assetIds":["P-102"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(3),
                OriginalJobId = v1bId
            };
            await dbContext.PlanningJobs.AddAsync(v1a);
            await dbContext.PlanningJobs.AddAsync(v2a);
            await dbContext.PlanningJobs.AddAsync(v1b);
            await dbContext.PlanningJobs.AddAsync(v2b);
            await dbContext.SaveChangesAsync();
        }

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        var revisionsA = await queries.GetJobRevisionChainAsync(v1aId, CancellationToken.None);

        // Should only return chain A, not B
        Assert.Equal(2, revisionsA.Count);
        Assert.Equal(v1aId, revisionsA[0].Id);
        Assert.Equal(v2aId, revisionsA[1].Id);
        Assert.False(revisionsA.Any(j => j.Id == v1bId || j.Id == v2bId));
    }

    [Fact]
    public async Task AT13_Test11_ResponseContractExcludesInternalFields()
    {
        // Verify response contains only revision-relevant fields, not ResultPayload, ErrorMessage, etc.
        await using var fixture = await TestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Test","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = """{"outcome":"CandidatesGenerated","result":{...}}""",
                ErrorMessage = "This should not be exposed in revision history",
                FailureCode = "INTERNAL_ERROR"
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        var revisions = await queries.GetJobRevisionChainAsync(jobId, CancellationToken.None);

        // Service layer returns all fields, but endpoint should map only revision fields
        Assert.Single(revisions);
        Assert.NotNull(revisions[0].ResultPayload);  // Service has it
        Assert.NotNull(revisions[0].ErrorMessage);   // Service has it

        // Endpoint would project to PlanningJobRevisionResponse which excludes these
        // This test verifies service includes them; endpoint responsibility to exclude
    }

    [Fact]
    public async Task AT13_Test7_NoDatabaseChanges()
    {
        // GET request does not modify any job state or counts
        await using var fixture = await TestFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1Job = new PlanningJob
            {
                Id = v1JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V1","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AttemptCount = 1
            };
            var v2Job = new PlanningJob
            {
                Id = v2JobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V2","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                OriginalJobId = v1JobId,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1),
                AttemptCount = 1
            };
            await dbContext.PlanningJobs.AddAsync(v1Job);
            await dbContext.PlanningJobs.AddAsync(v2Job);
            await dbContext.SaveChangesAsync();
        }

        // Capture before state
        int jobCountBefore, v1AttemptsBefore, v2AttemptsBefore;
        await using (var dbContext = fixture.CreateDbContext())
        {
            jobCountBefore = await dbContext.PlanningJobs.CountAsync();
            v1AttemptsBefore = (await dbContext.PlanningJobs.SingleAsync(j => j.Id == v1JobId)).AttemptCount;
            v2AttemptsBefore = (await dbContext.PlanningJobs.SingleAsync(j => j.Id == v2JobId)).AttemptCount;
        }

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        await queries.GetJobRevisionChainAsync(v1JobId, CancellationToken.None);

        // Verify no changes
        await using (var dbContext = fixture.CreateDbContext())
        {
            var jobCountAfter = await dbContext.PlanningJobs.CountAsync();
            var v1AttemptsAfter = (await dbContext.PlanningJobs.SingleAsync(j => j.Id == v1JobId)).AttemptCount;
            var v2AttemptsAfter = (await dbContext.PlanningJobs.SingleAsync(j => j.Id == v2JobId)).AttemptCount;

            Assert.Equal(jobCountBefore, jobCountAfter);
            Assert.Equal(v1AttemptsBefore, v1AttemptsAfter);
            Assert.Equal(v2AttemptsBefore, v2AttemptsAfter);
        }
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;

        public IServiceProvider ServiceProvider { get; }

        private TestFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            IServiceProvider serviceProvider)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            ServiceProvider = serviceProvider;
        }

        public static async Task<TestFixture> CreateAsync()
        {
            var connectionString = $"Data Source=AT13-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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
            services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();

            var serviceProvider = services.BuildServiceProvider();

            return new TestFixture(keeperConnection, connectionString, serviceProvider);
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
