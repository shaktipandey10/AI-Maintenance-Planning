using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Application.Edits;
using MaintenancePlanner.Application.History;
using MaintenancePlanner.Application.Locks;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Edits;
using MaintenancePlanner.Infrastructure.Locks;
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
/// FR05: users choose which task assignments stay locked; a lock that cannot be honoured is rejected with its specific
/// conflict, and replanning keeps locks rather than dropping them. Task edits are validated against current data, locks
/// and task progress and become a revision that is approved through the existing approval, superseding V1. Runs the
/// real endpoints on a temporary file-backed SQLite database seeded like the application.
/// </summary>
public sealed class FR05EditsAndLocksFileDatabaseTests
{
    private const string Prep = "P101-PREP";
    private const string Bearing = "P101-BEARING-REPLACEMENT";
    private const string Verification = "P101-VERIFICATION";

    [Fact]
    public async Task UserSelectedLocks_AreChangedAndRemoved_ImpossibleLocksAreRejected_AndReplanningKeepsThem()
    {
        await using var fixture = await EditFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        await fixture.AddJobAsync(v1JobId, null, ApprovalTestSupport.StableIdCandidate());
        var v1DraftId = await fixture.ApproveAsync(v1JobId);
        await fixture.AddTechnicianAsync("TECH-MORNING", ["MECHANICAL"], fromHour: 8, toHour: 12);

        // Approval locks each task to its lead technician; the user moves the bearing lock and removes the prep lock
        Assert.Equal(new[] { (Bearing, "TECH-001"), (Prep, "TECH-001"), (Verification, "TECH-003") }, await fixture.GetLocksAsync(v1DraftId));
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.LockAsync(v1DraftId, Bearing, "TECH-002")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.SendAsync(HttpMethod.Delete, $"/api/drafts/{v1DraftId}/locks/{Prep}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.SendAsync(HttpMethod.Delete, $"/api/drafts/{v1DraftId}/locks/{Prep}")).StatusCode);
        Assert.Equal(new[] { (Bearing, "TECH-002"), (Verification, "TECH-003") }, await fixture.GetLocksAsync(v1DraftId));

        // Locks that cannot be honoured with current data are rejected with the specific conflict and change nothing
        await fixture.AssertLockRejectedAsync(v1DraftId, Verification, "TECH-001", HttpStatusCode.Conflict, TaskLockChangeResult.TechnicianUnqualified);
        await fixture.AssertLockRejectedAsync(v1DraftId, Prep, "TECH-999", HttpStatusCode.Conflict, TaskLockChangeResult.TechnicianNotFound);
        await fixture.AssertLockRejectedAsync(v1DraftId, Prep, "TECH-MORNING", HttpStatusCode.Conflict, TaskLockChangeResult.TechnicianUnavailable);
        await fixture.AssertLockRejectedAsync(v1DraftId, "P101-UNKNOWN", "TECH-001", HttpStatusCode.NotFound, TaskLockChangeResult.TaskNotInPlan);
        Assert.Equal(new[] { (Bearing, "TECH-002"), (Verification, "TECH-003") }, await fixture.GetLocksAsync(v1DraftId));

        // Replanning keeps the user's lock: TECH-002 leads the bearing replacement in every alternative
        var replanned = await fixture.ReplanWithV1LocksAsync(v1JobId);
        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, replanned.Outcome);
        Assert.All(replanned.Candidates, candidate =>
            Assert.Equal("TECH-002", candidate.TaskPlacements.Single(p => p.TaskId == Bearing).TechnicianIds[0]));

        // When the locked technician becomes unavailable the lock is reported as the conflict, not silently dropped
        await fixture.RemoveAvailabilityAsync("TECH-002");
        var blocked = await fixture.ReplanWithV1LocksAsync(v1JobId);
        Assert.Equal(PlanningScheduleOutcome.Infeasible, blocked.Outcome);
        Assert.Contains(blocked.Violations, v =>
            v.Code == "LOCKED_TECHNICIAN_UNAVAILABLE" && v.TaskId == Bearing && v.ResourceId == "TECH-002");

        // Each applied lock change is recorded once; the rejected and repeated ones are not
        var lockHistory = (await fixture.GetHistoryAsync(v1JobId))
            .Where(entry => entry.Action is BusinessActions.TaskLocked or BusinessActions.TaskUnlocked)
            .Select(entry => (entry.Action, entry.TaskId, entry.PerformedBy, entry.Details))
            .ToArray();
        Assert.Equal(new[]
        {
            (BusinessActions.TaskLocked, (string?)Bearing, "planner", (string?)"Locked to TECH-002 (was TECH-001)."),
            (BusinessActions.TaskUnlocked, (string?)Prep, "planner", (string?)"Unlocked (was locked to TECH-001).")
        }, lockHistory);
    }

