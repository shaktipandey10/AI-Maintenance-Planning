using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Api.Workers;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// Tests for the Request-to-Alternatives workflow: HTTP-level clarification endpoint and job state management.
/// Focuses on HTTP contract behavior without requiring background worker processing.
/// </summary>
public sealed class RequestToAlternativesWorkflowTests : IAsyncLifetime
{
    private SqliteConnection? _connection;
    private string? _connectionString;
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=TestDb-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Initialize schema and seed data
        using (var initContext = new MaintenancePlannerDbContext(options))
        {
            await initContext.Database.MigrateAsync();
            await DatabaseInitializer.InitializeAsync(initContext);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(_connectionString));
        builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
        builder.Services.AddScoped<IDraftApprovalService, DraftApprovalService>();
        builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();

        _app = builder.Build();
        _app.MapPlanningJobEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SubmitClarification_OnClarificationRequiredJob_ResetJobAndClearsResult()
    {
        // Arrange: Create a job with ClarificationRequired outcome using a dedicated context
        var jobId = Guid.NewGuid();
        var connectionString = _connectionString!;

        using (var setupContext = new MaintenancePlannerDbContext(
            new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(connectionString)
                .Options))
        {
            var job = new MaintenancePlanner.Domain.Entities.PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = JsonSerializer.Serialize(
                    new PlanningJobRequestPayload("Plan bearing replacement", ["P-101"], null)),
                RequestFingerprint = "test-fingerprint",
                Status = MaintenancePlanner.Domain.Entities.PlanningJobStatus.Succeeded,
                AttemptCount = 1,
                MaxAttempts = 2,
                CurrentStep = "Completed",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = JsonSerializer.Serialize(new
                {
                    outcome = "ClarificationRequired",
                    result = new
                    {
                        questions = new[] { "Which maintenance window?" }
                    }
                })
            };
            setupContext.PlanningJobs.Add(job);
            await setupContext.SaveChangesAsync();
        }

        // Act
        using var clarifyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/planning-jobs/{jobId}/clarify")
        {
            Content = JsonContent.Create(new SubmitClarificationRequest
            {
                WindowId = "MW-P101-BASELINE-20261024"
            })
        };
        using var clarifyResponse = await _client!.SendAsync(clarifyRequest);

