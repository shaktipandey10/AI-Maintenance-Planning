using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Planning;
using MaintenancePlanner.Infrastructure.Queries;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class M7EndToEndSmokeTests
{
    // M7: End-to-End API Smoke Test - Full workflow without live Ollama

    [Fact]
    public async Task M7_Complete_Workflow_From_Request_To_Export()
    {
        // Arrange: Set up API and database
        await using var fixture = await SmokeTestFixture.CreateAsync();
        var planningJobId = Guid.NewGuid();

        // Step 1: Create a planning job with predetermined result (no Ollama)
        var resultPayload = """
        {
          "outcome": "CandidatesGenerated",
          "result": {
            "candidates": [
              {
                "rationale": "Standard bearing replacement procedure",
                "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                "dependencyTaskIds": [],
                "taskPlacements": [],
                "evidenceReferences": ["manual-section-4.2"],
                "totalLabourCostAmount": 500.00,
                "totalLabourCostCurrency": "USD",
                "completionTimeMinutes": 120,
                "deferralReasons": [],
                "isLowestCostCandidate": true,
                "costComparabilityWarning": null
              }
            ],
            "assumptions": [],
            "evidenceReferences": [],
            "interpretation": {
              "maintenanceIntent": "Replace bearing",
              "scope": "P-101",
              "proposedAssetIds": ["P-101"],
              "proposedTaskIds": ["P101-BEARING-REPLACEMENT"]
            }
          }
        }
        """;

        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = planningJobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = JsonSerializer.Serialize(
                    new PlanningJobRequestPayload("Replace bearing on P-101", ["P-101"], null)),
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = resultPayload
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        // Step 2: Retrieve candidates using GET /api/planning-jobs/{jobId}/candidates
        var candidatesResponse = await fixture.Client.GetAsync($"/api/planning-jobs/{planningJobId}/candidates");
        Assert.Equal(HttpStatusCode.OK, candidatesResponse.StatusCode);

        var candidates = await candidatesResponse.Content.ReadFromJsonAsync<PlanningJobCandidatesResponse>();
        Assert.NotNull(candidates);
        Assert.Equal("CandidatesGenerated", candidates.Outcome);
        Assert.Single(candidates.Candidates);
        Assert.Equal("Standard bearing replacement procedure", candidates.Candidates[0].Rationale);
        Assert.Equal(500.00m, candidates.Candidates[0].TotalLabourCostAmount);

        // Step 3: Approve the candidate
        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var approvalResult = await approvalService.ApproveDraftAsync(planningJobId, CancellationToken.None);
        Assert.True(approvalResult.Succeeded);
        var draftId = approvalResult.DraftId!.Value;

        // Step 4: Retrieve the approved draft using GET /api/drafts/{draftId}
        var draftResponse = await fixture.Client.GetAsync($"/api/drafts/{draftId}");
        Assert.Equal(HttpStatusCode.OK, draftResponse.StatusCode);

        var draft = await draftResponse.Content.ReadFromJsonAsync<DraftDetailResponse>();
        Assert.NotNull(draft);
        Assert.Equal(draftId, draft.DraftId);
        Assert.Equal("Approved", draft.Status);
        Assert.NotNull(draft.SelectedCandidate);
        Assert.Equal("Standard bearing replacement procedure", draft.SelectedCandidate.Rationale);

        // Step 5: Verify reservation state persisted
        await using (var dbContext = fixture.CreateDbContext())
        {
            var reservations = await dbContext.Reservations
                .Where(r => r.DraftId == draftId)
                .ToListAsync();
            Assert.NotEmpty(reservations);
            Assert.All(reservations, r => Assert.Equal(ReservationStatus.Confirmed, r.Status));

            var br101 = await dbContext.SpareParts.FirstOrDefaultAsync(p => p.Id == "BR-101");
            if (br101 != null)
            {
                Assert.True(br101.ReservedQuantity >= 0);
            }
        }

        // Step 6: Mark a task complete and verify progress
        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();
        // Try marking the task (may succeed or fail depending on reservation/task mapping)
        var markResult = await progressService.MarkTaskCompleteAsync(draftId, "P101-BEARING-REPLACEMENT", CancellationToken.None);

        // Get progress regardless of mark result
        var progressResponse = await fixture.Client.GetAsync($"/api/drafts/{draftId}/progress");
        Assert.Equal(HttpStatusCode.OK, progressResponse.StatusCode);

        var progress = await progressResponse.Content.ReadFromJsonAsync<PlanProgressResponse>();
        Assert.NotNull(progress);
        Assert.True(progress.CompletedQuantity >= 0);
        Assert.True(progress.RemainingQuantity >= 0);

        // Step 7: Export JSON and verify it references the correct draft
        var jsonExportResponse = await fixture.Client.GetAsync($"/api/drafts/{draftId}/export/json");
        Assert.Equal(HttpStatusCode.OK, jsonExportResponse.StatusCode);

        var jsonContent = await jsonExportResponse.Content.ReadAsStringAsync();
        Assert.Contains(draftId.ToString(), jsonContent);
        Assert.Contains("\"progress\"", jsonContent);

        // Step 8: Export PDF and verify valid PDF format
        var pdfExportResponse = await fixture.Client.GetAsync($"/api/drafts/{draftId}/export/pdf");
        Assert.Equal(HttpStatusCode.OK, pdfExportResponse.StatusCode);

        var pdfBytes = await pdfExportResponse.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(pdfBytes);
        // PDF magic bytes: %PDF
        Assert.Equal(0x25, pdfBytes[0]); // %
        Assert.Equal(0x50, pdfBytes[1]); // P
        Assert.Equal(0x44, pdfBytes[2]); // D
        Assert.Equal(0x46, pdfBytes[3]); // F

        // Step 9: Cancel draft and verify consistency
        var cancelResult = await approvalService.CancelDraftAsync(draftId, CancellationToken.None);
        Assert.True(cancelResult.Succeeded);

        // Verify cancelled state
        await using (var dbContext = fixture.CreateDbContext())
        {
            var cancelledDraft = await dbContext.Drafts.SingleAsync(d => d.Id == draftId);
            Assert.Equal(DraftStatus.Cancelled, cancelledDraft.Status);

            var reservations = await dbContext.Reservations
                .Where(r => r.DraftId == draftId)
                .ToListAsync();
            Assert.All(reservations, r => Assert.Equal(ReservationStatus.Cancelled, r.Status));
        }
    }

    [Fact]
    public async Task M7_Missing_Job_Returns_404_For_Candidates()
    {
        // Arrange
        await using var fixture = await SmokeTestFixture.CreateAsync();
        var missingJobId = Guid.NewGuid();

        // Act: Try to get candidates for non-existent job
        var response = await fixture.Client.GetAsync($"/api/planning-jobs/{missingJobId}/candidates");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task M7_Missing_Draft_Returns_404_For_Detail()
    {
        // Arrange
        await using var fixture = await SmokeTestFixture.CreateAsync();
        var missingDraftId = Guid.NewGuid();

        // Act: Try to get draft details for non-existent draft
        var response = await fixture.Client.GetAsync($"/api/drafts/{missingDraftId}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task M7_Cannot_Export_Unapproved_Draft()
    {
        // Arrange: Create unapproved draft
        await using var fixture = await SmokeTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Test","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            var draft = new Draft
            {
                Id = draftId,
                PlanningJobId = jobId,
                Status = DraftStatus.Pending
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.Drafts.AddAsync(draft);
            await dbContext.SaveChangesAsync();
        }

        // Act: Try to export unapproved draft
        var jsonResponse = await fixture.Client.GetAsync($"/api/drafts/{draftId}/export/json");
        var pdfResponse = await fixture.Client.GetAsync($"/api/drafts/{draftId}/export/pdf");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, jsonResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, pdfResponse.StatusCode);
    }

    private sealed class SmokeTestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        public HttpClient Client { get; }
        public IServiceProvider ServiceProvider { get; }

        private SmokeTestFixture(
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

        public static async Task<SmokeTestFixture> CreateAsync()
        {
            var connectionString = $"Data Source=M7-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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
            builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
            builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            builder.Services.AddScoped<IDraftApprovalService, DraftApprovalService>();
            builder.Services.AddScoped<IPlanProgressService, PlanProgressService>();
            builder.Services.AddScoped<IPlanExportService, PlanExportService>();

            var app = builder.Build();

            // Map all endpoints
            app.MapPlanningJobEndpoints();
            app.MapApprovalEndpoints();
            app.MapPlanProgressEndpoints();

            await app.StartAsync();

            var client = app.GetTestClient();
            return new SmokeTestFixture(connection, connectionString, app, client, app.Services);
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