    [Fact]
    public async Task LocksOfStartedWork_CannotBeChangedOrRemoved_AndRejectionsChangeNoLockOrHistory()
    {
        await using var fixture = await EditFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        await fixture.AddJobAsync(v1JobId, null, ApprovalTestSupport.StableIdCandidate());
        var v1DraftId = await fixture.ApproveAsync(v1JobId);

        // Preparation completed, bearing replacement in progress, verification not started
        Assert.Equal(HttpStatusCode.OK, (await fixture.MoveTaskAsync(v1DraftId, Prep, "InProgress")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.MoveTaskAsync(v1DraftId, Prep, "Completed")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.MoveTaskAsync(v1DraftId, Bearing, "InProgress")).StatusCode);
        var locksBefore = await fixture.GetLockRowsAsync(v1DraftId);
        var historyBefore = (await fixture.GetHistoryAsync(v1JobId)).Select(entry => entry.Id).ToArray();

        // Locking to another qualified technician and unlocking are both rejected for started work
        await fixture.AssertLockRejectedAsync(v1DraftId, Prep, "TECH-002", HttpStatusCode.Conflict, TaskLockChangeResult.TaskCompleted);
        await fixture.AssertLockRejectedAsync(v1DraftId, Bearing, "TECH-002", HttpStatusCode.Conflict, TaskLockChangeResult.TaskInProgress);
        await fixture.AssertUnlockRejectedAsync(v1DraftId, Prep, TaskLockChangeResult.TaskCompleted);
        await fixture.AssertUnlockRejectedAsync(v1DraftId, Bearing, TaskLockChangeResult.TaskInProgress);

        Assert.Equal(locksBefore, await fixture.GetLockRowsAsync(v1DraftId));
        Assert.Equal(historyBefore, (await fixture.GetHistoryAsync(v1JobId)).Select(entry => entry.Id));

        // A task not started yet can still be unlocked, and an unknown task is still not found
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.SendAsync(HttpMethod.Delete, $"/api/drafts/{v1DraftId}/locks/{Verification}")).StatusCode);
        Assert.Equal(new[] { (Bearing, "TECH-001"), (Prep, "TECH-001") }, await fixture.GetLocksAsync(v1DraftId));
        await fixture.AssertLockRejectedAsync(v1DraftId, "P101-UNKNOWN", "TECH-001", HttpStatusCode.NotFound, TaskLockChangeResult.TaskNotInPlan);
        Assert.Equal(BusinessActions.TaskUnlocked, (await fixture.GetHistoryAsync(v1JobId)).Last().Action);
    }

    [Fact]
    public async Task ValidEdits_BecomeARevision_ThatIsReviewedAndApprovedThroughTheExistingWorkflow_SupersedingV1()
    {
        await using var fixture = await EditFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        await fixture.AddJobAsync(v1JobId, null, ApprovalTestSupport.StableIdCandidate());
        var v1DraftId = await fixture.ApproveAsync(v1JobId);

        // Verification moved one hour later, same technician
        await fixture.SubmitEditAsync(v1DraftId, Verification, 13, 15, "TECH-003");
        var key = Guid.NewGuid();
        var revisionJobId = await fixture.CreateRevisionAsync(v1DraftId, key);
        Assert.Equal(revisionJobId, await fixture.CreateRevisionAsync(v1DraftId, key));

        // Reviewed like any planning job: one candidate, the edited plan with recalculated totals
        var candidates = await fixture.GetCandidatesAsync(revisionJobId);
        var candidate = Assert.Single(candidates.Candidates);
        Assert.Equal(420, candidate.CompletionTimeMinutes);
        Assert.Contains($"Revision of approved draft {v1DraftId}", candidate.Rationale);

        // Approved through the existing approval: V1 is superseded and its bearing reservation moves to the revision
        var v2DraftId = await fixture.ApproveAsync(revisionJobId);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(DraftStatus.Superseded, (await db.Drafts.SingleAsync(d => d.Id == v1DraftId)).Status);
            var v2 = await db.Drafts.Include(d => d.Reservations).SingleAsync(d => d.Id == v2DraftId);
            Assert.Equal(DraftStatus.Approved, v2.Status);
            var verification = ApprovedCandidateSnapshot.ReadPlacements(v2.ApprovedCandidatePayload)!.Single(p => p.TaskId == Verification);
            Assert.Equal((At(13), At(15)), (verification.Start, verification.End));
            Assert.Equal(ReservationStatus.Confirmed, Assert.Single(v2.Reservations).Status);
            Assert.Equal(ReservationStatus.Cancelled, (await db.Reservations.SingleAsync(r => r.DraftId == v1DraftId)).Status);
            Assert.Equal(1m, (await db.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
            Assert.Equal(2, await db.PlanningJobs.CountAsync());
        }

        // V1's locks are now history and cannot be changed
        await fixture.AssertLockRejectedAsync(v1DraftId, Prep, "TECH-002", HttpStatusCode.Conflict, TaskLockChangeResult.DraftNotApproved);
        Assert.Equal(
            new[] { BusinessActions.Approved, BusinessActions.TaskEditSubmitted, BusinessActions.RevisionCreatedFromEdits, BusinessActions.Approved },
            (await fixture.GetHistoryAsync(v1JobId)).Select(entry => entry.Action));
    }

    [Fact]
    public async Task ValidateEdits_ChecksTheWholeEditedPlan_LikeRevisionCreation()
    {
        await using var fixture = await EditFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        await fixture.AddJobAsync(v1JobId, null, ApprovalTestSupport.StableIdCandidate());
        var v1DraftId = await fixture.ApproveAsync(v1JobId);

        // Preparation moved to 08:30-09:30 alone overlaps the bearing replacement still starting at 09:00
        await fixture.SubmitEditAsync(v1DraftId, Prep, At(8).AddMinutes(30), At(9).AddMinutes(30), "TECH-001");
        var prepOnly = await fixture.ValidateEditsAsync(v1DraftId);
        Assert.False(prepOnly.IsValid);
        Assert.Equal((0, 1), (prepOnly.ValidEditsCount, prepOnly.InvalidEditsCount));
        Assert.Contains(prepOnly.Violations, v => v.TaskId == Bearing && v.Code == "PREDECESSOR_ORDER_VIOLATION");
        Assert.Equal("invalid", Assert.Single(await fixture.GetEditsAsync(v1DraftId)).ValidationStatus);
        await fixture.AssertRevisionRejectedAsync(v1DraftId, Bearing, "PREDECESSOR_ORDER_VIOLATION");

        // Shifting the whole chain by 30 minutes keeps the order, the locked technicians and the window
        await fixture.SubmitEditAsync(v1DraftId, Bearing, At(9).AddMinutes(30), At(12).AddMinutes(30), "TECH-001", "TECH-002");
        await fixture.SubmitEditAsync(v1DraftId, Verification, At(12).AddMinutes(30), At(14).AddMinutes(30), "TECH-003");
        var coordinated = await fixture.ValidateEditsAsync(v1DraftId);
        Assert.True(coordinated.IsValid, string.Join("; ", coordinated.Violations.Select(v => $"{v.TaskId} {v.Code}")));
        Assert.Equal((3, 0), (coordinated.ValidEditsCount, coordinated.InvalidEditsCount));
        Assert.All(await fixture.GetEditsAsync(v1DraftId), edit => Assert.Equal("valid", edit.ValidationStatus));
        var revision = Assert.Single((await fixture.GetCandidatesAsync(await fixture.CreateRevisionAsync(v1DraftId, Guid.NewGuid()))).Candidates);
        Assert.Equal(360, revision.CompletionTimeMinutes);
        Assert.Equal(At(8).AddMinutes(30), revision.TaskPlacements.Single(p => p.GetProperty("taskId").GetString() == Prep).GetProperty("start").GetDateTimeOffset());

        // Validation reports lock and task-progress conflicts too, as revision creation does
        await fixture.ClearEditsAsync(v1DraftId);
        await fixture.AddTechnicianAsync("TECH-ROTATING", ["ROTATING-EQUIPMENT"], fromHour: 8, toHour: 16);
        Assert.Equal(HttpStatusCode.OK, (await fixture.MoveTaskAsync(v1DraftId, Verification, "InProgress")).StatusCode);
        await fixture.SubmitEditAsync(v1DraftId, Bearing, 9, 12, "TECH-002", "TECH-ROTATING");
        await fixture.SubmitEditAsync(v1DraftId, Verification, 12, 14, "TECH-003");
        var conflicts = await fixture.ValidateEditsAsync(v1DraftId);
        Assert.False(conflicts.IsValid);
        Assert.Contains(conflicts.Violations, v => v.TaskId == Bearing && v.Code == "LOCKED_ASSIGNMENT_CHANGED");
        Assert.Contains(conflicts.Violations, v => v.TaskId == Verification && v.Code == "TASK_IN_PROGRESS");
    }

    [Fact]
    public async Task EditsThatBreakTheSchedule_ChangeALockedAssignment_OrTouchStartedWork_AreRejectedWithTheirConflict()
    {
        await using var fixture = await EditFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        await fixture.AddJobAsync(v1JobId, null, ApprovalTestSupport.StableIdCandidate());
        var v1DraftId = await fixture.ApproveAsync(v1JobId);
        await fixture.AddTechnicianAsync("TECH-ROTATING", ["ROTATING-EQUIPMENT"], fromHour: 8, toHour: 16);

        // The bearing replacement moved past the start of verification breaks the dependency
        await fixture.SubmitEditAsync(v1DraftId, Bearing, 11, 14, "TECH-001", "TECH-002");
        await fixture.AssertRevisionRejectedAsync(v1DraftId, Verification, "PREDECESSOR_ORDER_VIOLATION");

        // Replacing the locked lead technician (TECH-001) of the bearing replacement conflicts with its lock
        await fixture.ClearEditsAsync(v1DraftId);
        await fixture.SubmitEditAsync(v1DraftId, Bearing, 9, 12, "TECH-002", "TECH-ROTATING");
        await fixture.AssertRevisionRejectedAsync(v1DraftId, Bearing, "LOCKED_ASSIGNMENT_CHANGED");

        // After the user unlocks the task, the same edit is accepted
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.SendAsync(HttpMethod.Delete, $"/api/drafts/{v1DraftId}/locks/{Bearing}")).StatusCode);
        var accepted = await fixture.CreateRevisionAsync(v1DraftId, Guid.NewGuid());

        // Work in progress keeps its approved placement and completed work is not scheduled again
        var v1DraftAgain = v1DraftId;
        await fixture.ClearEditsAsync(v1DraftAgain);
        Assert.Equal(HttpStatusCode.OK, (await fixture.MoveTaskAsync(v1DraftAgain, Prep, "InProgress")).StatusCode);
        await fixture.SubmitEditAsync(v1DraftAgain, Prep, 8, 9, "TECH-002");
        await fixture.AssertRevisionRejectedAsync(v1DraftAgain, Prep, "TASK_IN_PROGRESS");
        Assert.Equal(HttpStatusCode.OK, (await fixture.MoveTaskAsync(v1DraftAgain, Prep, "Completed")).StatusCode);
        await fixture.AssertRevisionRejectedAsync(v1DraftAgain, Prep, "TASK_ALREADY_COMPLETED");

        // A valid edit after completed work leaves the completed task out of the revision
        await fixture.ClearEditsAsync(v1DraftAgain);
        await fixture.SubmitEditAsync(v1DraftAgain, Verification, 13, 15, "TECH-003");
        var afterProgress = await fixture.CreateRevisionAsync(v1DraftAgain, Guid.NewGuid());
        var revised = Assert.Single((await fixture.GetCandidatesAsync(afterProgress)).Candidates);
        Assert.DoesNotContain(revised.TaskPlacements, p => p.GetProperty("taskId").GetString() == Prep);

        // Only the accepted revisions were created
        await using var db = fixture.CreateDbContext();
        Assert.Equal(new[] { accepted, afterProgress }.Order(), (await db.PlanningJobs.Where(j => j.OriginalJobId == v1JobId).Select(j => j.Id).ToListAsync()).Order());
    }

    private static DateTimeOffset At(int hour) => new(2026, 10, 24, hour, 0, 0, TimeSpan.FromHours(5.5));

    private sealed class EditFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private EditFixture(string databasePath, string connectionString, WebApplication app)
        {
            _databasePath = databasePath;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        private HttpClient Client { get; }

        public static async Task<EditFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"maintenance-planner-fr05-{Guid.NewGuid():N}.db");
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
            builder.Services.AddScoped<CandidateCostCalculator>();
            builder.Services.AddScoped<DeterministicPlanningScheduler>();
            builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
            builder.Services.AddScoped<IPlanProgressService, PlanProgressService>();
            builder.Services.AddScoped<IPlanExportService, PlanExportService>();
            builder.Services.AddScoped<ITaskEditorService, TaskEditorService>();
            builder.Services.AddScoped<EditedPlanValidator>();
            builder.Services.AddScoped<IEditValidationService, EditValidationService>();
            builder.Services.AddScoped<IEditRevisionService, EditRevisionService>();
            builder.Services.AddScoped<ITaskLockService, TaskLockService>();
            var app = builder.Build();
            app.UseBusinessActorFromHeader();
            app.MapPlanningJobEndpoints();
            app.MapApprovalEndpoints();
            app.MapPlanProgressEndpoints();
            app.MapEditEndpoints();
            app.MapHistoryEndpoints();
            await app.StartAsync();
            return new EditFixture(databasePath, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        public async Task AddJobAsync(Guid jobId, Guid? originalJobId, object candidate)
        {
            await using var dbContext = CreateDbContext();
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId, originalJobId, candidate);
        }

        public async Task AddTechnicianAsync(string id, string[] skills, int fromHour, int toHour)
        {
            await using var dbContext = CreateDbContext();
            await dbContext.Technicians.AddAsync(new Technician
            {
                Id = id,
                Name = $"Test technician {id}",
                HourlyRate = 40m,
                TechnicalSkills = skills.Select(skill => new TechnicalSkill { TechnicianId = id, SkillCode = skill }).ToList(),
                TechnicianAvailabilities =
                    [new TechnicianAvailability { TechnicianId = id, AvailableFrom = At(fromHour), AvailableTo = At(toHour) }]
            });
            await dbContext.SaveChangesAsync();
        }

        public async Task RemoveAvailabilityAsync(string technicianId)
        {
            await using var dbContext = CreateDbContext();
            dbContext.TechnicianAvailabilities.RemoveRange(
                await dbContext.TechnicianAvailabilities.Where(a => a.TechnicianId == technicianId).ToListAsync());
            await dbContext.SaveChangesAsync();
        }

        // The replanning step that uses locks: the scheduler with the approved V1 draft's locks, as PlanningJobProcessor
        // passes them for a replacement job
        public async Task<PlanningScheduleResult> ReplanWithV1LocksAsync(Guid v1JobId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var locks = await scope.ServiceProvider.GetRequiredService<ITaskLockService>().GetLocksForJobAsync(v1JobId, CancellationToken.None);
            return await scope.ServiceProvider.GetRequiredService<DeterministicPlanningScheduler>().ScheduleAsync(
                new PlanningScheduleRequest(["P-101"], ApprovalTestSupport.BaselineWindowId, [Bearing])
                {
                    ReleasableReservedQuantities = new Dictionary<string, decimal> { ["BR-101"] = 1m }
                },
                CancellationToken.None,
                locks);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, Guid? idempotencyKey = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add(BusinessActor.HeaderName, "planner");
            if (idempotencyKey is { } key)
                request.Headers.Add("Idempotency-Key", key.ToString());
            if (body is not null)
                request.Content = JsonContent.Create(body);
            return await Client.SendAsync(request);
        }

        public async Task<Guid> ApproveAsync(Guid jobId)
        {
            var response = await SendAsync(HttpMethod.Post, $"/api/drafts/{jobId}/approve", new { candidateIndex = 0 });
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
            return Guid.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
        }

        public Task<HttpResponseMessage> LockAsync(Guid draftId, string taskId, string technicianId) =>
            SendAsync(HttpMethod.Put, $"/api/drafts/{draftId}/locks/{taskId}", new { technicianId });

        public async Task AssertLockRejectedAsync(Guid draftId, string taskId, string technicianId, HttpStatusCode status, string code)
        {
            var response = await LockAsync(draftId, taskId, technicianId);
            Assert.Equal(status, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<TaskLockChangeResult>())!;
            Assert.Equal(code, result.FailureCode);
            Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        }

        public async Task AssertUnlockRejectedAsync(Guid draftId, string taskId, string code)
        {
            var response = await SendAsync(HttpMethod.Delete, $"/api/drafts/{draftId}/locks/{taskId}");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<TaskLockChangeResult>())!;
            Assert.Equal(code, result.FailureCode);
            Assert.Contains(taskId, result.FailureReason);
        }

        // The draft's lock rows exactly as stored, including when each was set
        public async Task<string[]> GetLockRowsAsync(Guid draftId)
        {
            await using var db = CreateDbContext();
            return (await db.LockedTaskAssignments.AsNoTracking().Where(l => l.DraftId == draftId).ToListAsync())
                .Select(l => $"{l.Id}|{l.TaskId}|{l.LockedTechnicianId}|{l.CreatedAtUtc:O}")
                .Order()
                .ToArray();
        }

        public async Task<(string TaskId, string TechnicianId)[]> GetLocksAsync(Guid draftId) =>
            (await Client.GetFromJsonAsync<List<TaskLockResponse>>($"/api/drafts/{draftId}/locks"))!
            .Select(l => (l.TaskId, l.TechnicianId))
            .ToArray();

        public Task SubmitEditAsync(Guid draftId, string taskId, int startHour, int endHour, params string[] technicianIds) =>
            SubmitEditAsync(draftId, taskId, At(startHour), At(endHour), technicianIds);

        public async Task SubmitEditAsync(Guid draftId, string taskId, DateTimeOffset start, DateTimeOffset end, params string[] technicianIds)
        {
            var response = await SendAsync(HttpMethod.Post, $"/api/drafts/{draftId}/task-edits", new
            {
                edits = new[] { new { taskId, newStart = start, newEnd = end, newTechnicianIds = technicianIds } }
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task<EditValidationResult> ValidateEditsAsync(Guid draftId)
        {
            var response = await SendAsync(HttpMethod.Post, $"/api/drafts/{draftId}/task-edits/validate");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<EditValidationResult>())!;
        }

        public async Task<IReadOnlyList<Application.Edits.EditedTaskPlacementResponse>> GetEditsAsync(Guid draftId) =>
            (await Client.GetFromJsonAsync<List<Application.Edits.EditedTaskPlacementResponse>>($"/api/drafts/{draftId}/task-edits"))!;

        public async Task ClearEditsAsync(Guid draftId) =>
            Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/drafts/{draftId}/task-edits")).StatusCode);

        public Task<HttpResponseMessage> MoveTaskAsync(Guid draftId, string taskId, string status) =>
            SendAsync(HttpMethod.Post, $"/api/drafts/{draftId}/tasks/{taskId}/progress", new { status });

        public async Task<Guid> CreateRevisionAsync(Guid draftId, Guid key)
        {
            var response = await SendAsync(HttpMethod.Post, $"/api/drafts/{draftId}/task-edits/revision", idempotencyKey: key);
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ReplanAcceptedResponse>())!.NewJobId;
        }

        public async Task AssertRevisionRejectedAsync(Guid draftId, string taskId, string code)
        {
            await using (var db = CreateDbContext())
            {
                var jobsBefore = await db.PlanningJobs.CountAsync();
                var response = await SendAsync(HttpMethod.Post, $"/api/drafts/{draftId}/task-edits/revision", idempotencyKey: Guid.NewGuid());
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                var result = (await response.Content.ReadFromJsonAsync<EditRevisionResult>())!;
                Assert.Equal(EditRevisionResult.EditsInvalid, result.FailureCode);
                Assert.Contains(result.Violations!, v => v.TaskId == taskId && v.Code == code);
                Assert.Equal(jobsBefore, await db.PlanningJobs.CountAsync());
            }
        }

        public async Task<CandidatesView> GetCandidatesAsync(Guid jobId) =>
            (await Client.GetFromJsonAsync<CandidatesView>($"/api/planning-jobs/{jobId}/candidates"))!;

        public async Task<IReadOnlyList<BusinessActionResponse>> GetHistoryAsync(Guid jobId) =>
            (await Client.GetFromJsonAsync<List<BusinessActionResponse>>($"/api/planning-jobs/{jobId}/history"))!;

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
    }

    private sealed record CandidatesView(IReadOnlyList<CandidateView> Candidates);

    private sealed record CandidateView(string Rationale, long CompletionTimeMinutes, IReadOnlyList<JsonElement> TaskPlacements);
}