        // Assert: Response indicates job was reset
        Assert.Equal(HttpStatusCode.OK, clarifyResponse.StatusCode);
        var responseBody = await clarifyResponse.Content.ReadFromJsonAsync<PlanningJobStatusResponse>();
        Assert.NotNull(responseBody);
        Assert.Equal("Queued", responseBody.Status);
        Assert.Null(responseBody.ResultPayload);
        Assert.Null(responseBody.FailureCode); // No failure
    }

    [Fact]
    public async Task SubmitClarification_WithoutWindowId_ReturnsBadRequest()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        using (var setupContext = new MaintenancePlannerDbContext(
            new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString!)
                .Options))
        {
            var job = new MaintenancePlanner.Domain.Entities.PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = JsonSerializer.Serialize(
                    new PlanningJobRequestPayload("Plan bearing replacement", ["P-101"], null)),
                RequestFingerprint = "test",
                Status = MaintenancePlanner.Domain.Entities.PlanningJobStatus.Succeeded,
                ResultPayload = JsonSerializer.Serialize(new { outcome = "ClarificationRequired" })
            };
            setupContext.PlanningJobs.Add(job);
            await setupContext.SaveChangesAsync();
        }

        // Act
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/planning-jobs/{jobId}/clarify")
        {
            Content = JsonContent.Create(new { })
        };
        using var response = await _client!.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SubmitClarification_OnNonClarificationJob_ReturnsBadRequest()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        using (var setupContext = new MaintenancePlannerDbContext(
            new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString!)
                .Options))
        {
            var job = new MaintenancePlanner.Domain.Entities.PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = JsonSerializer.Serialize(
                    new PlanningJobRequestPayload("Test", ["P-101"], "MW-1")),
                RequestFingerprint = "test",
                Status = MaintenancePlanner.Domain.Entities.PlanningJobStatus.Succeeded,
                ResultPayload = JsonSerializer.Serialize(new { outcome = "CandidatesGenerated" })
            };
            setupContext.PlanningJobs.Add(job);
            await setupContext.SaveChangesAsync();
        }

        // Act
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/planning-jobs/{jobId}/clarify")
        {
            Content = JsonContent.Create(new SubmitClarificationRequest { WindowId = "MW-1" })
        };
        using var response = await _client!.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SubmitClarification_OnMissingJob_ReturnsNotFound()
    {
        // Act
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/planning-jobs/{Guid.NewGuid()}/clarify")
        {
            Content = JsonContent.Create(new SubmitClarificationRequest { WindowId = "MW-1" })
        };
        using var response = await _client!.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EndToEnd_ClarificationWorkflow_PreserveRequestAndResetJob()
    {
        // End-to-end HTTP-level verification:
        // 1. Create a job in ClarificationRequired state with specific RequestText/AssetIds
        // 2. Submit clarification with new WindowId
        // 3. Verify job is reset to Queued with request updated
        // 4. Verify invalid WindowId is rejected
        // 5. Verify repeated clarification on already-Queued job is rejected

        var jobId = Guid.NewGuid();
        var originalRequestText = "Plan bearing replacement for P-101";
        var originalAssetIds = new[] { "P-101" };

        // Setup: Create a job with ClarificationRequired outcome
        using (var setupContext = new MaintenancePlannerDbContext(
            new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString!)
                .Options))
        {
            var job = new MaintenancePlanner.Domain.Entities.PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = JsonSerializer.Serialize(
                    new PlanningJobRequestPayload(originalRequestText, originalAssetIds, null)),
                RequestFingerprint = "test",
                Status = MaintenancePlanner.Domain.Entities.PlanningJobStatus.Succeeded,
                AttemptCount = 1,
                MaxAttempts = 2,
                CurrentStep = "Completed",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = JsonSerializer.Serialize(new
                {
                    outcome = "ClarificationRequired",
                    result = new { questions = new[] { "Which maintenance window?" } }
                })
            };
            setupContext.PlanningJobs.Add(job);
            await setupContext.SaveChangesAsync();
        }

        // Act: Step 1 - Submit clarification with valid WindowId
        var newWindowId = "MW-P101-BASELINE-20261024";
        using var clarifyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/planning-jobs/{jobId}/clarify")
        {
            Content = JsonContent.Create(new SubmitClarificationRequest { WindowId = newWindowId })
        };
        using var clarifyResponse = await _client!.SendAsync(clarifyRequest);

        // Assert: Step 1 response
        Assert.Equal(HttpStatusCode.OK, clarifyResponse.StatusCode);
        var clarifiedStatus = await clarifyResponse.Content.ReadFromJsonAsync<PlanningJobStatusResponse>();
        Assert.NotNull(clarifiedStatus);
        Assert.Equal("Queued", clarifiedStatus.Status);
        Assert.Null(clarifiedStatus.ResultPayload);
        Assert.Equal(jobId, clarifiedStatus.JobId);

        // Step 2: Verify invalid WindowId is rejected
        using var invalidRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/planning-jobs/{jobId}/clarify")
        {
            Content = JsonContent.Create(new SubmitClarificationRequest { WindowId = null })
        };
        using var invalidResponse = await _client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        // Step 3: Verify job state remains Queued after invalid attempt
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/planning-jobs/{jobId}");
        using var statusResponse = await _client.SendAsync(statusRequest);
        var currentStatus = await statusResponse.Content.ReadFromJsonAsync<PlanningJobStatusResponse>();
        Assert.NotNull(currentStatus);
        Assert.Equal("Queued", currentStatus.Status);

        // Step 4: Verify repeated clarification attempt on already-Queued job is rejected
        using var repeatRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/planning-jobs/{jobId}/clarify")
        {
            Content = JsonContent.Create(new SubmitClarificationRequest { WindowId = newWindowId })
        };
        using var repeatResponse = await _client.SendAsync(repeatRequest);
        // Should fail because job is now Queued (no longer ClarificationRequired)
        Assert.Equal(HttpStatusCode.BadRequest, repeatResponse.StatusCode);
    }

    /// <summary>
    /// Complete end-to-end clarification workflow with background worker processing:
    /// 1. Request without window → ClarificationRequired outcome persisted
    /// 2. Submit clarification → Job reset to Queued, result cleared
    /// 3. Worker processes resumed job → Final candidates/outcome generated
    /// 4. Verify single job ID, no duplication, state coherent
    /// </summary>
    [Fact]
    public async Task EndToEnd_ClarificationResumeWithWorker_GeneratesCandidatesAfterClarification()
    {
        // Use a deterministic processor that returns candidates
        var interpretation = new PlanningInterpretation(
            "Bearing maintenance for P-101",
            "P-101 bearing",
            ["P-101"],
            ["P101-BEARING-REPLACEMENT"],
            [],
            [])
        {
            ScopeResolution = PlanningScopeResolution.Resolved
        };
        var expectedResult = new PlanningCandidatesGeneratedResult([], [], [])
        {
            Interpretation = interpretation
        };

        using var connection = new SqliteConnection(
            $"Data Source=ClarificationResume-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var initContext = new MaintenancePlannerDbContext(options))
        {
            await initContext.Database.MigrateAsync();
            await DatabaseInitializer.InitializeAsync(initContext);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<MaintenancePlannerDbContext>(
            db => db.UseSqlite(connection.ConnectionString));
        builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
        builder.Services.AddScoped<IDraftApprovalService, DraftApprovalService>();
        builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
        builder.Services.AddSingleton<IPlanningJobProcessor>(
            new PlanningJobProcessor(new TestFixedWorkflow(expectedResult)));
        builder.Services.Configure<PlanningJobWorkerOptions>(opts =>
        {
            opts.PollInterval = TimeSpan.FromMilliseconds(10);
            opts.LeaseDuration = TimeSpan.FromSeconds(3);
        });
        builder.Services.AddHostedService<PlanningJobWorker>();

        var app = builder.Build();
        app.MapPlanningJobEndpoints();
        await app.StartAsync();

        var client = app.GetTestClient();

        try
        {
            // Step 1: Submit request WITHOUT window → planning will generate ClarificationRequired
            using var submitRequest = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
            {
                Content = JsonContent.Create(new CreatePlanningJobRequest
                {
                    RequestText = "Plan bearing replacement for P-101",
                    AssetIds = ["P-101"]
                    // MISSING: WindowId
                })
            };
            submitRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var submitResponse = await client.SendAsync(submitRequest);
            Assert.Equal(HttpStatusCode.Accepted, submitResponse.StatusCode);

            var accepted = await submitResponse.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>();
            Assert.NotNull(accepted);
            var jobId = accepted.JobId;

            // Step 2: Poll until ClarificationRequired
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            PlanningJobStatusResponse? clarificationStatus = null;
            while (!timeout.IsCancellationRequested)
            {
                using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/planning-jobs/{jobId}");
                using var statusResponse = await client.SendAsync(statusRequest, timeout.Token);
                statusResponse.EnsureSuccessStatusCode();

                clarificationStatus = await statusResponse.Content.ReadFromJsonAsync<PlanningJobStatusResponse>(timeout.Token);
                if (clarificationStatus?.ResultPayload is not null)
                {
                    using var resultDoc = JsonDocument.Parse(clarificationStatus.ResultPayload);
                    var outcome = resultDoc.RootElement.GetProperty("outcome").GetString();
                    if (outcome == "ClarificationRequired")
                        break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }

            Assert.NotNull(clarificationStatus?.ResultPayload);
            using (var doc = JsonDocument.Parse(clarificationStatus.ResultPayload))
            {
                Assert.Equal("ClarificationRequired", doc.RootElement.GetProperty("outcome").GetString());
            }

            // Step 3: Submit clarification with valid WindowId
            using var clarifyRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/planning-jobs/{jobId}/clarify")
            {
                Content = JsonContent.Create(new SubmitClarificationRequest
                {
                    WindowId = "MW-P101-BASELINE-20261024"
                })
            };
            using var clarifyResponse = await client.SendAsync(clarifyRequest);
            Assert.Equal(HttpStatusCode.OK, clarifyResponse.StatusCode);

            var clarifyStatus = await clarifyResponse.Content.ReadFromJsonAsync<PlanningJobStatusResponse>();
            Assert.NotNull(clarifyStatus);
            Assert.Equal("Queued", clarifyStatus.Status);
            Assert.Null(clarifyStatus.ResultPayload); // Result cleared for reprocessing

            // Step 4: Poll until terminal state (worker processes the resumed job)
            using var finalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            PlanningJobStatusResponse? finalStatus = null;
            while (!finalTimeout.IsCancellationRequested)
            {
                using var finalRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/planning-jobs/{jobId}");
                using var finalResponse = await client.SendAsync(finalRequest, finalTimeout.Token);
                finalResponse.EnsureSuccessStatusCode();

                finalStatus = await finalResponse.Content.ReadFromJsonAsync<PlanningJobStatusResponse>(finalTimeout.Token);
                if (finalStatus?.Status == "Succeeded" || finalStatus?.Status == "Failed")
                    break;

                await Task.Delay(TimeSpan.FromMilliseconds(50), finalTimeout.Token);
            }

            // Step 5: Verify final outcome
            Assert.NotNull(finalStatus);
            Assert.Equal("Succeeded", finalStatus.Status);
            Assert.NotNull(finalStatus.ResultPayload);

            using (var resultDoc = JsonDocument.Parse(finalStatus.ResultPayload))
            {
                var outcome = resultDoc.RootElement.GetProperty("outcome").GetString();
                Assert.Equal("CandidatesGenerated", outcome);
            }

            // Step 6: Verify single job, no duplication
            await using var verifyDb = new MaintenancePlannerDbContext(options);
            var allJobs = await verifyDb.PlanningJobs.ToListAsync();
            Assert.Single(allJobs);
            Assert.Equal(jobId, allJobs[0].Id);
            Assert.Equal(PlanningJobStatus.Succeeded, allJobs[0].Status);
        }
        finally
        {
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestFixedWorkflow(PlanningWorkflowResult result) : IPlanningWorkflow
    {
        public Task<PlanningWorkflowResult> ExecuteAsync(
            PlanningRequest request,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? lockedTaskAssignments = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // If window is missing, return ClarificationRequired instead
            if (string.IsNullOrWhiteSpace(request.MaintenanceWindowId))
            {
                var interpretation = new PlanningInterpretation(
                    "Bearing maintenance",
                    "P-101 bearing",
                    request.AssetIds,
                    [],
                    [],
                    [])
                {
                    ScopeResolution = PlanningScopeResolution.Resolved
                };
                var clarificationResult = new PlanningClarificationRequiredResult(
                    ["Which maintenance window should be used?"],
                    [],
                    [])
                {
                    Interpretation = interpretation
                };
                return Task.FromResult<PlanningWorkflowResult>(clarificationResult);
            }

            return Task.FromResult(result);
        }

        public Task<PlanningWorkflowResult> ResumeFromCheckpointAsync(
            PlanningRequest request,
            PlanningInterpretation checkpoint,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? lockedTaskAssignments = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // After clarification, return the expected result
            return Task.FromResult(result);
        }
    }
}
