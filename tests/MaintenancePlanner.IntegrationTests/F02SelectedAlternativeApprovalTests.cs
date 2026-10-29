using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaintenancePlanner.Api;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// F02: the alternative selected by candidateIndex is the one approved, revalidated, snapshotted, locked, reserved for
/// and reported back by the draft-detail API. Runs the real approval endpoints on a temporary file-backed SQLite
/// database. Alternative 1 (stable crew) and Alternative 2 (lowest-cost crew) differ in who prepares and who leads
/// the bearing replacement.
/// </summary>
public sealed class F02SelectedAlternativeApprovalTests
{
    [Theory]
    [InlineData(0, "TECH-001", "TECH-001")]
    [InlineData(1, "TECH-003", "TECH-002")]
    public async Task SelectedAlternative_IsApprovedPersistedAndReturnedByDraftDetail(
        int candidateIndex,
        string expectedPrepTechnician,
        string expectedBearingLead)
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        var job = ApprovalTestSupport.CreateSucceededJob(
            jobId, null, ApprovalTestSupport.StableIdCandidate(), ApprovalTestSupport.LowestCostCandidate());
        await fixture.AddJobAsync(job);
        var selectedJson = ApprovalTestSupport.CandidateJson(job, candidateIndex);
        var otherJson = ApprovalTestSupport.CandidateJson(job, 1 - candidateIndex);

        var approval = await fixture.Client.PostAsJsonAsync($"/api/drafts/{jobId}/approve", new { candidateIndex });

        Assert.Equal(HttpStatusCode.Accepted, approval.StatusCode);
        var draftId = Guid.Parse(approval.Headers.Location!.OriginalString.Split('/').Last());

        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.Include(d => d.Reservations).SingleAsync();
            Assert.Equal((draftId, DraftStatus.Approved), (draft.Id, draft.Status));
            Assert.Equal(selectedJson, draft.ApprovedCandidatePayload);
            Assert.NotEqual(otherJson, draft.ApprovedCandidatePayload);

            var locks = await dbContext.LockedTaskAssignments.Where(l => l.DraftId == draftId).ToListAsync();
            Assert.Equal(3, locks.Count);
            Assert.Equal(expectedPrepTechnician, locks.Single(l => l.TaskId == "P101-PREP").LockedTechnicianId);
            Assert.Equal(expectedBearingLead, locks.Single(l => l.TaskId == "P101-BEARING-REPLACEMENT").LockedTechnicianId);

