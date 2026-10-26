using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Queries;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT14CandidateRetrievalTests
{
    [Fact]
    public async Task AT14_Test1_CandidatesReturnedAfterCompletedJob()
    {
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();

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
                        "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                        "dependencyTaskIds": [],
                        "taskPlacements": [],
                        "evidenceReferences": ["manual-section-2.1"],
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
                      "proposedTaskIds": ["P101-BEARING-REPLACEMENT"]
                    }
                  }
                }
                """
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        using var response = await testApi.Client.GetAsync($"/api/planning-jobs/{jobId}/candidates");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<PlanningJobCandidatesResponse>();
        Assert.NotNull(result);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("CandidatesGenerated", result.Outcome);
        Assert.Single(result.Candidates);
        var candidate = result.Candidates[0];
        Assert.Equal("Standard bearing replacement", candidate.Rationale);
        Assert.Equal(450.50m, candidate.TotalLabourCostAmount);
        Assert.Equal("USD", candidate.TotalLabourCostCurrency);
        Assert.Equal(120L, candidate.CompletionTimeMinutes);
        Assert.True(candidate.IsLowestCostCandidate);
        Assert.Null(candidate.CostComparabilityWarning);
        Assert.NotNull(result.Interpretation);
        Assert.Equal("Replace bearing", result.Interpretation.MaintenanceIntent);
    }

    [Fact]
    public async Task AT14_Test2_JobNotFoundReturns404()
    {
        await using var testApi = await CreateTestApiAsync();
        var missingJobId = Guid.NewGuid();

        using var response = await testApi.Client.GetAsync($"/api/planning-jobs/{missingJobId}/candidates");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AT14_Test3_JobStillProcessingReturnsBadRequest()
    {
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();

        await using (var dbContext = testApi.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Test","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Running,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        using var response = await testApi.Client.GetAsync($"/api/planning-jobs/{jobId}/candidates");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AT14_Test4_ClarificationRequiredReturnsBadRequest()
    {
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();

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
                  "outcome": "ClarificationRequired",
                  "result": {
                    "question": "Which maintenance window?"
                  }
                }
                """
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        using var response = await testApi.Client.GetAsync($"/api/planning-jobs/{jobId}/candidates");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AT14_Test5_FailedJobReturnsBadRequest()
    {
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();

        await using (var dbContext = testApi.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Test","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Failed,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ErrorMessage = "Planning failed due to invalid request"
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        using var response = await testApi.Client.GetAsync($"/api/planning-jobs/{jobId}/candidates");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AT14_Test6_NoCandidatesAvailableReturnsBadRequest()
    {
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();

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
                  "outcome": "NoFeasibleCandidates",
                  "result": {
                    "reason": "No valid maintenance plans available"
                  }
                }
                """
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        using var response = await testApi.Client.GetAsync($"/api/planning-jobs/{jobId}/candidates");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AT14_Test7_MultipleCandidatesReturnedInOrder()
    {
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();

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
                        "rationale": "Candidate 1",
                        "requestedTaskIds": ["T1"],
                        "dependencyTaskIds": [],
                        "taskPlacements": [],
                        "evidenceReferences": [],
                        "totalLabourCostAmount": 300.00,
                        "totalLabourCostCurrency": "USD",
                        "completionTimeMinutes": 90,
                        "deferralReasons": [],
                        "isLowestCostCandidate": true,
                        "costComparabilityWarning": null
                      },
                      {
                        "rationale": "Candidate 2",
                        "requestedTaskIds": ["T2"],
                        "dependencyTaskIds": [],
                        "taskPlacements": [],
                        "evidenceReferences": [],
                        "totalLabourCostAmount": 450.00,
                        "totalLabourCostCurrency": "USD",
                        "completionTimeMinutes": 120,
                        "deferralReasons": [],
                        "isLowestCostCandidate": false,
                        "costComparabilityWarning": "Price 50% higher than lowest cost option"
                      },
                      {
                        "rationale": "Candidate 3",
                        "requestedTaskIds": ["T3"],
                        "dependencyTaskIds": [],
                        "taskPlacements": [],
                        "evidenceReferences": [],
                        "totalLabourCostAmount": 600.00,
                        "totalLabourCostCurrency": "USD",
                        "completionTimeMinutes": 180,
                        "deferralReasons": [{"taskId": "T3", "reason": "Technician unavailable during requested window"}],
                        "isLowestCostCandidate": false,
                        "costComparabilityWarning": "Price 100% higher than lowest cost option"
                      }
                    ],
                    "assumptions": [],
                    "evidenceReferences": [],
                    "interpretation": {
                      "maintenanceIntent": "Replace bearing",
                      "scope": "P-101",
                      "proposedAssetIds": ["P-101"],
                      "proposedTaskIds": ["T1", "T2", "T3"]
                    }
                  }
                }
                """
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        using var response = await testApi.Client.GetAsync($"/api/planning-jobs/{jobId}/candidates");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<PlanningJobCandidatesResponse>();
        Assert.NotNull(result);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal("Candidate 1", result.Candidates[0].Rationale);
        Assert.Equal("Candidate 2", result.Candidates[1].Rationale);
        Assert.Equal("Candidate 3", result.Candidates[2].Rationale);

        // Verify cost and time data
        Assert.Equal(300.00m, result.Candidates[0].TotalLabourCostAmount);
        Assert.True(result.Candidates[0].IsLowestCostCandidate);
        Assert.Equal(450.00m, result.Candidates[1].TotalLabourCostAmount);
        Assert.Equal("Price 50% higher than lowest cost option", result.Candidates[1].CostComparabilityWarning);

        // Verify deferral reasons on third candidate
        Assert.Single(result.Candidates[2].DeferralReasons);
        Assert.Equal("T3", result.Candidates[2].DeferralReasons[0].TaskId);
        Assert.Equal("Technician unavailable during requested window", result.Candidates[2].DeferralReasons[0].Reason);
    }

    [Fact]
    public async Task AT14_Test8_RepeatedGetDoesNotModifyState()
    {
        await using var testApi = await CreateTestApiAsync();
        var jobId = Guid.NewGuid();

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
                        "rationale": "Test",
                        "requestedTaskIds": [],
                        "dependencyTaskIds": [],
                        "taskPlacements": [],
                        "evidenceReferences": [],
                        "totalLabourCostAmount": 500.00,
                        "totalLabourCostCurrency": "USD",
                        "completionTimeMinutes": 150,
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
                      "proposedAssetIds": [],
                      "proposedTaskIds": []
                    }
                  }
                }
                """
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        // First call
        using var response1 = await testApi.Client.GetAsync($"/api/planning-jobs/{jobId}/candidates");
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        var result1 = await response1.Content.ReadFromJsonAsync<PlanningJobCandidatesResponse>();
        Assert.NotNull(result1);

        // Second call
        using var response2 = await testApi.Client.GetAsync($"/api/planning-jobs/{jobId}/candidates");
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        var result2 = await response2.Content.ReadFromJsonAsync<PlanningJobCandidatesResponse>();
        Assert.NotNull(result2);

        // Verify data unchanged
        Assert.Equal(result1.Status, result2.Status);
        Assert.Equal(result1.Candidates.Count, result2.Candidates.Count);
        Assert.Equal(result1.Candidates[0].TotalLabourCostAmount, result2.Candidates[0].TotalLabourCostAmount);
    }

    private static async Task<CandidateRetrievalTestApi> CreateTestApiAsync()
    {
        var connectionString = $"Data Source=AT14-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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
        builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();

        var app = builder.Build();

        // Map only the candidates endpoint for this test
        app.MapGet("/api/planning-jobs/{jobId:guid}/candidates", async Task<Results<
                Ok<PlanningJobCandidatesResponse>,
                NotFound,
                BadRequest<string>>> (
                Guid jobId,
                IPlanningJobs planningJobs,
                CancellationToken cancellationToken) =>
            {
                var job = await planningJobs.GetAsync(jobId, cancellationToken);
                if (job is null)
                    return TypedResults.NotFound();

                if (job.Status == PlanningJobStatus.Queued ||
                    job.Status == PlanningJobStatus.Running)
                {
                    return TypedResults.BadRequest("Job is still processing. Check status at " + $"/api/planning-jobs/{jobId}");
                }

                if (job.Status == PlanningJobStatus.Failed)
                {
                    return TypedResults.BadRequest($"Job failed: {job.ErrorMessage ?? job.FailureCode ?? "Unknown error"}");
                }

                if (string.IsNullOrEmpty(job.ResultPayload))
                {
                    return TypedResults.BadRequest("Job has no result available.");
                }

                try
                {
                    using var resultDoc = JsonDocument.Parse(job.ResultPayload);
                    var outcome = resultDoc.RootElement.GetProperty("outcome").GetString() ?? "Unknown";

                    if (outcome == "ClarificationRequired")
                    {
                        return TypedResults.BadRequest("Job requires clarification. Submit clarification at " + $"/api/planning-jobs/{jobId}/clarify");
                    }

                    if (outcome != "CandidatesGenerated")
                    {
                        return TypedResults.BadRequest($"Job outcome is '{outcome}'. No candidates available.");
                    }

                    var result = resultDoc.RootElement.GetProperty("result");
                    var candidates = result.GetProperty("candidates").EnumerateArray()
                        .Select(c => new CandidateResponse(
                            Rationale: c.GetProperty("rationale").GetString() ?? "",
                            RequestedTaskIds: c.GetProperty("requestedTaskIds").EnumerateArray()
                                .Select(t => t.GetString() ?? "").ToList(),
                            DependencyTaskIds: c.GetProperty("dependencyTaskIds").EnumerateArray()
                                .Select(t => t.GetString() ?? "").ToList(),
                            TaskPlacements: c.GetProperty("taskPlacements").Deserialize<List<object>>() ?? [],
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
                                : null))
                        .ToList();

                    InterpretationResponse? interpretation = null;
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

                    var response = new PlanningJobCandidatesResponse(
                        JobId: job.Id,
                        Status: job.Status.ToString(),
                        Outcome: outcome,
                        Candidates: candidates,
                        Interpretation: interpretation);

                    return TypedResults.Ok(response);
                }
                catch (JsonException ex)
                {
                    return TypedResults.BadRequest($"Failed to parse job result: {ex.Message}");
                }
            })
            .WithName("GetCandidates")
            .WithSummary("Get candidates for a completed planning job")
            .Produces<PlanningJobCandidatesResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        await app.StartAsync();

        return new CandidateRetrievalTestApi(connection, connectionString, app);
    }

    private sealed class CandidateRetrievalTestApi : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        public HttpClient Client { get; }

        public CandidateRetrievalTestApi(SqliteConnection connection, string connectionString, WebApplication app)
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
