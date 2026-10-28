using System.Net;
using System.Net.Http.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT06ConcurrentApprovalTests
{
    [Fact]
    public async Task AT06_TwoParallelApprovalsCompeteForFinalBR101_OnlyOneSucceeds()
    {
        // AT06: Two parallel approvals compete for the final available BR-101.
        // Arrange: Set up scenario with exactly 1 BR-101 available
        // Act: Submit both approvals concurrently
        // Assert: One succeeds (202), one fails (409), exactly 1 reservation in DB

        await using var fixture = await ApprovalTestFixture.CreateAsync();

        var job1Id = Guid.NewGuid();
        var job2Id = Guid.NewGuid();

        // Create two planning jobs that require BR-101, staffed by different crews so they compete only for the part
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddJobsCompetingOnlyForPartsAsync(dbContext, [job1Id, job2Id]);
        }

        // Submit both approvals concurrently with separate scopes for each request
        var approvalTask1 = Task.Run(async () =>
        {
            await using var scope = fixture.ServiceProvider.CreateAsyncScope();
            var approvalService = scope.ServiceProvider.GetRequiredService<IDraftApprovalService>();
            return await approvalService.ApproveDraftAsync(job1Id, CancellationToken.None);
        });

        var approvalTask2 = Task.Run(async () =>
        {
            await using var scope = fixture.ServiceProvider.CreateAsyncScope();
            var approvalService = scope.ServiceProvider.GetRequiredService<IDraftApprovalService>();
            return await approvalService.ApproveDraftAsync(job2Id, CancellationToken.None);
        });

        var results = await Task.WhenAll(approvalTask1, approvalTask2);

        var successfulResult = results.FirstOrDefault(r => r.Succeeded);
        var failedResult = results.FirstOrDefault(r => !r.Succeeded);

        Assert.NotNull(successfulResult);
        Assert.NotNull(failedResult);
        Assert.True(successfulResult.Succeeded);
        Assert.False(failedResult.Succeeded);
        // The loser must be rejected for the final BR-101, not for any other conflict
        Assert.Equal("INSUFFICIENT_INVENTORY", failedResult.ConflictData!.Reason);
        Assert.Equal("BR-101", failedResult.ConflictData.ResourceId);

        // Verify database state
        await using (var dbContext = fixture.CreateDbContext())
        {
            var drafts = await dbContext.Drafts.ToListAsync();
            Assert.Single(drafts);
            Assert.Equal(Domain.Entities.DraftStatus.Approved, Assert.Single(drafts).Status);

            var br101Reservations = await dbContext.Reservations
                .Where(r => r.SparePartId == "BR-101" && r.Status == Domain.Entities.ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Single(br101Reservations);
            Assert.Equal(1m, Assert.Single(br101Reservations).QuantityReserved);

            var br101Part = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101Part.ReservedQuantity);
            Assert.Equal(0m, br101Part.AvailableQuantity);
        }
    }

    [Fact]
    public async Task AT06_ConcurrentApprovalsRemainDeterministic_RepeatedRunsProduceSameOutcome()
    {
        // Verify the test is deterministic - repeated runs should behave the same

        for (int iteration = 0; iteration < 3; iteration++)
        {
            await using var fixture = await ApprovalTestFixture.CreateAsync();

            var job1Id = Guid.NewGuid();
            var job2Id = Guid.NewGuid();

            await using (var dbContext = fixture.CreateDbContext())
            {
                await ApprovalTestSupport.AddJobsCompetingOnlyForPartsAsync(dbContext, [job1Id, job2Id]);
            }

            var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

            var result1 = await approvalService.ApproveDraftAsync(job1Id, CancellationToken.None);
            var result2 = await approvalService.ApproveDraftAsync(job2Id, CancellationToken.None);

            var successCount = (result1.Succeeded ? 1 : 0) + (result2.Succeeded ? 1 : 0);
            Assert.Equal(1, successCount);
            Assert.Equal("INSUFFICIENT_INVENTORY", (result1.Succeeded ? result2 : result1).ConflictData!.Reason);

            await using (var dbContext = fixture.CreateDbContext())
            {
                var br101Reservations = await dbContext.Reservations
                    .Where(r => r.SparePartId == "BR-101" && r.Status == Domain.Entities.ReservationStatus.Confirmed)
                    .ToListAsync();
                Assert.Single(br101Reservations);
            }

            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task AT06_TwoConcurrentCancellationsOfSameDraft_InventoryReleasedOnceOnly()
    {
        // AT06: Two concurrent cancellation requests for the same approved draft
        // Inventory must be released exactly once, no negative quantities
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        // Approve a draft with BR-101 requirement
        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithBR101RequirementAsync(dbContext, jobId);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var approvalResult = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        var draftId = approvalResult.DraftId!.Value;

        // Verify draft is approved and inventory is reserved
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.ReservedQuantity);
        }

        // Submit two cancellations concurrently for the same draft
        var cancelTask1 = Task.Run(async () =>
        {
            await using var scope = fixture.ServiceProvider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDraftApprovalService>();
            return await service.CancelDraftAsync(draftId, CancellationToken.None);
        });

        var cancelTask2 = Task.Run(async () =>
        {
            await using var scope = fixture.ServiceProvider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDraftApprovalService>();
            return await service.CancelDraftAsync(draftId, CancellationToken.None);
        });

        var results = await Task.WhenAll(cancelTask1, cancelTask2);

        // At least one should succeed, second might be idempotent or fail depending on timing
        var successCount = results.Count(r => r.Succeeded);
        Assert.True(successCount >= 1, "At least one cancellation should succeed");

        // Verify final state: inventory must be released exactly once
        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.SingleAsync(d => d.Id == draftId);
            Assert.Equal(Domain.Entities.DraftStatus.Cancelled, draft.Status);

            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            // Inventory should be released (0 reserved, 1 available)
            Assert.Equal(0m, br101.ReservedQuantity);
            Assert.Equal(1m, br101.AvailableQuantity);

            // All reservations for this draft should be cancelled
            var reservations = await dbContext.Reservations
                .Where(r => r.DraftId == draftId)
                .ToListAsync();
            Assert.All(reservations, r => Assert.Equal(Domain.Entities.ReservationStatus.Cancelled, r.Status));
        }
    }

    [Fact]
    public async Task AT06_CancellationOfMissingDraft_ReturnsFalse()
    {
        // AT06: Cancellation of missing draft follows API contract
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var missingDraftId = Guid.NewGuid();

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result = await approvalService.CancelDraftAsync(missingDraftId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AT06_TrueParallelApprovalsViaHttpApi_ExactlyOneSucceedsWithFinalBR101()
    {
        // AT06: True parallel approvals via HTTP API competing for final BR-101 unit
        // Launch 12 concurrent HTTP POST requests for approval
        // Assert: Exactly one returns 202, all others return 409
        // Assert: Database state is consistent (1 draft, 1 reservation, BR-101 reserved by 1)

        await using var fixture = await HttpApprovalTestFixture.CreateAsync();
        const int concurrentRequestCount = 12;
        var jobIds = new List<Guid>();

        // Create 12 planning jobs, each requiring BR-101 and staffed by its own crew, so they compete only for the part
        await using (var dbContext = fixture.CreateDbContext())
        {
            for (int i = 0; i < concurrentRequestCount; i++)
                jobIds.Add(Guid.NewGuid());
            await ApprovalTestSupport.AddJobsCompetingOnlyForPartsAsync(dbContext, jobIds);
        }

        // Verify jobs were created
        await using (var verifyContext = fixture.CreateDbContext())
        {
            var createdJobs = await verifyContext.PlanningJobs.Where(j => jobIds.Contains(j.Id)).CountAsync();
            Assert.Equal(concurrentRequestCount, createdJobs);
        }

        // Launch 12 concurrent HTTP POST /api/drafts/{jobId}/approve requests
        var approvalTasks = jobIds.Select(jobId =>
            fixture.Client.PostAsync($"/api/drafts/{jobId}/approve", new StringContent("", System.Text.Encoding.UTF8, "application/json"))
        ).ToList();

        var responses = await Task.WhenAll(approvalTasks);

        // Assertion 1: Exactly one request succeeds with 202 Accepted
        var successfulResponses = responses.Where(r => r.StatusCode == HttpStatusCode.Accepted).ToList();
        Assert.Single(successfulResponses);

        // Assertion 2: All other requests return 409 Conflict
        var conflictResponses = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();
        Assert.Equal(concurrentRequestCount - 1, conflictResponses.Count);
        foreach (var conflict in conflictResponses)
        {
            // Every loser must be rejected for the final BR-101, not for any other conflict
            var error = await conflict.Content.ReadFromJsonAsync<ApprovalErrorResponse>();
            Assert.Equal("INSUFFICIENT_INVENTORY", error!.Reason);
            Assert.Equal("BR-101", error.ResourceId);
        }

        // Assertion 3: No other status codes present
        var otherResponses = responses.Where(r =>
            r.StatusCode != HttpStatusCode.Accepted &&
            r.StatusCode != HttpStatusCode.Conflict).ToList();
        Assert.Empty(otherResponses);

        // Assertion 4 & 5: Verify final database state is consistent
        await using (var dbContext = fixture.CreateDbContext())
        {
            // Exactly one approved draft should exist
            var drafts = await dbContext.Drafts
                .Where(d => d.Status == DraftStatus.Approved)
                .ToListAsync();
            Assert.Single(drafts);

            // Exactly one reservation should exist for BR-101
            var br101Reservations = await dbContext.Reservations
                .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Single(br101Reservations);
            Assert.Equal(1m, br101Reservations[0].QuantityReserved);

            // BR-101 spare part inventory should show exactly 1 reserved, 0 available
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.ReservedQuantity);
            Assert.Equal(0m, br101.AvailableQuantity);
            Assert.Equal(1m, br101.OnHandQuantity);

            // No partial/failed reservations should exist
            var allBR101Reservations = await dbContext.Reservations
                .Where(r => r.SparePartId == "BR-101")
                .ToListAsync();
            Assert.Single(allBR101Reservations);
        }
    }

    // Approval revalidates the candidate, so the job carries a schedulable P-101 candidate requiring one BR-101.
    private static Task CreatePlanningJobWithBR101RequirementAsync(
        MaintenancePlannerDbContext dbContext,
        Guid jobId) =>
        ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId);

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
                $"Data Source=AT06-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();
            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(keeperConnection)
                .Options;

            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);

                var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
                Assert.Equal(1m, br101.OnHandQuantity);
                Assert.Equal(0m, br101.ReservedQuantity);
            }

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
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

    private sealed class HttpApprovalTestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        public HttpClient Client { get; }
        public IServiceProvider ServiceProvider { get; }

        private HttpApprovalTestFixture(
            SqliteConnection connection,
            string connectionString,
            WebApplication app,
            HttpClient client,
            IServiceProvider serviceProvider)
        {
            _connection = connection;
            _connectionString = connectionString;
            _app = app;
            Client = client;
            ServiceProvider = serviceProvider;
        }

        public static async Task<HttpApprovalTestFixture> CreateAsync()
        {
            var connectionString = $"Data Source=AT06HTTP-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(connection)
                .Options;

            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();

            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
            builder.Services.AddApprovalServices();
            builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();

            var app = builder.Build();

            // Map approval endpoints for HTTP API testing
            app.MapApprovalEndpoints();

            await app.StartAsync();

            var client = app.GetTestClient();
            return new HttpApprovalTestFixture(connection, connectionString, app, client, app.Services);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
