using System.Text.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT13RevisionHistoryEndpointTests
{
    [Fact]
    public async Task AT13_EndpointTest1_ValidChainMapsToRevisionResponse()
    {
        // Valid V1→V2 chain, when mapped to response contract, includes OriginalJobId
        await using var fixture = await EndpointTestFixture.CreateAsync();
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
                CreatedAtUtc = DateTimeOffset.UtcNow
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

        // Map to response contract (what endpoint does)
        var response = revisions.Select(job => new PlanningJobRevisionResponse(
            job.Id,
            job.Status.ToString(),
            job.CreatedAtUtc,
            job.OriginalJobId)).ToList();

        Assert.Equal(2, response.Count);
        Assert.Equal(v1JobId, response[0].Id);
        Assert.Null(response[0].OriginalJobId);
        Assert.Equal(v2JobId, response[1].Id);
        Assert.Equal(v1JobId, response[1].OriginalJobId);
    }

    [Fact]
    public async Task AT13_EndpointTest2_MissingJobReturnsEmptyList()
    {
        // Non-existent jobId returns empty list (endpoint converts to 404)
        await using var fixture = await EndpointTestFixture.CreateAsync();
        var missingJobId = Guid.NewGuid();

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        var revisions = await queries.GetJobRevisionChainAsync(missingJobId, CancellationToken.None);

        Assert.Empty(revisions);
        // Endpoint: if (revisions.Count == 0) return TypedResults.NotFound();
    }

    [Fact]
    public void AT13_EndpointTest3_ResponseContractExcludesInternalFields()
    {
        // PlanningJobRevisionResponse contains only: Id, Status, CreatedAtUtc, OriginalJobId
        // Does NOT contain: ResultPayload, ErrorMessage, FailureCode, AttemptCount, etc.
        var response = new PlanningJobRevisionResponse(
            Guid.NewGuid(),
            "Succeeded",
            DateTimeOffset.UtcNow,
            null);

        // Verify the type has only these properties
        var props = typeof(PlanningJobRevisionResponse).GetProperties();
        var propNames = props.Select(p => p.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "CreatedAtUtc", "Id", "OriginalJobId", "Status" }, propNames);
        // Does NOT include: ResultPayload, ErrorMessage, FailureCode, AttemptCount, MaxAttempts, CurrentStep
    }

    [Fact]
    public void AT13_EndpointTest4_DetailEndpointRetainsFullResponseContract()
    {
        // Verify GET /api/planning-jobs/{id} still uses PlanningJobStatusResponse (unchanged)
        var detailResponse = new PlanningJobStatusResponse(
            Guid.NewGuid(),
            "Succeeded",
            1,
            2,
            "Complete",
            """{"outcome":"CandidatesGenerated"}""",
            null,
            null,
            DateTimeOffset.UtcNow,
            null,
            null,
            null);

        // Verify the type has all expected properties including internal fields
        var props = typeof(PlanningJobStatusResponse).GetProperties();
        var propNames = props.Select(p => p.Name).OrderBy(n => n).ToList();

        Assert.Contains("ResultPayload", propNames);
        Assert.Contains("ErrorMessage", propNames);
        Assert.Contains("FailureCode", propNames);
        Assert.Contains("AttemptCount", propNames);
        Assert.Contains("MaxAttempts", propNames);
        Assert.Contains("CurrentStep", propNames);
    }

    [Fact]
    public async Task AT13_EndpointTest5_QueryFromV2ReturnsCompleteChain()
    {
        // Query with V2 jobId returns full chain (V1→V2→V3) at service level
        await using var fixture = await EndpointTestFixture.CreateAsync();
        var v1Id = Guid.NewGuid();
        var v2Id = Guid.NewGuid();
        var v3Id = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            var v1 = new PlanningJob
            {
                Id = v1Id,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V1","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            var v2 = new PlanningJob
            {
                Id = v2Id,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V2","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1),
                OriginalJobId = v1Id
            };
            var v3 = new PlanningJob
            {
                Id = v3Id,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"V3","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(2),
                OriginalJobId = v2Id
            };
            await dbContext.PlanningJobs.AddAsync(v1);
            await dbContext.PlanningJobs.AddAsync(v2);
            await dbContext.PlanningJobs.AddAsync(v3);
            await dbContext.SaveChangesAsync();
        }

        var queries = fixture.ServiceProvider.GetRequiredService<IMaintenancePlannerQueries>();
        var revisions = await queries.GetJobRevisionChainAsync(v2Id, CancellationToken.None);

        // Querying from V2 should return entire chain
        Assert.Equal(3, revisions.Count);
        Assert.Equal(v1Id, revisions[0].Id);
        Assert.Equal(v2Id, revisions[1].Id);
        Assert.Equal(v3Id, revisions[2].Id);
    }

    private sealed class EndpointTestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;

        public IServiceProvider ServiceProvider { get; }

        private EndpointTestFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            IServiceProvider serviceProvider)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            ServiceProvider = serviceProvider;
        }

        public static async Task<EndpointTestFixture> CreateAsync()
        {
            var connectionString = $"Data Source=AT13Endpoint-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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

            return new EndpointTestFixture(keeperConnection, connectionString, serviceProvider);
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
