using System.Net;
using System.Net.Http.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// F06 Part A (FR08): individual task progress for an approved draft, through the real progress endpoints on a
/// temporary file-backed SQLite database. Tasks move forward only, NotStarted → InProgress → Completed. The
/// stable-ID plan places P101-PREP and P101-VERIFICATION (no spare part) and P101-BEARING-REPLACEMENT (one BR-101).
/// </summary>
public sealed class F06TaskProgressFileDatabaseTests
{
    private static readonly string[] PlanTaskIds = ["P101-PREP", "P101-BEARING-REPLACEMENT", "P101-VERIFICATION"];

    [Fact]
    public async Task ForwardTransitions_ArePersisted_AndSurviveAnApplicationRestart()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var draftId = await fixture.ApproveStableIdPlanAsync();

        var initial = await fixture.GetProgressAsync(draftId);
        Assert.Equal(PlanTaskIds, initial.TaskStatuses.Select(t => t.TaskId));
        Assert.All(initial.TaskStatuses, t => Assert.Equal(("NotStarted", (DateTimeOffset?)null), (t.Status, t.UpdatedAtUtc)));

        var started = await fixture.PostStatusAsync(draftId, "P101-PREP", "InProgress");
        var completed = await fixture.PostStatusAsync(draftId, "P101-PREP", "Completed");

        Assert.Equal((HttpStatusCode.OK, true, "InProgress"), (started.StatusCode, started.Body!.Succeeded, started.Body.Status));
        Assert.Equal((HttpStatusCode.OK, true, "Completed"), (completed.StatusCode, completed.Body!.Succeeded, completed.Body.Status));
        await using (var dbContext = fixture.CreateDbContext())
        {
            var row = Assert.Single(await dbContext.DraftTaskProgress.ToListAsync());
            Assert.Equal((draftId, "P101-PREP", TaskProgressStatus.Completed), (row.DraftId, row.TaskId, row.Status));
        }

        var expected = new[] { "P101-PREP:Completed", "P101-BEARING-REPLACEMENT:NotStarted", "P101-VERIFICATION:NotStarted" };
        Assert.Equal(expected, Describe(await fixture.GetProgressAsync(draftId)));

