using System.Net;
using System.Net.Http.Json;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Application.Edits;
using MaintenancePlanner.Application.History;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Edits;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// FR09: approve, cancel, replan, task-progress and task-edit actions are persisted with who performed them (the
/// X-Actor-Role header) and when, in the same save as the action; repeated or rejected actions record nothing; the
/// history of every revision of a plan and the saved plans are retrievable. Runs the real endpoints on a temporary
/// file-backed SQLite database seeded like the application.
/// </summary>
public sealed class FR09ActionHistoryFileDatabaseTests
{
    [Fact]
    public async Task V1ToV2Lifecycle_RecordsEachAppliedActionOnceWithItsActorAndTime_AndHistoryCoversEveryRevision()
    {
        await using var fixture = await HistoryFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        await fixture.AddJobAsync(v1JobId, null, ApprovalTestSupport.StableIdCandidate());
        await fixture.AddJobAsync(v2JobId, v1JobId, ApprovalTestSupport.LowestCostCandidate());
        var startedAt = DateTimeOffset.UtcNow;

        // V1: approved by a reviewer; the repeated approval is idempotent and records nothing
        var v1DraftId = await fixture.ApproveAsync(v1JobId, 0, "reviewer");
        Assert.Equal(v1DraftId, await fixture.ApproveAsync(v1JobId, 0, "Reviewer"));

        // Task edits by a planner; deleting with no header and clearing nothing are recorded only when applied
        var edit = new TaskEditsSubmissionRequest(
        [
            new TaskEditRequest("P101-VERIFICATION", At(13), At(15), ["TECH-003"])
        ]);
        Assert.Equal(HttpStatusCode.OK,
            (await fixture.SendAsync(HttpMethod.Post, $"/api/drafts/{v1DraftId}/task-edits", "planner", edit)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await fixture.SendAsync(HttpMethod.Delete, $"/api/drafts/{v1DraftId}/task-edits/P101-VERIFICATION", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await fixture.SendAsync(HttpMethod.Delete, $"/api/drafts/{v1DraftId}/task-edits", "planner")).StatusCode);

        // Replan requested by a planner; a retry with the same idempotency key records nothing
        var replanKey = Guid.NewGuid();
        var replanned = await fixture.ReplanAsync(v1JobId, replanKey, "planner");
        Assert.Equal(replanned, await fixture.ReplanAsync(v1JobId, replanKey, "planner"));

        // V2 approved by a reviewer, superseding V1; approving V1 again is rejected and records nothing
        var v2DraftId = await fixture.ApproveAsync(v2JobId, 0, "reviewer");
        Assert.Equal(HttpStatusCode.Conflict,
            (await fixture.SendAsync(HttpMethod.Post, $"/api/drafts/{v1JobId}/approve", "reviewer", new { candidateIndex = 0 })).StatusCode);

        // Progress on V2 by a technician; a rejected transition records nothing; an unknown role is "unspecified"
        Assert.Equal(HttpStatusCode.OK, (await fixture.MoveTaskAsync(v2DraftId, "P101-PREP", "InProgress", "technician")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.MoveTaskAsync(v2DraftId, "P101-PREP", "Completed", "admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.MoveTaskAsync(v2DraftId, "P101-PREP", "Completed", "technician")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await fixture.SendAsync(HttpMethod.Post, $"/api/drafts/{v2DraftId}/mark-task-complete?sparePartId=BR-101", "technician")).StatusCode);

        // V2 cancelled by a reviewer; the repeated cancellation records nothing
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.SendAsync(HttpMethod.Post, $"/api/drafts/{v2DraftId}/cancel", "reviewer")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.SendAsync(HttpMethod.Post, $"/api/drafts/{v2DraftId}/cancel", "reviewer")).StatusCode);
        var finishedAt = DateTimeOffset.UtcNow;

        var history = await fixture.GetHistoryAsync(v1JobId);
        Assert.Equal(
            new (string, string, Guid?, Guid?, string?)[]
            {
                (BusinessActions.Approved, "reviewer", v1JobId, v1DraftId, null),
                (BusinessActions.TaskEditSubmitted, "planner", v1JobId, v1DraftId, "P101-VERIFICATION"),
                (BusinessActions.TaskEditDeleted, BusinessActor.Unspecified, null, v1DraftId, "P101-VERIFICATION"),
                (BusinessActions.ReplanRequested, "planner", replanned, null, null),
                (BusinessActions.Approved, "reviewer", v2JobId, v2DraftId, null),
                (BusinessActions.TaskProgressUpdated, "technician", v2JobId, v2DraftId, "P101-PREP"),
                (BusinessActions.TaskProgressUpdated, BusinessActor.Unspecified, v2JobId, v2DraftId, "P101-PREP"),
                (BusinessActions.PartConsumptionRecorded, "technician", v2JobId, v2DraftId, null),
                (BusinessActions.Cancelled, "reviewer", v2JobId, v2DraftId, null)
            },
            history.Select(entry => (entry.Action, entry.PerformedBy, entry.PlanningJobId, entry.DraftId, entry.TaskId)));
        Assert.All(history, entry => Assert.InRange(entry.PerformedAtUtc, startedAt, finishedAt));
        Assert.Equal(history.Select(entry => entry.PerformedAtUtc).Order(), history.Select(entry => entry.PerformedAtUtc));
        Assert.Contains($"superseded draft {v1DraftId}", history[4].Details);
        Assert.Equal("NotStarted -> InProgress", history[5].Details);
        Assert.Equal("Released 0 unused reservation(s).", history[8].Details);

        // The history of any revision of the plan is the whole chain's history
        Assert.Equal(history.Select(entry => entry.Id), (await fixture.GetHistoryAsync(v2JobId)).Select(entry => entry.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.SendAsync(HttpMethod.Get, $"/api/planning-jobs/{Guid.NewGuid()}/history", null)).StatusCode);

        // The recorded actions changed nothing else: V1 superseded with its reservation released, V2 cancelled
        await using var db = fixture.CreateDbContext();
        Assert.Equal(DraftStatus.Superseded, (await db.Drafts.SingleAsync(d => d.Id == v1DraftId)).Status);
        Assert.Equal(DraftStatus.Cancelled, (await db.Drafts.SingleAsync(d => d.Id == v2DraftId)).Status);
        Assert.Equal(9, await db.BusinessActionRecords.CountAsync());
    }

    [Fact]
    public async Task SavedPlans_ListEveryDraftNewestFirst_WithItsRequestAndRevisionLink()
    {
        await using var fixture = await HistoryFixture.CreateAsync();
        Assert.Empty(await fixture.GetSavedPlansAsync());

        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        await fixture.AddJobAsync(v1JobId, null, ApprovalTestSupport.StableIdCandidate());
        await fixture.AddJobAsync(v2JobId, v1JobId, ApprovalTestSupport.LowestCostCandidate());
        var v1DraftId = await fixture.ApproveAsync(v1JobId, 0, "reviewer");
        var v2DraftId = await fixture.ApproveAsync(v2JobId, 0, "reviewer");

        var plans = await fixture.GetSavedPlansAsync();

        Assert.Equal(new[] { v2DraftId, v1DraftId }, plans.Select(plan => plan.DraftId));
        Assert.Equal((v2JobId, (Guid?)v1JobId, "Approved"), (plans[0].PlanningJobId, plans[0].OriginalJobId, plans[0].Status));
        Assert.Equal((v1JobId, (Guid?)null, "Superseded"), (plans[1].PlanningJobId, plans[1].OriginalJobId, plans[1].Status));
        Assert.All(plans, plan =>
        {
            Assert.Equal(new[] { "P-101" }, plan.AssetIds);
            Assert.Equal(ApprovalTestSupport.BaselineWindowId, plan.WindowId);
            Assert.Equal("Plan bearing replacement for P-101.", plan.RequestText);
            Assert.NotNull(plan.ApprovedAtUtc);
        });
    }

    private static DateTimeOffset At(int hour) => new(2026, 10, 24, hour, 0, 0, TimeSpan.FromHours(5.5));

    private sealed class HistoryFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private HistoryFixture(string databasePath, string connectionString, WebApplication app)
        {
            _databasePath = databasePath;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        private HttpClient Client { get; }

        public static async Task<HistoryFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"maintenance-planner-fr09-{Guid.NewGuid():N}.db");
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
            builder.Services.AddScoped<BusinessActor>();
            builder.Services.AddApprovalServices();
            builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
            builder.Services.AddScoped<IPlanProgressService, PlanProgressService>();
            builder.Services.AddScoped<IPlanExportService, PlanExportService>();
            builder.Services.AddScoped<ITaskEditorService, TaskEditorService>();
            builder.Services.AddScoped<IEditValidationService, EditValidationService>();
            var app = builder.Build();
            app.UseBusinessActorFromHeader();
            app.MapPlanningJobEndpoints();
            app.MapApprovalEndpoints();
            app.MapPlanProgressEndpoints();
            app.MapEditEndpoints();
            app.MapHistoryEndpoints();
            await app.StartAsync();
            return new HistoryFixture(databasePath, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        public async Task AddJobAsync(Guid jobId, Guid? originalJobId, object candidate)
        {
            await using var dbContext = CreateDbContext();
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId, originalJobId, candidate);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? role, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (role is not null)
                request.Headers.Add(BusinessActor.HeaderName, role);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            return await Client.SendAsync(request);
        }

        public async Task<Guid> ApproveAsync(Guid jobId, int candidateIndex, string role)
        {
            var response = await SendAsync(HttpMethod.Post, $"/api/drafts/{jobId}/approve", role, new { candidateIndex });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return Guid.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
        }

        public async Task<Guid> ReplanAsync(Guid originalJobId, Guid idempotencyKey, string role)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/planning-jobs/{originalJobId}/replan")
            {
                Content = JsonContent.Create(new
                {
                    requestText = "Replan the P-101 bearing replacement.",
                    assetIds = new[] { "P-101" },
                    windowId = ApprovalTestSupport.BaselineWindowId
                })
            };
            request.Headers.Add("Idempotency-Key", idempotencyKey.ToString());
            request.Headers.Add(BusinessActor.HeaderName, role);
            var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ReplanAccepted>())!.NewJobId;
        }

        public Task<HttpResponseMessage> MoveTaskAsync(Guid draftId, string taskId, string status, string role) =>
            SendAsync(HttpMethod.Post, $"/api/drafts/{draftId}/tasks/{taskId}/progress", role, new { status });

        public async Task<IReadOnlyList<BusinessActionResponse>> GetHistoryAsync(Guid jobId) =>
            (await Client.GetFromJsonAsync<List<BusinessActionResponse>>($"/api/planning-jobs/{jobId}/history"))!;

        public async Task<IReadOnlyList<SavedPlanResponse>> GetSavedPlansAsync() =>
            (await Client.GetFromJsonAsync<List<SavedPlanResponse>>("/api/drafts"))!;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                if (File.Exists(_databasePath + suffix))
                    File.Delete(_databasePath + suffix);
            }
        }

        private sealed record ReplanAccepted(Guid NewJobId, string StatusUrl);
    }
}
