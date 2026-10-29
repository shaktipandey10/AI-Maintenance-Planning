using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Api.Workers;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.ManualEvidence;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>AT03 integration tests: conflict persistence and API exposure.</summary>
public sealed class AT03_ConflictPersistenceAndApiTests
{
    [Fact]
    public async Task AT03_ScopeBoundaryStatement_DoesNotBlockPlanning()
    {
        // Arrange: Create fixture with controlled interpreter that directs to P101-BEARING-REPLACEMENT
        // MAN-P102-SCOPE-001 contains "Do not substitute this scope for the P-101 bearing-replacement scope"
        // This is a scope boundary, not a genuine conflict, and should not block planning
        var ollamaHandler = new ControlledHandler(
            """
            {"maintenanceIntent":"bearing replacement","scope":"P-101 bearing","scopeResolution":"Resolved","proposedAssetIds":["P-101"],"proposedTaskIds":["P101-BEARING-REPLACEMENT"],"missingInformation":[],"assumptions":[],"scopeMappings":[{"taskId":"P101-BEARING-REPLACEMENT","assetId":"P-101"}]}
            """);
        await using var fixture = await AT03IntegrationFixture.CreateAsync(ollamaHandler);

        // Act: Submit planning job via API
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
        {
            Content = JsonContent.Create(new CreatePlanningJobRequest
            {
                RequestText = "Replace the P-101 bearing.",
                AssetIds = ["P-101"],
                WindowId = "MW-P101-BASELINE-20261024"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var acceptedResponse = await fixture.Client.SendAsync(request);
        acceptedResponse.EnsureSuccessStatusCode();
        var accepted = (await acceptedResponse.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>())!;
        var jobStatus = await fixture.WaitForSucceededAsync(accepted.JobId);

        // Assert: Result should have candidates (not DataUnavailable) because scope boundary is not a conflict
        Assert.NotNull(jobStatus.ResultPayload);
        using var payload = JsonDocument.Parse(jobStatus.ResultPayload);

        // Verify outcome is CandidatesGenerated (planning succeeds)
        Assert.Equal("CandidatesGenerated", payload.RootElement.GetProperty("outcome").GetString());

        // Verify candidates were generated
        var result = payload.RootElement.GetProperty("result");
        var candidates = result.GetProperty("candidates");
        Assert.True(candidates.GetArrayLength() > 0, "Candidates should be generated for P-101 bearing replacement");

        // Verify no MANUAL_EVIDENCE_CONFLICT_CROSS_DOCUMENT violation (scope boundary ≠ conflict)
        var hasViolations = result.TryGetProperty("violations", out var violationsElement);
        if (hasViolations)
        {
            var conflictViolation = violationsElement.EnumerateArray()
                .FirstOrDefault(v => v.GetProperty("code").GetString() == "MANUAL_EVIDENCE_CONFLICT_CROSS_DOCUMENT");
            Assert.True(conflictViolation.ValueKind == JsonValueKind.Undefined,
                "Scope boundary statement should not produce MANUAL_EVIDENCE_CONFLICT_CROSS_DOCUMENT violation");
        }
    }

    [Fact]
    public async Task AT03_ScopeBoundaryStatement_VisibleViaGetJobApi_ButNotAsConflict()
    {
        // Arrange
        var ollamaHandler = new ControlledHandler(
            """
            {"maintenanceIntent":"bearing replacement","scope":"P-101 bearing","scopeResolution":"Resolved","proposedAssetIds":["P-101"],"proposedTaskIds":["P101-BEARING-REPLACEMENT"],"missingInformation":[],"assumptions":[],"scopeMappings":[{"taskId":"P101-BEARING-REPLACEMENT","assetId":"P-101"}]}
            """);
        await using var fixture = await AT03IntegrationFixture.CreateAsync(ollamaHandler);

        // Act: Submit job and get status via API
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
        {
            Content = JsonContent.Create(new CreatePlanningJobRequest
            {
                RequestText = "Replace the P-101 bearing.",
                AssetIds = ["P-101"],
                WindowId = "MW-P101-BASELINE-20261024"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var acceptedResponse = await fixture.Client.SendAsync(request);
        acceptedResponse.EnsureSuccessStatusCode();
        var accepted = (await acceptedResponse.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>())!;

        // Wait and call GET endpoint
        var jobStatus = await fixture.WaitForSucceededAsync(accepted.JobId);

        // Assert: GET response shows successful planning with candidates
        using var statusResponse = await fixture.Client.GetAsync($"/api/planning-jobs/{accepted.JobId}");
        statusResponse.EnsureSuccessStatusCode();

        var apiStatus = (await statusResponse.Content.ReadFromJsonAsync<PlanningJobStatusResponse>())!;

        // Verify API response structure
        Assert.Equal("Succeeded", apiStatus.Status);
        Assert.NotNull(apiStatus.ResultPayload);

        using var apiPayload = JsonDocument.Parse(apiStatus.ResultPayload);
        // Outcome should be CandidatesGenerated, not DataUnavailable
        Assert.Equal("CandidatesGenerated", apiPayload.RootElement.GetProperty("outcome").GetString());

        // Verify candidates are present
        var apiResult = apiPayload.RootElement.GetProperty("result");
        var candidates = apiResult.GetProperty("candidates");
        Assert.True(candidates.GetArrayLength() > 0, "Candidates should be present in successful planning");

        // Verify no false-positive conflict violation
        var hasApiViolations = apiResult.TryGetProperty("violations", out var apiViolationsElement);
        if (hasApiViolations)
        {
            var apiConflict = apiViolationsElement.EnumerateArray()
                .FirstOrDefault(v => v.GetProperty("code").GetString() == "MANUAL_EVIDENCE_CONFLICT_CROSS_DOCUMENT");
            Assert.True(apiConflict.ValueKind == JsonValueKind.Undefined,
                "Scope boundary statement should not be treated as conflict violation");
        }
    }

    [Fact]
    public async Task AT03_ScopeBoundaryStatement_ProducesCandidates()
    {
        // Arrange: Verify that scope boundary statements (not genuine conflicts) do allow candidate generation
        var ollamaHandler = new ControlledHandler(
            """
            {"maintenanceIntent":"bearing replacement","scope":"P-101 bearing","scopeResolution":"Resolved","proposedAssetIds":["P-101"],"proposedTaskIds":["P101-BEARING-REPLACEMENT"],"missingInformation":[],"assumptions":[],"scopeMappings":[{"taskId":"P101-BEARING-REPLACEMENT","assetId":"P-101"}]}
            """);
        await using var fixture = await AT03IntegrationFixture.CreateAsync(ollamaHandler);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
        {
            Content = JsonContent.Create(new CreatePlanningJobRequest
            {
                RequestText = "Replace the P-101 bearing.",
                AssetIds = ["P-101"],
                WindowId = "MW-P101-BASELINE-20261024"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var acceptedResponse = await fixture.Client.SendAsync(request);
        var accepted = (await acceptedResponse.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>())!;
        var jobStatus = await fixture.WaitForSucceededAsync(accepted.JobId);

        // Assert: Candidates should be present (scope boundary ≠ blocking conflict)
        using var payload = JsonDocument.Parse(jobStatus.ResultPayload!);
        var result = payload.RootElement.GetProperty("result");

        Assert.True(result.TryGetProperty("candidates", out var candidates),
            "Candidates should exist in result - scope boundary is not a blocking conflict");
        Assert.True(candidates.GetArrayLength() > 0,
            "At least one candidate should be generated for valid P-101 bearing replacement request");
    }

    [Fact]
    public async Task AT03_GenuineConflict_StillBlocksPlanning()
    {
        // Regression test: Ensure genuine technical conflicts still block planning
        // This test uses a controlled evidence retriever that returns contradictory evidence
        var ollamaHandler = new ControlledHandler(
            """
            {"maintenanceIntent":"bearing replacement","scope":"P-101 bearing","scopeResolution":"Resolved","proposedAssetIds":["P-101"],"proposedTaskIds":["P101-BEARING-REPLACEMENT"],"missingInformation":[],"assumptions":[],"scopeMappings":[{"taskId":"P101-BEARING-REPLACEMENT","assetId":"P-101"}]}
            """);
        await using var fixture = await AT03IntegrationFixture.CreateAsync(
            ollamaHandler,
            new GenuineConflictEvidenceRetriever());

        // Act: Submit planning job
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
        {
            Content = JsonContent.Create(new CreatePlanningJobRequest
            {
                RequestText = "Replace the P-101 bearing.",
                AssetIds = ["P-101"],
                WindowId = "MW-P101-BASELINE-20261024"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var acceptedResponse = await fixture.Client.SendAsync(request);
        acceptedResponse.EnsureSuccessStatusCode();
        var accepted = (await acceptedResponse.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>())!;
        var jobStatus = await fixture.WaitForSucceededAsync(accepted.JobId);

        // Assert: Genuine conflict should still block planning
        Assert.NotNull(jobStatus.ResultPayload);
        using var payload = JsonDocument.Parse(jobStatus.ResultPayload);

        // Outcome should be DataUnavailable
        Assert.Equal("DataUnavailable", payload.RootElement.GetProperty("outcome").GetString());

        // Should have conflict violation
        var violations = payload.RootElement.GetProperty("result").GetProperty("violations");
        var conflictViolation = violations.EnumerateArray()
            .FirstOrDefault(v => v.GetProperty("code").GetString() == "MANUAL_EVIDENCE_CONFLICT_CROSS_DOCUMENT");

        Assert.False(conflictViolation.ValueKind == JsonValueKind.Undefined,
            "Genuine technical conflict should still produce MANUAL_EVIDENCE_CONFLICT_CROSS_DOCUMENT violation");
    }

    private sealed class GenuineConflictEvidenceRetriever : IManualEvidenceRetriever
    {
        public Task<IReadOnlyList<PlanningEvidenceReference>> SearchAsync(
            string query,
            string? documentId,
            CancellationToken cancellationToken)
        {
            var result = (documentId) switch
            {
                "MAN-P101-001" => new List<PlanningEvidenceReference>
                {
                    new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P101-001", "MAN-P101-001", null, 1,
                        "Planning assumptions", "Bearing replacement requires 180 minutes and 2 technicians")
                },
                "MAN-P101-CONFLICTING" => new List<PlanningEvidenceReference>
                {
                    new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P101-CONFLICTING", "MAN-P101-CONFLICTING",
                        null, 1, "Alternate method",
                        "Do not use the standard bearing replacement method. Use inspection-only method instead")
                },
                null => new List<PlanningEvidenceReference>
                {
                    new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P101-001", "MAN-P101-001", null, 1,
                        "Planning assumptions", "Bearing replacement requires 180 minutes and 2 technicians"),
                    new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P101-CONFLICTING", "MAN-P101-CONFLICTING",
                        null, 1, "Alternate method",
                        "Do not use the standard bearing replacement method. Use inspection-only method instead")
                },
                _ => new List<PlanningEvidenceReference>()
            };
            return Task.FromResult<IReadOnlyList<PlanningEvidenceReference>>(result);
        }
    }

    private sealed class ControlledHandler(string output) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var content = JsonSerializer.Serialize(new
            {
                model = "qwen2.5:7b",
                created_at = "2026-10-24T00:00:00Z",
                message = new
                {
                    role = "assistant",
                    content = output
                },
                done = true
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ControlledEvidenceRetriever : IManualEvidenceRetriever
    {
        public Task<IReadOnlyList<PlanningEvidenceReference>> SearchAsync(
            string query,
            string? documentId,
            CancellationToken cancellationToken)
        {
            var result = (documentId) switch
            {
                "MAN-P101-001" => new List<PlanningEvidenceReference>
                {
                    new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P101-001", "MAN-P101-001", null, 1,
                        "Planning assumptions", "Bearing replacement requires 180 minutes and 2 technicians")
                },
                "MAN-P102-SCOPE-001" => new List<PlanningEvidenceReference>
                {
                    new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P102-SCOPE-001", "MAN-P102-SCOPE-001", null, 1,
                        "Scope", "Inspection only - Do not substitute this scope for the P-101 bearing-replacement scope")
                },
                null => new List<PlanningEvidenceReference>
                {
                    new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P101-001", "MAN-P101-001", null, 1,
                        "Planning assumptions", "Bearing replacement requires 180 minutes and 2 technicians"),
                    new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P102-SCOPE-001", "MAN-P102-SCOPE-001", null, 1,
                        "Scope", "Inspection only - Do not substitute this scope for the P-101 bearing-replacement scope")
                },
                _ => new List<PlanningEvidenceReference>()
            };
            return Task.FromResult<IReadOnlyList<PlanningEvidenceReference>>(result);
        }
    }

    private sealed class AT03IntegrationFixture(
        SqliteConnection keeperConnection,
        WebApplication app,
        HttpClient client)
        : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<AT03IntegrationFixture> CreateAsync(
            HttpMessageHandler ollamaHandler,
            IManualEvidenceRetriever? evidenceRetriever = null)
        {
            var connectionString = $"Data Source=AT03-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();

            var dbOptions = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using (var dbContext = new MaintenancePlannerDbContext(dbOptions))
                await DatabaseInitializer.InitializeAsync(dbContext);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration["Ollama:BaseUrl"] = "http://ollama.test:11434";
            builder.Configuration["Ollama:Model"] = "qwen2.5:7b";
            builder.Configuration["Ollama:TimeoutSeconds"] = "60";
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(options =>
                options.UseSqlite(connectionString));
            builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            builder.Services.AddScoped<ISparePartReadModelProvider, ScenarioAwareSparePartReadModelProvider>();
            builder.Services.AddScoped<PlanningCandidateValidator>();
            builder.Services.AddScoped<CandidateCostCalculator>();
            builder.Services.AddScoped<DeterministicPlanningScheduler>();

            var manualsPath = FindRepositoryRoot();
            builder.Services.AddSingleton<IManualEvidenceRetriever>(
                evidenceRetriever ?? new ControlledEvidenceRetriever());

            builder.Services.AddHttpClient<IPlanningRequestInterpreter, OllamaPlanningRequestInterpreter>(
                    (serviceProvider, client) =>
                    {
                        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OllamaOptions>>().Value;
                        client.BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/", UriKind.Absolute);
                        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                    })
                .ConfigurePrimaryHttpMessageHandler(() => ollamaHandler);

            builder.Services.AddOptions<OllamaOptions>()
                .Bind(builder.Configuration.GetSection(OllamaOptions.SectionName))
                .ValidateOnStart();

            builder.Services.AddScoped<IPlanningWorkflow, PlanningWorkflow>();
            builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
            builder.Services.AddScoped<IPlanningJobProcessor, PlanningJobProcessor>();
            builder.Services.AddScoped<IDraftApprovalService, DraftApprovalService>();
            builder.Services.Configure<PlanningJobWorkerOptions>(options =>
            {
                options.PollInterval = TimeSpan.FromMilliseconds(10);
                options.LeaseDuration = TimeSpan.FromSeconds(3);
            });
            builder.Services.AddHostedService<PlanningJobWorker>();

            var app = builder.Build();
            app.MapPlanningJobEndpoints();
            await app.StartAsync();
            return new AT03IntegrationFixture(keeperConnection, app, app.GetTestClient());
        }

        public async Task<PlanningJobStatusResponse> WaitForSucceededAsync(Guid jobId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!timeout.IsCancellationRequested)
            {
                using var response = await Client.GetAsync($"/api/planning-jobs/{jobId}", timeout.Token);
                response.EnsureSuccessStatusCode();
                var status = (await response.Content.ReadFromJsonAsync<PlanningJobStatusResponse>(timeout.Token))!;

                if (status.Status == "Succeeded")
                    return status;
                if (status.Status == "Failed")
                    throw new InvalidOperationException($"Planning job failed: {status.FailureCode} - {status.ErrorMessage}");

                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }

            throw new TimeoutException("The planning job did not complete within timeout.");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            await keeperConnection.DisposeAsync();
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "docs", "requirementsforpoc.md")) ||
                    File.Exists(Path.Combine(directory.FullName, "requirementsforpoc.md")))
                    return directory.FullName;
            }

            throw new DirectoryNotFoundException(
                "Could not locate requirementsforpoc.md from the integration test output directory.");
        }
    }
}