        // A new application host on the same database file reports the same task progress
        await fixture.RestartAsync();
        var reloaded = await fixture.GetProgressAsync(draftId);
        Assert.Equal(expected, Describe(reloaded));
        Assert.NotNull(reloaded.TaskStatuses.Single(t => t.TaskId == "P101-PREP").UpdatedAtUtc);
    }

    [Fact]
    public async Task SkippedBackwardAndRepeatedTransitions_AreRejected_AndChangeNothing()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var draftId = await fixture.ApproveStableIdPlanAsync();

        await fixture.AssertRejectedAsync(draftId, "P101-PREP", "Completed", HttpStatusCode.Conflict, "INVALID_TASK_TRANSITION");
        await fixture.AssertRejectedAsync(draftId, "P101-PREP", "NotStarted", HttpStatusCode.Conflict, "INVALID_TASK_TRANSITION");

        Assert.Equal(HttpStatusCode.OK, (await fixture.PostStatusAsync(draftId, "P101-PREP", "InProgress")).StatusCode);
        await fixture.AssertRejectedAsync(draftId, "P101-PREP", "InProgress", HttpStatusCode.Conflict, "INVALID_TASK_TRANSITION");
        await fixture.AssertRejectedAsync(draftId, "P101-PREP", "NotStarted", HttpStatusCode.Conflict, "INVALID_TASK_TRANSITION");

        Assert.Equal(HttpStatusCode.OK, (await fixture.PostStatusAsync(draftId, "P101-PREP", "Completed")).StatusCode);
        await fixture.AssertRejectedAsync(draftId, "P101-PREP", "Completed", HttpStatusCode.Conflict, "INVALID_TASK_TRANSITION");
        await fixture.AssertRejectedAsync(draftId, "P101-PREP", "InProgress", HttpStatusCode.Conflict, "INVALID_TASK_TRANSITION");
        await fixture.AssertRejectedAsync(draftId, "P101-PREP", "NotStarted", HttpStatusCode.Conflict, "INVALID_TASK_TRANSITION");

        Assert.Equal(
            ["P101-PREP:Completed", "P101-BEARING-REPLACEMENT:NotStarted", "P101-VERIFICATION:NotStarted"],
            Describe(await fixture.GetProgressAsync(draftId)));
    }

    [Fact]
    public async Task UnknownTaskUnknownDraftAndInvalidStatus_AreRejected_AndNothingIsPersisted()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var draftId = await fixture.ApproveStableIdPlanAsync();

        await fixture.AssertRejectedAsync(draftId, "P102-INSPECTION", "InProgress", HttpStatusCode.NotFound, "TASK_NOT_IN_PLAN");
        await fixture.AssertRejectedAsync(Guid.NewGuid(), "P101-PREP", "InProgress", HttpStatusCode.NotFound, "DRAFT_NOT_FOUND");
        foreach (var invalidStatus in new[] { "Done", "1", "", null })
            Assert.Equal(HttpStatusCode.BadRequest, (await fixture.PostStatusAsync(draftId, "P101-PREP", invalidStatus)).StatusCode);

        await using var dbContext = fixture.CreateDbContext();
        Assert.Empty(await dbContext.DraftTaskProgress.ToListAsync());
    }

    [Fact]
    public async Task CancelledAndSupersededDrafts_RejectProgressUpdates_AndKeepTheirRecordedProgress()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        await fixture.AddJobAsync(v1JobId, null, ApprovalTestSupport.StableIdCandidate());
        // V2 keeps V1's in-progress P101-PREP at its approved placement, as a replacement must (FR08)
        await fixture.AddJobAsync(v2JobId, v1JobId, ApprovalTestSupport.StableIdCandidate());
        var v1 = await fixture.ApproveAsync(v1JobId);
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostStatusAsync(v1, "P101-PREP", "InProgress")).StatusCode);

        // V2 supersedes V1; V1's progress can no longer change
        var v2 = await fixture.ApproveAsync(v2JobId);
        var rowsBefore = await fixture.ProgressRowsAsync();
        await fixture.AssertRejectedAsync(v1, "P101-PREP", "Completed", HttpStatusCode.Conflict, "DRAFT_NOT_APPROVED");
        await fixture.AssertRejectedAsync(v1, "P101-VERIFICATION", "InProgress", HttpStatusCode.Conflict, "DRAFT_NOT_APPROVED");

        // Cancelled V2 no longer accepts progress either
        Assert.True((await fixture.CancelAsync(v2)).Succeeded);
        await fixture.AssertRejectedAsync(v2, "P101-PREP", "InProgress", HttpStatusCode.Conflict, "DRAFT_NOT_APPROVED");

        Assert.Equal(rowsBefore, await fixture.ProgressRowsAsync());
        await using var dbContext = fixture.CreateDbContext();
        Assert.Equal(DraftStatus.Superseded, (await dbContext.Drafts.SingleAsync(d => d.Id == v1)).Status);
        Assert.Equal(DraftStatus.Cancelled, (await dbContext.Drafts.SingleAsync(d => d.Id == v2)).Status);
    }

    [Fact]
    public async Task CompletingTasksWithoutSpareParts_LeavesReservationsAndStockUnchanged()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var draftId = await fixture.ApproveStableIdPlanAsync();

        foreach (var taskId in new[] { "P101-PREP", "P101-VERIFICATION" })
        {
            Assert.Equal(HttpStatusCode.OK, (await fixture.PostStatusAsync(draftId, taskId, "InProgress")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await fixture.PostStatusAsync(draftId, taskId, "Completed")).StatusCode);
        }

        var progress = await fixture.GetProgressAsync(draftId);
        Assert.Equal(
            ["P101-PREP:Completed", "P101-BEARING-REPLACEMENT:NotStarted", "P101-VERIFICATION:Completed"],
            Describe(progress));
        Assert.Equal(0m, progress.CompletedQuantity);
        await using var dbContext = fixture.CreateDbContext();
        var reservation = await dbContext.Reservations.SingleAsync(r => r.DraftId == draftId);
        Assert.Equal(("BR-101", ReservationStatus.Confirmed, (DateTimeOffset?)null),
            (reservation.SparePartId, reservation.Status, reservation.CompletedAtUtc));
        var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
        Assert.Equal((1m, 1m), (br101.OnHandQuantity, br101.ReservedQuantity));
    }

    [Fact]
    public async Task CancellingDraftWithTaskProgress_StillReleasesItsUnusedReservation()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var draftId = await fixture.ApproveStableIdPlanAsync();
        Assert.Equal(HttpStatusCode.OK, (await fixture.PostStatusAsync(draftId, "P101-PREP", "InProgress")).StatusCode);
        var rowsBefore = await fixture.ProgressRowsAsync();

        var cancellation = await fixture.CancelAsync(draftId);

        Assert.True(cancellation.Succeeded, cancellation.FailureReason);
        Assert.Equal(rowsBefore, await fixture.ProgressRowsAsync());
        await using var dbContext = fixture.CreateDbContext();
        Assert.Equal(ReservationStatus.Cancelled, (await dbContext.Reservations.SingleAsync(r => r.DraftId == draftId)).Status);
        Assert.Equal(0m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
    }

    private static IEnumerable<string> Describe(PlanProgressResponse progress) =>
        progress.TaskStatuses.Select(t => $"{t.TaskId}:{t.Status}");

    private sealed class FileDatabaseFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly string _connectionString;
        private WebApplication _app;

        private FileDatabaseFixture(string databasePath, string connectionString, WebApplication app)
        {
            _databasePath = databasePath;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; private set; }

        public static async Task<FileDatabaseFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"maintenance-planner-f06-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
            await using (var dbContext = new MaintenancePlannerDbContext(
                             new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(connectionString).Options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            return new FileDatabaseFixture(databasePath, connectionString, await StartAppAsync(connectionString));
        }

        /// <summary>Disposes the running application host and starts a new one on the same database file.</summary>
        public async Task RestartAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            _app = await StartAppAsync(_connectionString);
            Client = _app.GetTestClient();
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        public async Task AddJobAsync(Guid jobId, Guid? originalJobId, object candidate)
        {
            await using var dbContext = CreateDbContext();
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId, originalJobId, candidate);
        }

        public async Task<Guid> ApproveStableIdPlanAsync()
        {
            var jobId = Guid.NewGuid();
            await AddJobAsync(jobId, null, ApprovalTestSupport.StableIdCandidate());
            return await ApproveAsync(jobId);
        }

        public async Task<Guid> ApproveAsync(Guid jobId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
                .ApproveDraftAsync(jobId, CancellationToken.None);
            Assert.True(result.Succeeded, result.FailureReason);
            return result.DraftId!.Value;
        }

        public async Task<CancellationResult> CancelAsync(Guid draftId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
                .CancelDraftAsync(draftId, CancellationToken.None);
        }

        public async Task<PlanProgressResponse> GetProgressAsync(Guid draftId)
        {
            using var response = await Client.GetAsync($"/api/drafts/{draftId}/progress");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<PlanProgressResponse>())!;
        }

        public async Task<(HttpStatusCode StatusCode, TaskStatusUpdateResponse? Body)> PostStatusAsync(
            Guid draftId,
            string taskId,
            string? status)
        {
            using var response = await Client.PostAsJsonAsync(
                $"/api/drafts/{draftId}/tasks/{taskId}/progress", new { status });
            var body = response.StatusCode == HttpStatusCode.BadRequest
                ? null
                : await response.Content.ReadFromJsonAsync<TaskStatusUpdateResponse>();
            return (response.StatusCode, body);
        }

        /// <summary>Posts a transition that must be rejected with the given status and code, changing no rows.</summary>
        public async Task AssertRejectedAsync(
            Guid draftId,
            string taskId,
            string status,
            HttpStatusCode expectedStatusCode,
            string expectedFailureCode)
        {
            var rowsBefore = await ProgressRowsAsync();
            var (statusCode, body) = await PostStatusAsync(draftId, taskId, status);
            Assert.Equal((expectedStatusCode, false, expectedFailureCode), (statusCode, body!.Succeeded, body.FailureCode));
            Assert.Equal(rowsBefore, await ProgressRowsAsync());
        }

        /// <summary>Every task progress row, for before/after comparison.</summary>
        public async Task<string> ProgressRowsAsync()
        {
            await using var dbContext = CreateDbContext();
            return string.Join("\n", (await dbContext.DraftTaskProgress.AsNoTracking().ToListAsync())
                .OrderBy(p => p.Id)
                .Select(p => $"{p.Id}:{p.DraftId}:{p.TaskId}:{p.Status}:{p.UpdatedAtUtc:O}"));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
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

        private static async Task<WebApplication> StartAppAsync(string connectionString)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(connectionString));
            builder.Services.AddApprovalServices();
            builder.Services.AddScoped<IPlanProgressService, PlanProgressService>();
            builder.Services.AddScoped<IPlanExportService, PlanExportService>();
            var app = builder.Build();
            app.MapApprovalEndpoints();
            app.MapPlanProgressEndpoints();
            await app.StartAsync();
            return app;
        }
    }
}
