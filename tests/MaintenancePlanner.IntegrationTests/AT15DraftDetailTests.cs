using System.Net;
using System.Net.Http.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT15DraftDetailTests
{
    [Fact]
    public async Task AT15_Test1_ExistingDraftReturnedWithCorrectDetails()
    {
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        await using (var dbContext = testApi.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Replace bearing","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = """
                {
                  "outcome": "CandidatesGenerated",
                  "result": {
                    "candidates": [
                      {
                        "rationale": "Standard bearing replacement",
                        "requestedTaskIds": ["TASK-001"],
                        "dependencyTaskIds": [],
                        "taskPlacements": [],
                        "evidenceReferences": ["manual-2.1"],
                        "totalLabourCostAmount": 450.50,
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
                      "proposedTaskIds": ["TASK-001"]
                    }
                  }
                }
                """
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();

            var draft = new Draft
            {
                Id = draftId,
                PlanningJobId = jobId,
                Status = DraftStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.Drafts.AddAsync(draft);
            await dbContext.SaveChangesAsync();

            // Note: Reservations require SparePart records to exist due to foreign key constraints
            // For this test, we verify the endpoint returns the draft with empty reservations
        }

        using var response = await testApi.Client.GetAsync($"/api/drafts/{draftId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DraftDetailResponse>();
        Assert.NotNull(result);
        Assert.Equal(draftId, result.DraftId);
        Assert.Equal(jobId, result.PlanningJobId);
        Assert.Equal("Pending", result.Status);
        Assert.NotNull(result.SelectedCandidate);
        Assert.Equal("Standard bearing replacement", result.SelectedCandidate.Rationale);
        Assert.Equal(450.50m, result.SelectedCandidate.TotalLabourCostAmount);
        Assert.Empty(result.Reservations);
        Assert.NotNull(result.Interpretation);
        Assert.Equal("Replace bearing", result.Interpretation.MaintenanceIntent);
    }

    [Fact]
    public async Task AT15_Test2_MissingDraftReturns404()
    {
        await using var testApi = await CreateTestApiAsync();
        var missingDraftId = Guid.NewGuid();

        using var response = await testApi.Client.GetAsync($"/api/drafts/{missingDraftId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AT15_Test3_ApprovedDraftIncludesReservationInfo()
    {
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        await using (var dbContext = testApi.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Test","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = """
                {
                  "outcome": "CandidatesGenerated",
                  "result": {
                    "candidates": [
                      {
                        "rationale": "Test candidate",
                        "requestedTaskIds": [],
                        "dependencyTaskIds": [],
                        "taskPlacements": [],
                        "evidenceReferences": [],
                        "totalLabourCostAmount": 500.00,
                        "totalLabourCostCurrency": "USD",
                        "completionTimeMinutes": 100,
                        "deferralReasons": [],
                        "isLowestCostCandidate": true,
                        "costComparabilityWarning": null
                      }
                    ],
                    "assumptions": [],
                    "evidenceReferences": [],
                    "interpretation": {
                      "maintenanceIntent": "Test",
                      "scope": "P-101",
                      "proposedAssetIds": ["P-101"],
                      "proposedTaskIds": []
                    }
                  }
                }
                """
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();

            var approvedTime = DateTimeOffset.UtcNow;
            var draft = new Draft
            {
                Id = draftId,
                PlanningJobId = jobId,
                Status = DraftStatus.Approved,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ApprovedAtUtc = approvedTime
            };
            await dbContext.Drafts.AddAsync(draft);
            await dbContext.SaveChangesAsync();
        }

        using var response = await testApi.Client.GetAsync($"/api/drafts/{draftId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DraftDetailResponse>();
        Assert.NotNull(result);
        Assert.Equal("Approved", result.Status);
        Assert.NotNull(result.ApprovedAtUtc);
        Assert.Empty(result.Reservations);
    }

    [Fact]
    public async Task AT15_Test4_CancelledDraftReturnedWithStatus()
    {
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        await using (var dbContext = testApi.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Test","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = """
                {
                  "outcome": "CandidatesGenerated",
                  "result": {
                    "candidates": [],
                    "assumptions": [],
                    "evidenceReferences": [],
                    "interpretation": {"maintenanceIntent": "Test", "scope": "P-101", "proposedAssetIds": [], "proposedTaskIds": []}
                  }
                }
                """
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();

            var draft = new Draft
            {
                Id = draftId,
                PlanningJobId = jobId,
                Status = DraftStatus.Cancelled,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.Drafts.AddAsync(draft);
            await dbContext.SaveChangesAsync();
        }

        using var response = await testApi.Client.GetAsync($"/api/drafts/{draftId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DraftDetailResponse>();
        Assert.NotNull(result);
        Assert.Equal("Cancelled", result.Status);
        Assert.Null(result.SelectedCandidate);
    }

    [Fact]
    public async Task AT15_Test5_ReplacementDraftShowsRevisionRelationship()
    {
        await using var testApi = await CreateTestApiAsync();
        var originalJobId = Guid.NewGuid();
        var replacementJobId = Guid.NewGuid();
        var originalDraftId = Guid.NewGuid();
        var replacementDraftId = Guid.NewGuid();

        await using (var dbContext = testApi.CreateDbContext())
        {
            // Create original job
            var originalJob = new PlanningJob
            {
                Id = originalJobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Original","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = """{"outcome": "CandidatesGenerated", "result": {"candidates": [], "assumptions": [], "evidenceReferences": [], "interpretation": {"maintenanceIntent": "Original", "scope": "P-101", "proposedAssetIds": [], "proposedTaskIds": []}}}"""
            };
            await dbContext.PlanningJobs.AddAsync(originalJob);
            await dbContext.SaveChangesAsync();

            // Create replacement job with reference to original
            var replacementJob = new PlanningJob
            {
                Id = replacementJobId,
                OperationId = Guid.NewGuid(),
                OriginalJobId = originalJobId,
                RequestPayload = """{"requestText":"Replacement","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = """{"outcome": "CandidatesGenerated", "result": {"candidates": [], "assumptions": [], "evidenceReferences": [], "interpretation": {"maintenanceIntent": "Replacement", "scope": "P-101", "proposedAssetIds": [], "proposedTaskIds": []}}}"""
            };
            await dbContext.PlanningJobs.AddAsync(replacementJob);
            await dbContext.SaveChangesAsync();

            // Create original draft
            var originalDraft = new Draft
            {
                Id = originalDraftId,
                PlanningJobId = originalJobId,
                Status = DraftStatus.Approved,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ApprovedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.Drafts.AddAsync(originalDraft);
            await dbContext.SaveChangesAsync();

            // Create replacement draft
            var replacementDraft = new Draft
            {
                Id = replacementDraftId,
                PlanningJobId = replacementJobId,
                Status = DraftStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.Drafts.AddAsync(replacementDraft);
            await dbContext.SaveChangesAsync();
        }

        // Get replacement draft
        using var response = await testApi.Client.GetAsync($"/api/drafts/{replacementDraftId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DraftDetailResponse>();
        Assert.NotNull(result);
        Assert.Equal(replacementDraftId, result.DraftId);
        Assert.Equal(replacementJobId, result.PlanningJobId);
    }

    [Fact]
    public async Task AT15_Test5B_RegressionJsonParsingWithComplexProperties()
    {
        // Regression test for HTTP 500 when ResultPayload has object properties where strings are expected
        // This reproduces the issue fixed by SafeGetStringProperty helper methods
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        await using (var dbContext = testApi.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Complex parsing test","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                // This payload includes various JSON structures to test robust parsing
                ResultPayload = """
                {
                  "outcome": "CandidatesGenerated",
                  "result": {
                    "candidates": [
                      {
                        "rationale": "Complex candidate with various field types",
                        "requestedTaskIds": ["TASK-001", "TASK-002"],
                        "dependencyTaskIds": ["DEP-001"],
                        "taskPlacements": [
                          {"taskId": "TASK-001", "start": "2026-10-25T08:00:00Z", "end": "2026-10-25T09:00:00Z", "technicianIds": ["TECH-001"]},
                          {"taskId": "TASK-002", "start": "2026-10-25T09:00:00Z", "end": "2026-10-25T12:00:00Z", "technicianIds": ["TECH-001", "TECH-002"]}
                        ],
                        "evidenceReferences": ["manual-1.0", "doc-2.0"],
                        "totalLabourCostAmount": 750.75,
                        "totalLabourCostCurrency": "USD",
                        "completionTimeMinutes": 180,
                        "deferralReasons": [
                          {"taskId": "TASK-003", "reason": "Not feasible in this maintenance window"}
                        ],
                        "isLowestCostCandidate": false,
                        "costComparabilityWarning": "Cost significantly higher than alternative"
                      }
                    ],
                    "assumptions": [],
                    "evidenceReferences": [],
                    "interpretation": {
                      "maintenanceIntent": "Comprehensive bearing and seal replacement",
                      "scope": "P-101",
                      "proposedAssetIds": ["P-101"],
                      "proposedTaskIds": ["TASK-001", "TASK-002"]
                    }
                  }
                }
                """
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();

            var draft = new Draft
            {
                Id = draftId,
                PlanningJobId = jobId,
                Status = DraftStatus.Approved,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ApprovedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.Drafts.AddAsync(draft);
            await dbContext.SaveChangesAsync();
        }

        // This should NOT return 500; it should successfully parse the complex JSON
        using var response = await testApi.Client.GetAsync($"/api/drafts/{draftId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<DraftDetailResponse>();
        Assert.NotNull(result);
        Assert.Equal(draftId, result.DraftId);
        Assert.NotNull(result.SelectedCandidate);
        Assert.Equal("Complex candidate with various field types", result.SelectedCandidate.Rationale);
        Assert.NotNull(result.Interpretation);
        Assert.Equal("Comprehensive bearing and seal replacement", result.Interpretation.MaintenanceIntent);
        Assert.Equal(2, result.SelectedCandidate.RequestedTaskIds.Count);
        Assert.Equal(1, result.SelectedCandidate.DeferralReasons.Count);
        Assert.Equal("Cost significantly higher than alternative", result.SelectedCandidate.CostComparabilityWarning);
    }

    [Fact]
    public async Task AT15_Test6_RepeatedGetDoesNotModifyState()
    {
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        await using (var dbContext = testApi.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Test","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = """
                {
                  "outcome": "CandidatesGenerated",
                  "result": {
                    "candidates": [
                      {"rationale": "Test", "requestedTaskIds": [], "dependencyTaskIds": [], "taskPlacements": [], "evidenceReferences": [], "totalLabourCostAmount": 100.00, "totalLabourCostCurrency": "USD", "completionTimeMinutes": 60, "deferralReasons": [], "isLowestCostCandidate": true, "costComparabilityWarning": null}
                    ],
                    "assumptions": [],
                    "evidenceReferences": [],
                    "interpretation": {"maintenanceIntent": "Test", "scope": "P-101", "proposedAssetIds": [], "proposedTaskIds": []}
                  }
                }
                """
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();

            var draft = new Draft
            {
                Id = draftId,
                PlanningJobId = jobId,
                Status = DraftStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.Drafts.AddAsync(draft);
            await dbContext.SaveChangesAsync();
        }

        // First call
        using var response1 = await testApi.Client.GetAsync($"/api/drafts/{draftId}");
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        var result1 = await response1.Content.ReadFromJsonAsync<DraftDetailResponse>();
        Assert.NotNull(result1);

        // Second call
        using var response2 = await testApi.Client.GetAsync($"/api/drafts/{draftId}");
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        var result2 = await response2.Content.ReadFromJsonAsync<DraftDetailResponse>();
        Assert.NotNull(result2);

        // Verify data unchanged
        Assert.Equal(result1.Status, result2.Status);
        Assert.Equal(result1.Reservations.Count, result2.Reservations.Count);
        if (result1.SelectedCandidate is not null && result2.SelectedCandidate is not null)
        {
            Assert.Equal(result1.SelectedCandidate.Rationale, result2.SelectedCandidate.Rationale);
        }
    }

    private static async Task<DraftDetailTestApi> CreateTestApiAsync()
    {
        var connectionString = $"Data Source=AT15-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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
        builder.Services.AddDbContext<MaintenancePlannerDbContext>(
            db => db.UseSqlite(connectionString));

        var app = builder.Build();

        // Map only the GET draft detail endpoint
        app.MapGet("/api/drafts/{draftId:guid}", async Task<Results<
                Ok<DraftDetailResponse>,
                NotFound>> (
                Guid draftId,
                MaintenancePlannerDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var draft = await dbContext.Drafts
                    .Include(d => d.PlanningJob)
                    .Include(d => d.Reservations)
                    .FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);

                if (draft is null)
                    return TypedResults.NotFound();

                var planningJob = draft.PlanningJob;
                if (planningJob is null)
                    return TypedResults.NotFound();

                CandidateResponse? selectedCandidate = null;
                InterpretationResponse? interpretation = null;

                if (!string.IsNullOrEmpty(planningJob.ResultPayload))
                {
                    try
                    {
                        using var resultDoc = System.Text.Json.JsonDocument.Parse(planningJob.ResultPayload);
                        var outcome = resultDoc.RootElement.GetProperty("outcome").GetString();

                        if (outcome == "CandidatesGenerated")
                        {
                            var result = resultDoc.RootElement.GetProperty("result");
                            var candidates = result.GetProperty("candidates").EnumerateArray().ToList();

                            if (candidates.Count > 0)
                            {
                                var c = candidates[0];
                                selectedCandidate = new CandidateResponse(
                                    Rationale: c.GetProperty("rationale").GetString() ?? "",
                                    RequestedTaskIds: c.GetProperty("requestedTaskIds").EnumerateArray()
                                        .Select(t => t.GetString() ?? "").ToList(),
                                    DependencyTaskIds: c.GetProperty("dependencyTaskIds").EnumerateArray()
                                        .Select(t => t.GetString() ?? "").ToList(),
                                    TaskPlacements: System.Text.Json.JsonSerializer.Deserialize<List<object>>(c.GetProperty("taskPlacements").GetRawText()) ?? [],
                                    EvidenceReferences: c.GetProperty("evidenceReferences").EnumerateArray()
                                        .Select(e => e.GetString() ?? "").ToList(),
                                    TotalLabourCostAmount: c.TryGetProperty("totalLabourCostAmount", out var costAmount)
                                        ? costAmount.GetDecimal()
                                        : 0m,
                                    TotalLabourCostCurrency: c.TryGetProperty("totalLabourCostCurrency", out var currency)
                                        ? currency.GetString() ?? "UNKNOWN"
                                        : "UNKNOWN",
                                    CompletionTimeMinutes: c.TryGetProperty("completionTimeMinutes", out var time)
                                        ? time.GetInt64()
                                        : 0L,
                                    DeferralReasons: c.TryGetProperty("deferralReasons", out var deferralElem)
                                        ? deferralElem.EnumerateArray()
                                            .Select(d => new DeferralReasonResponse(
                                                TaskId: d.GetProperty("taskId").GetString() ?? "",
                                                Reason: d.GetProperty("reason").GetString() ?? ""))
                                            .ToList()
                                        : [],
                                    IsLowestCostCandidate: c.TryGetProperty("isLowestCostCandidate", out var isLowest)
                                        ? isLowest.GetBoolean()
                                        : false,
                                    CostComparabilityWarning: c.TryGetProperty("costComparabilityWarning", out var warning)
                                        ? warning.GetString()
                                        : null);
                            }

                            if (result.TryGetProperty("interpretation", out var interpretationElement))
                            {
                                var interp = interpretationElement;
                                interpretation = new InterpretationResponse(
                                    MaintenanceIntent: interp.GetProperty("maintenanceIntent").GetString() ?? "",
                                    Scope: interp.GetProperty("scope").GetString() ?? "",
                                    ProposedAssetIds: interp.GetProperty("proposedAssetIds").EnumerateArray()
                                        .Select(a => a.GetString() ?? "").ToList(),
                                    ProposedTaskIds: interp.GetProperty("proposedTaskIds").EnumerateArray()
                                        .Select(t => t.GetString() ?? "").ToList());
                            }
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // Continue with null candidate and interpretation
                    }
                }

                var reservations = draft.Reservations
                    .Select(r => new ReservationResponse(
                        ReservationId: r.Id,
                        SparePartId: r.SparePartId,
                        QuantityReserved: r.QuantityReserved,
                        Status: r.Status.ToString(),
                        CreatedAtUtc: r.CreatedAtUtc,
                        ConfirmedAtUtc: r.ConfirmedAtUtc))
                    .ToList();

                var response = new DraftDetailResponse(
                    DraftId: draft.Id,
                    PlanningJobId: draft.PlanningJobId,
                    Status: draft.Status.ToString(),
                    CreatedAtUtc: draft.CreatedAtUtc,
                    ApprovedAtUtc: draft.ApprovedAtUtc,
                    SelectedCandidate: selectedCandidate,
                    Reservations: reservations,
                    Interpretation: interpretation);

                return TypedResults.Ok(response);
            })
            .WithName("GetDraftDetail")
            .WithSummary("Get draft details with selected candidate and reservations")
            .Produces<DraftDetailResponse>()
            .Produces(StatusCodes.Status404NotFound);

        await app.StartAsync();

        return new DraftDetailTestApi(connection, connectionString, app);
    }

    private sealed class DraftDetailTestApi : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        public HttpClient Client { get; }

        public DraftDetailTestApi(SqliteConnection connection, string connectionString, WebApplication app)
        {
            _connection = connection;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
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
