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

/// <summary>AT04 integration tests: infeasibility with unavailable bearing and reduced window.</summary>
public sealed class AT04_InfeasibilityTests
{
    [Fact]
    public async Task AT04_UnavailableBearingAndReducedWindow_PersistedAsInfeasibleWithBothViolations()
    {
        // Arrange: Use deterministic handler (no actual interpretation needed for infeasibility path)
        var ollamaHandler = new DeterministicHandler(
            """
            {"maintenanceIntent":"bearing replacement","scope":"P-101 bearing","scopeResolution":"Resolved","proposedAssetIds":["P-101"],"proposedTaskIds":["P101-BEARING-REPLACEMENT"],"missingInformation":[],"assumptions":[],"scopeMappings":[{"taskId":"P101-BEARING-REPLACEMENT","assetId":"P-101"}]}
            """);
        await using var fixture = await AT04IntegrationFixture.CreateAsync(ollamaHandler);

        // Act: Submit planning job via API with reduced window (4 hours, unavailable bearing via scenario override)
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
        {
            Content = JsonContent.Create(new CreatePlanningJobRequest
            {
                RequestText = "Replace the P-101 bearing.",
                AssetIds = ["P-101"],
                WindowId = "MW-P101-REDUCED-20261024"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var acceptedResponse = await fixture.Client.SendAsync(request);
        acceptedResponse.EnsureSuccessStatusCode();
        var accepted = (await acceptedResponse.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>())!;
        var jobStatus = await fixture.WaitForSucceededAsync(accepted.JobId);

        // Assert: Result persisted as Infeasible with both violations
        Assert.NotNull(jobStatus.ResultPayload);
        using var payload = JsonDocument.Parse(jobStatus.ResultPayload);

        // Verify outcome is Infeasible
        Assert.Equal("Infeasible", payload.RootElement.GetProperty("outcome").GetString());

        // Verify both violations are in the persisted result
        var violations = payload.RootElement.GetProperty("result").GetProperty("violations");
        var violationsList = violations.EnumerateArray().ToList();

        var partUnavailable = violationsList
            .FirstOrDefault(v => v.GetProperty("code").GetString() == "PART_UNAVAILABLE");
        Assert.False(partUnavailable.ValueKind == JsonValueKind.Undefined,
            "PART_UNAVAILABLE violation not found in persisted result");
        Assert.Equal("BR-101", partUnavailable.GetProperty("resourceId").GetString());

        var tasksExceed = violationsList
            .FirstOrDefault(v => v.GetProperty("code").GetString() == "TASKS_EXCEED_WINDOW");
        Assert.False(tasksExceed.ValueKind == JsonValueKind.Undefined,
            "TASKS_EXCEED_WINDOW violation not found in persisted result");

        // Verify no candidates generated
        Assert.False(payload.RootElement.GetProperty("result").TryGetProperty("candidates", out var candidates),
            "Infeasible result should not contain candidates");
    }

    [Fact]
    public async Task AT04_Infeasibility_VisibleViaGetJobApi()
    {
        // Arrange
        var ollamaHandler = new DeterministicHandler(
            """
            {"maintenanceIntent":"bearing replacement","scope":"P-101 bearing","scopeResolution":"Resolved","proposedAssetIds":["P-101"],"proposedTaskIds":["P101-BEARING-REPLACEMENT"],"missingInformation":[],"assumptions":[],"scopeMappings":[{"taskId":"P101-BEARING-REPLACEMENT","assetId":"P-101"}]}
            """);
        await using var fixture = await AT04IntegrationFixture.CreateAsync(ollamaHandler);

        // Act: Submit job and get status via API
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
        {
            Content = JsonContent.Create(new CreatePlanningJobRequest
            {
                RequestText = "Replace the P-101 bearing.",
                AssetIds = ["P-101"],
                WindowId = "MW-P101-REDUCED-20261024"
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var acceptedResponse = await fixture.Client.SendAsync(request);
        var accepted = (await acceptedResponse.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>())!;

        // Wait and call GET endpoint
        var jobStatus = await fixture.WaitForSucceededAsync(accepted.JobId);

        // Assert: GET response contains infeasibility details
        using var statusResponse = await fixture.Client.GetAsync($"/api/planning-jobs/{accepted.JobId}");
        statusResponse.EnsureSuccessStatusCode();

        var apiStatus = (await statusResponse.Content.ReadFromJsonAsync<PlanningJobStatusResponse>())!;

        // Verify API response structure
        Assert.Equal("Succeeded", apiStatus.Status);
        Assert.NotNull(apiStatus.ResultPayload);

        using var apiPayload = JsonDocument.Parse(apiStatus.ResultPayload);
        Assert.Equal("Infeasible", apiPayload.RootElement.GetProperty("outcome").GetString());

        // Verify violations array contains both blockers
        var apiViolations = apiPayload.RootElement.GetProperty("result").GetProperty("violations");
        var apiViolationsList = apiViolations.EnumerateArray().ToList();

        var apiPartUnavailable = apiViolationsList
            .FirstOrDefault(v => v.GetProperty("code").GetString() == "PART_UNAVAILABLE");
        Assert.False(apiPartUnavailable.ValueKind == JsonValueKind.Undefined,
            "PART_UNAVAILABLE violation not visible via GET /api/planning-jobs/{id}");

        var apiTasksExceed = apiViolationsList
            .FirstOrDefault(v => v.GetProperty("code").GetString() == "TASKS_EXCEED_WINDOW");
        Assert.False(apiTasksExceed.ValueKind == JsonValueKind.Undefined,
            "TASKS_EXCEED_WINDOW violation not visible via GET /api/planning-jobs/{id}");
    }

    private sealed class DeterministicHandler(string output) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = System.Text.Json.JsonSerializer.Serialize(new
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

    private sealed class AT04IntegrationFixture(
        SqliteConnection keeperConnection,
        WebApplication app,
        HttpClient client)
        : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<AT04IntegrationFixture> CreateAsync(HttpMessageHandler ollamaHandler)
        {
            var connectionString = $"Data Source=AT04-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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
                new CompositeManualEvidenceRetriever(
                [
                    new TextManualEvidenceRetriever(Path.Combine(
                        manualsPath, "data", "manuals", "MAN-P101-001.txt")),
                    new PdfManualEvidenceRetriever(
                        Path.Combine(manualsPath, "data", "manuals", "MAN-P101-PLANNING-001.pdf"),
                        "MAN-P101-PLANNING-001",
                        "1.0")
                ]));

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
            return new AT04IntegrationFixture(keeperConnection, app, app.GetTestClient());
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
