using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Api.Workers;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Queries;
using MaintenancePlanner.Infrastructure.Seeding;
using IPlanningJobProcessor = MaintenancePlanner.Application.PlanningJobs.IPlanningJobProcessor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT16ReplanRevisionHttpWorkflowTests
{
    [Fact]
    public async Task AT16_Test1_ReplanEndpointCreatesJobWithOriginalJobId()
    {
        // Test 1: Replan endpoint creates V2 with correct OriginalJobId reference
        await using var api = await ReplanTestApi.CreateAsync();

        // Create V1 with result payload and approve it
        var v1JobId = await CreateJobWithResultAsync(api);
        await ApproveJobAsync(api, v1JobId);

        // Create V2 (replan) via HTTP API
        var v2JobId = await ReplanJobViaHttpAsync(api, v1JobId, "Revised plan for P-101", ["P-101"]);
        Assert.NotEqual(v1JobId, v2JobId);

        // Verify V2 has OriginalJobId set to V1 in database
        await using var dbContext = api.CreateDbContext();
        var v2JobEntity = await dbContext.PlanningJobs.SingleAsync(j => j.Id == v2JobId);
        Assert.Equal(v1JobId, v2JobEntity.OriginalJobId);
        Assert.NotNull(v2JobEntity.RequestPayload);
    }

    [Fact]
    public async Task AT16_Test2_ReplanWithIdempotencyKey_DoesNotCreateDuplicate()
    {
        // Test 2: Repeated replan with same Idempotency-Key returns existing job
        await using var api = await ReplanTestApi.CreateAsync();

        // Create and approve V1
        var v1JobId = await CreateJobWithResultAsync(api);
        await ApproveJobAsync(api, v1JobId);

        // Create V2 with specific idempotency key
        var idempotencyKey = Guid.NewGuid();
        var v2JobId1 = await ReplanJobViaHttpAsync(api, v1JobId, "Revised plan", ["P-101"], idempotencyKey);

        // Attempt replan again with same idempotency key
        var v2JobId2 = await ReplanJobViaHttpAsync(api, v1JobId, "Revised plan", ["P-101"], idempotencyKey);

        // Should return same job ID
        Assert.Equal(v2JobId1, v2JobId2);

        // Verify only one V2 job exists in database
        await using var dbContext = api.CreateDbContext();
        var v2Jobs = await dbContext.PlanningJobs
            .Where(j => j.OriginalJobId == v1JobId)
            .ToListAsync();
        Assert.Single(v2Jobs);
    }

    [Fact]
    public async Task AT16_Test3_ReplanWithDifferentRequest_SameIdempotencyKey_ReturnsConflict()
    {
        // Test 3: Different request with same Idempotency-Key returns conflict
        await using var api = await ReplanTestApi.CreateAsync();

        // Create and approve V1
        var v1JobId = await CreateJobWithResultAsync(api);
        await ApproveJobAsync(api, v1JobId);

        // Create V2 with specific idempotency key and request
        var idempotencyKey = Guid.NewGuid();
        var v2JobId1 = await ReplanJobViaHttpAsync(api, v1JobId, "Revised for P-101", ["P-101"], idempotencyKey);

        // Attempt different replan with same idempotency key (different asset)
        using var conflictRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/planning-jobs/{v1JobId}/replan")
        {
            Content = JsonContent.Create(new CreatePlanningJobRequest
            {
                RequestText = "Different plan for P-102",
                AssetIds = ["P-102"],
                WindowId = "MW-P101"
            })
        };
        conflictRequest.Headers.Add("Idempotency-Key", idempotencyKey.ToString());
        using var conflictResponse = await api.Client.SendAsync(conflictRequest);

        // Should return conflict
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
    }

    [Fact]
    public async Task AT16_Test4_GetRevisionHistory_ReturnsCompleteChain()
    {
        // Test 4: Revision history endpoint returns V1 → V2 in order
        await using var api = await ReplanTestApi.CreateAsync();

        // Create V1 and approve it
        var v1JobId = await CreateJobWithResultAsync(api);
        await ApproveJobAsync(api, v1JobId);

        // Create V2 (replan). This harness registers no planning workflow, so the worker cannot produce a V2
        // result and V2 approval is rejected; it was always rejected here, previously without being checked.
        var v2JobId = await ReplanJobViaHttpAsync(api, v1JobId, "Revised plan", ["P-101"]);
        var v2Approval = await ApproveJobExpectingConflictAsync(api, v2JobId);
        Assert.Equal("PLANNING_JOB_NO_RESULT", v2Approval.Reason);

        // Get revision history
        using var revisionResponse = await api.Client.GetAsync($"/api/planning-jobs/{v1JobId}/revisions");
        Assert.Equal(HttpStatusCode.OK, revisionResponse.StatusCode);

        var revisions = await revisionResponse.Content
            .ReadFromJsonAsync<List<PlanningJobRevisionResponse>>();
        Assert.NotNull(revisions);
        Assert.Equal(2, revisions.Count);

        // Verify order: V1 first, V2 second
        Assert.Equal(v1JobId, revisions[0].Id);
        Assert.Null(revisions[0].OriginalJobId);
        Assert.Equal(v2JobId, revisions[1].Id);
        Assert.Equal(v1JobId, revisions[1].OriginalJobId);

        // Verify status (both should exist, exact status may vary based on processing)
        Assert.NotNull(revisions[0].Status);
        Assert.NotNull(revisions[1].Status);
    }

    [Fact]
    public async Task AT16_Test5_ReplanNonApprovedJob_Returns_Conflict()
    {
        // Test 5: Replan of unapproved job returns conflict
        await using var api = await ReplanTestApi.CreateAsync();

        // Create V1 but don't approve
        var v1JobId = await CreatePlanningJobAsync(api, "Plan P-101", ["P-101"]);
        await api.WaitForJobCompletionAsync(v1JobId);
        // Skip approval

        // Attempt to replan unapproved job
        using var replanRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/planning-jobs/{v1JobId}/replan")
        {
            Content = JsonContent.Create(new CreatePlanningJobRequest
            {
                RequestText = "Revised plan",
                AssetIds = ["P-101"]
            })
        };
        replanRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var replanResponse = await api.Client.SendAsync(replanRequest);

        // Should return conflict
        Assert.Equal(HttpStatusCode.Conflict, replanResponse.StatusCode);
        var error = await replanResponse.Content.ReadAsStringAsync();
        Assert.Contains("approved draft", error);
    }

    [Fact]
    public async Task AT16_Test6_ReplanNonexistentJob_Returns_NotFound()
    {
        // Test 6: Replan of non-existent job returns 404
        await using var api = await ReplanTestApi.CreateAsync();

        var nonexistentJobId = Guid.NewGuid();
        using var replanRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/planning-jobs/{nonexistentJobId}/replan")
        {
            Content = JsonContent.Create(new CreatePlanningJobRequest
            {
                RequestText = "Revised plan",
                AssetIds = ["P-101"]
            })
        };
        replanRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var replanResponse = await api.Client.SendAsync(replanRequest);

        Assert.Equal(HttpStatusCode.NotFound, replanResponse.StatusCode);
    }

    [Fact]
    public async Task AT16_Test7_V1DraftUnchangedAfterV2Creation()
    {
        // Test 7: V1's draft remains unchanged when V2 is created through replan
        await using var api = await ReplanTestApi.CreateAsync();

        // Create and approve V1
        var v1JobId = await CreateJobWithResultAsync(api);
        var v1ApprovalResponse = await ApproveJobAsync(api, v1JobId);

        // Get V1's draft state
        await using var dbContext = api.CreateDbContext();
        var v1Draft = await dbContext.Drafts.SingleAsync(d => d.PlanningJobId == v1JobId);
        var v1DraftId = v1Draft.Id;
        Assert.Equal(DraftStatus.Approved, v1Draft.Status);
        var v1ApprovedTime = v1Draft.ApprovedAtUtc;

        // Create V2 (replan)
        var v2JobId = await ReplanJobViaHttpAsync(api, v1JobId, "Revised plan", ["P-101"]);

        // Verify V2 was created with OriginalJobId
        var v2Job = await dbContext.PlanningJobs.SingleAsync(j => j.Id == v2JobId);
        Assert.Equal(v1JobId, v2Job.OriginalJobId);

        // Verify V1's draft is still approved and unchanged
        var v1DraftAfter = await dbContext.Drafts.SingleAsync(d => d.Id == v1DraftId);
        Assert.Equal(DraftStatus.Approved, v1DraftAfter.Status);
        Assert.Equal(v1ApprovedTime, v1DraftAfter.ApprovedAtUtc); // Time unchanged
    }

    [Fact]
    public async Task AT16_Test8_RevisionQueryFromV2_ReturnsCompleteChain()
    {
        // Test 8: Query revision history with V2 jobId returns chain up to V2
        await using var api = await ReplanTestApi.CreateAsync();

        // Create V1 → V2
        var v1JobId = await CreateJobWithResultAsync(api);
        await ApproveJobAsync(api, v1JobId);

        var v2JobId = await ReplanJobViaHttpAsync(api, v1JobId, "Revised V2", ["P-101"]);

        // Query with V2 jobId
        using var revisionResponse = await api.Client.GetAsync($"/api/planning-jobs/{v2JobId}/revisions");
        var revisions = await revisionResponse.Content
            .ReadFromJsonAsync<List<PlanningJobRevisionResponse>>();

        Assert.NotNull(revisions);
        // Should return chain: V1 → V2
        Assert.Equal(2, revisions.Count);
        Assert.Equal(v1JobId, revisions[0].Id);
        Assert.Equal(v2JobId, revisions[1].Id);

        // Verify parent relationships
        Assert.Null(revisions[0].OriginalJobId);
        Assert.Equal(v1JobId, revisions[1].OriginalJobId);
    }

    [Fact]
    public async Task AT16_Test9_ReloadPersistenceAndVerifyState()
    {
        // Test 9: Reload database connection and verify complete state persists
        await using var api = await ReplanTestApi.CreateAsync();

        // Create V1 → approve → V2
        var v1JobId = await CreateJobWithResultAsync(api);
        await ApproveJobAsync(api, v1JobId);

        var v2JobId = await ReplanJobViaHttpAsync(api, v1JobId, "Revised plan", ["P-101"]);

        // Create new DB context (simulates reload)
        await using var dbContext = api.CreateDbContext();

        // Verify V1 state
        var v1Job = await dbContext.PlanningJobs.SingleAsync(j => j.Id == v1JobId);
        Assert.Null(v1Job.OriginalJobId);
        Assert.Equal(PlanningJobStatus.Succeeded, v1Job.Status);

        var v1Draft = await dbContext.Drafts.SingleAsync(d => d.PlanningJobId == v1JobId);
        Assert.Equal(DraftStatus.Approved, v1Draft.Status);

        // Verify V2 state
        var v2Job = await dbContext.PlanningJobs.SingleAsync(j => j.Id == v2JobId);
        Assert.Equal(v1JobId, v2Job.OriginalJobId);

        // Verify v2 exists with correct reference
        Assert.NotNull(v2Job);
        var v2JobReloaded = await dbContext.PlanningJobs.SingleAsync(j => j.Id == v2JobId);
        Assert.Equal(v1JobId, v2JobReloaded.OriginalJobId);
    }

    [Fact]
    public async Task AT16_Test10_RevisionEndpointValidatesResponse()
    {
        // Test 10: Revision response contains only expected fields
        await using var api = await ReplanTestApi.CreateAsync();

        var v1JobId = await CreateJobWithResultAsync(api);

        using var revisionResponse = await api.Client.GetAsync($"/api/planning-jobs/{v1JobId}/revisions");
        var revisions = await revisionResponse.Content
            .ReadFromJsonAsync<List<PlanningJobRevisionResponse>>();

        Assert.NotNull(revisions);
        Assert.NotEmpty(revisions);

        var revision = revisions[0];
        // Verify response has expected properties
        Assert.NotEqual(Guid.Empty, revision.Id);
        Assert.NotNull(revision.Status);
        Assert.NotEqual(default, revision.CreatedAtUtc);
        // OriginalJobId is null for V1, which is correct
        Assert.Null(revision.OriginalJobId);
    }

    private static async Task<Guid> CreateJobWithResultAsync(ReplanTestApi api)
    {
        var jobId = Guid.NewGuid();
        await using var dbContext = api.CreateDbContext();
        var job = new PlanningJob
        {
            Id = jobId,
            OperationId = Guid.NewGuid(),
            RequestPayload = ApprovalTestSupport.RequestPayload("Plan P-101 bearing replacement"),
            Status = PlanningJobStatus.Succeeded,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            // Approval revalidates the candidate, so V1 carries a schedulable P-101 plan requiring one BR-101.
            ResultPayload = ApprovalTestSupport.ResultPayload()
        };
        await dbContext.PlanningJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();
        return jobId;
    }

    private static async Task<Guid> CreatePlanningJobAsync(
        ReplanTestApi api,
        string requestText,
        string[] assetIds,
        Guid? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
        {
            Content = JsonContent.Create(new CreatePlanningJobRequest
            {
                RequestText = requestText,
                AssetIds = assetIds,
                WindowId = "MW-P101"
            })
        };
        request.Headers.Add("Idempotency-Key", (idempotencyKey ?? Guid.NewGuid()).ToString());

        using var response = await api.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var accepted = await response.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>();
        return accepted!.JobId;
    }

    private static async Task<Guid> ReplanJobViaHttpAsync(
        ReplanTestApi api,
        Guid originalJobId,
        string requestText,
        string[] assetIds,
        Guid? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/planning-jobs/{originalJobId}/replan")
        {
            Content = JsonContent.Create(new CreatePlanningJobRequest
            {
                RequestText = requestText,
                AssetIds = assetIds,
                WindowId = "MW-P101"
            })
        };
        request.Headers.Add("Idempotency-Key", (idempotencyKey ?? Guid.NewGuid()).ToString());

        using var response = await api.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var accepted = await response.Content.ReadFromJsonAsync<ReplanAcceptedResponse>();
        return accepted!.NewJobId;
    }

    private static async Task<PlanningJobStatusResponse> GetJobStatusAsync(ReplanTestApi api, Guid jobId)
    {
        using var response = await api.Client.GetAsync($"/api/planning-jobs/{jobId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlanningJobStatusResponse>())!;
    }

    private static async Task<HttpResponseMessage> ApproveJobAsync(ReplanTestApi api, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/drafts/{jobId}/approve");
        var response = await api.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return response;
    }

    private static async Task<ApprovalErrorResponse> ApproveJobExpectingConflictAsync(ReplanTestApi api, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/drafts/{jobId}/approve");
        using var response = await api.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApprovalErrorResponse>())!;
    }

    private sealed class ReplanTestApi : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private ReplanTestApi(SqliteConnection connection, string connectionString, WebApplication app)
        {
            _connection = connection;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<ReplanTestApi> CreateAsync()
        {
            var connectionString = $"Data Source=AT16-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(connectionString));
            builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
            builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            builder.Services.AddApprovalServices();
            builder.Services.AddScoped<IPlanningJobProcessor, PlanningJobProcessor>();

            // Register job worker with fast polling for tests
            builder.Services.Configure<PlanningJobWorkerOptions>(options =>
            {
                options.PollInterval = TimeSpan.FromMilliseconds(10);
                options.LeaseDuration = TimeSpan.FromSeconds(3);
            });
            builder.Services.AddHostedService<PlanningJobWorker>();

            var app = builder.Build();
            app.MapPlanningJobEndpoints();
            app.MapApprovalEndpoints();

            await app.StartAsync();

            return new ReplanTestApi(connection, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        public async Task WaitForJobCompletionAsync(Guid jobId, TimeSpan? timeout = null)
        {
            timeout ??= TimeSpan.FromSeconds(10);
            var deadline = DateTime.UtcNow.Add(timeout.Value);

            while (DateTime.UtcNow < deadline)
            {
                using var response = await Client.GetAsync($"/api/planning-jobs/{jobId}");
                if (response.IsSuccessStatusCode)
                {
                    var status = await response.Content.ReadFromJsonAsync<PlanningJobStatusResponse>();
                    if (status?.Status is "Succeeded" or "Failed")
                        return;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException($"Job {jobId} did not complete within {timeout.Value.TotalSeconds}s");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