            var reservation = Assert.Single(draft.Reservations);
            Assert.Equal(("BR-101", 1m, ReservationStatus.Confirmed),
                (reservation.SparePartId, reservation.QuantityReserved, reservation.Status));
        }

        // The draft-detail API reports the approved alternative, not simply the first candidate
        using var detailResponse = await fixture.Client.GetAsync($"/api/drafts/{draftId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var selected = detail.RootElement.GetProperty("selectedCandidate");
        using var selectedDocument = JsonDocument.Parse(selectedJson);
        Assert.Equal(
            selectedDocument.RootElement.GetProperty("rationale").GetString(),
            selected.GetProperty("rationale").GetString());
        var placements = selected.GetProperty("taskPlacements").EnumerateArray().ToList();
        Assert.Equal(expectedPrepTechnician, TechniciansOf(placements, "P101-PREP")[0]);
        Assert.Equal(expectedBearingLead, TechniciansOf(placements, "P101-BEARING-REPLACEMENT")[0]);
    }

    [Fact]
    public async Task CrossOriginApproval_ExposesLocationHeaderToBrowserScript()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        await fixture.AddJobAsync(ApprovalTestSupport.CreateSucceededJob(
            jobId, null, ApprovalTestSupport.StableIdCandidate(), ApprovalTestSupport.LowestCostCandidate()));

        // The frontend dev server calls the API cross-origin; the browser hides Location from fetch() unless exposed
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/drafts/{jobId}/approve")
        {
            Content = JsonContent.Create(new { candidateIndex = 1 })
        };
        request.Headers.Add("Origin", "http://localhost:5173");
        using var approval = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, approval.StatusCode);
        Assert.True(approval.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowedOrigins));
        Assert.Equal("*", Assert.Single(allowedOrigins));
        Assert.True(approval.Headers.TryGetValues("Access-Control-Expose-Headers", out var exposedHeaders));
        Assert.Contains(
            "Location",
            exposedHeaders.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries)),
            StringComparer.OrdinalIgnoreCase);

        await using var dbContext = fixture.CreateDbContext();
        var draft = await dbContext.Drafts.SingleAsync();
        Assert.Equal($"/api/drafts/{draft.Id}", approval.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task InvalidOrMissingSelection_IsRejected_AndNothingIsPersisted()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        await fixture.AddJobAsync(ApprovalTestSupport.CreateSucceededJob(
            jobId, null, ApprovalTestSupport.StableIdCandidate(), ApprovalTestSupport.LowestCostCandidate()));

        var outOfRange = await fixture.Client.PostAsJsonAsync($"/api/drafts/{jobId}/approve", new { candidateIndex = 2 });
        var negative = await fixture.Client.PostAsJsonAsync($"/api/drafts/{jobId}/approve", new { candidateIndex = -1 });
        var missing = await fixture.Client.PostAsync(
            $"/api/drafts/{jobId}/approve", new StringContent("", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal("CANDIDATE_NOT_FOUND", (await ReadConflictAsync(outOfRange)).Reason);
        Assert.Equal("CANDIDATE_NOT_FOUND", (await ReadConflictAsync(negative)).Reason);
        Assert.Equal("CANDIDATE_SELECTION_REQUIRED", (await ReadConflictAsync(missing)).Reason);
        await fixture.AssertNothingApprovedAsync();
    }

    [Fact]
    public async Task SelectedAlternativeNoLongerValid_IsRejected_AndNothingIsPersisted()
    {
        await using var fixture = await FileDatabaseFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        await fixture.AddJobAsync(ApprovalTestSupport.CreateSucceededJob(
            jobId, null, ApprovalTestSupport.StableIdCandidate(), ApprovalTestSupport.LowestCostCandidate()));

        // After planning, TECH-003 (who prepares and verifies in Alternative 2) loses all availability
        await using (var dbContext = fixture.CreateDbContext())
        {
            dbContext.TechnicianAvailabilities.RemoveRange(
                dbContext.TechnicianAvailabilities.Where(a => a.TechnicianId == "TECH-003"));
            await dbContext.SaveChangesAsync();
        }

        var approval = await fixture.Client.PostAsJsonAsync($"/api/drafts/{jobId}/approve", new { candidateIndex = 1 });

        var conflict = await ReadConflictAsync(approval);
        Assert.Equal("CANDIDATE_NO_LONGER_VALID", conflict.Reason);
        Assert.Equal("TECHNICIAN_UNAVAILABLE", conflict.ResourceId);
        await fixture.AssertNothingApprovedAsync();
    }

    private static string[] TechniciansOf(IEnumerable<JsonElement> placements, string taskId) =>
        placements
            .Single(p => p.GetProperty("taskId").GetString() == taskId)
            .GetProperty("technicianIds").EnumerateArray().Select(t => t.GetString()!).ToArray();

    private static async Task<ApprovalErrorResponse> ReadConflictAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApprovalErrorResponse>())!;
    }

    private sealed class FileDatabaseFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private FileDatabaseFixture(string databasePath, string connectionString, WebApplication app)
        {
            _databasePath = databasePath;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<FileDatabaseFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"maintenance-planner-f02-{Guid.NewGuid():N}.db");
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
            builder.Services.AddApprovalServices();
            builder.Services.AddFrontendCors();
            var app = builder.Build();
            app.UseCors(FrontendCors.PolicyName);
            app.MapApprovalEndpoints();
            await app.StartAsync();
            return new FileDatabaseFixture(databasePath, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        public async Task AddJobAsync(PlanningJob job)
        {
            await using var dbContext = CreateDbContext();
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        public async Task AssertNothingApprovedAsync()
        {
            await using var dbContext = CreateDbContext();
            Assert.Empty(await dbContext.Drafts.ToListAsync());
            Assert.Empty(await dbContext.Reservations.ToListAsync());
            Assert.Empty(await dbContext.LockedTaskAssignments.ToListAsync());
            Assert.Equal(0m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
        }

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
}
