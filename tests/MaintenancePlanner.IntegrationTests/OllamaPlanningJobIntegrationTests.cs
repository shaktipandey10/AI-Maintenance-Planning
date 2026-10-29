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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class OllamaPlanningJobIntegrationTests
{
    [Fact]
    public async Task ApiSubmission_RunsOllamaWorkflowAndPersistsCandidatesGenerated()
    {
        var ollamaHandler = new OllamaHandler(
            """
            {"maintenanceIntent":"bearing replacement","scope":"P-101 bearing","scopeResolution":"Resolved","proposedAssetIds":["P-101"],"proposedTaskIds":["P101-BEARING-REPLACEMENT"],"missingInformation":[],"assumptions":[],"scopeMappings":[{"taskId":"P101-BEARING-REPLACEMENT","assetId":"P-101"}]}
            """);
        await using var fixture = await IntegrationFixture.CreateAsync(ollamaHandler);
        var status = await SubmitAndWaitAsync(fixture);

        Assert.Equal(1, ollamaHandler.CallCount);
        Assert.Contains("P101-BEARING-REPLACEMENT", ollamaHandler.RequestBody);
        Assert.Equal("Succeeded", status.Status);
        using var payload = JsonDocument.Parse(status.ResultPayload!);
        Assert.Equal("CandidatesGenerated", payload.RootElement.GetProperty("outcome").GetString());
        var candidates = payload.RootElement.GetProperty("result").GetProperty("candidates");
        Assert.NotEmpty(candidates.EnumerateArray());
        var candidate = candidates[0];
        Assert.Contains(
            "P101-BEARING-REPLACEMENT",
            candidate.GetProperty("requestedTaskIds").EnumerateArray()
                .Select(taskId => taskId.GetString()));
        Assert.Contains(
            "P101-PREP",
            candidate.GetProperty("dependencyTaskIds").EnumerateArray()
                .Select(taskId => taskId.GetString()));
        Assert.DoesNotContain(
            "P101-PREP",
            candidate.GetProperty("requestedTaskIds").EnumerateArray()
                .Select(taskId => taskId.GetString()));
        Assert.Null(status.FailureCode);
        Assert.Null(status.ErrorMessage);
    }

    [Fact]
    public async Task ApiSubmission_RunsOllamaWorkflowAndPersistsClarificationOutcomeForAmbiguousScope()
    {
        var ollamaHandler = new OllamaHandler(
            """
            {"maintenanceIntent":"bearing work","scope":"P-101 bearing","scopeResolution":"Ambiguous","proposedAssetIds":["P-101"],"proposedTaskIds":[],"missingInformation":["Should the bearing be inspected or replaced?"],"assumptions":[],"scopeMappings":[]}
            """);
        await using var fixture = await IntegrationFixture.CreateAsync(ollamaHandler);
        var status = await SubmitAndWaitAsync(fixture);

        Assert.Equal(1, ollamaHandler.CallCount);
        Assert.Equal("Succeeded", status.Status);
        using var payload = JsonDocument.Parse(status.ResultPayload!);
        Assert.Equal(
            "ClarificationRequired",
            payload.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "Should the bearing be inspected or replaced?",
            payload.RootElement.GetProperty("result")
                .GetProperty("questions")[0]
                .GetString());
        Assert.Null(status.FailureCode);
        Assert.Null(status.ErrorMessage);
    }

    private static async Task<PlanningJobStatusResponse> SubmitAndWaitAsync(IntegrationFixture fixture)
    {
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
        return await fixture.WaitForSucceededAsync(accepted.JobId);
    }

    private sealed class OllamaHandler(string output) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var content = JsonSerializer.Serialize(new
            {
                model = "qwen2.5:7b",
                created_at = "2026-10-23T00:00:00Z",
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

    private sealed class IntegrationFixture(
        SqliteConnection keeperConnection,
        WebApplication app,
        HttpClient client)
        : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<IntegrationFixture> CreateAsync(HttpMessageHandler ollamaHandler)
        {
            var connectionString =
                $"Data Source=PlanningOllama-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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
                        var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MaintenancePlanner.Infrastructure.Planning.OllamaOptions>>().Value;
                        client.BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/", UriKind.Absolute);
                        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                    })
                .ConfigurePrimaryHttpMessageHandler(() => ollamaHandler);
            builder.Services.AddOptions<MaintenancePlanner.Infrastructure.Planning.OllamaOptions>()
                .Bind(builder.Configuration.GetSection(MaintenancePlanner.Infrastructure.Planning.OllamaOptions.SectionName))
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
            return new IntegrationFixture(keeperConnection, app, app.GetTestClient());
        }

        public async Task<PlanningJobStatusResponse> WaitForSucceededAsync(Guid jobId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!timeout.IsCancellationRequested)
            {
                using var response = await Client.GetAsync(
                    $"/api/planning-jobs/{jobId}",
                    timeout.Token);
                response.EnsureSuccessStatusCode();
                var status = (await response.Content.ReadFromJsonAsync<PlanningJobStatusResponse>(
                    timeout.Token))!;
                if (status.Status == "Succeeded")
                    return status;

                if (status.Status == "Failed")
                    throw new InvalidOperationException(
                        $"Planning job failed: {status.FailureCode} - {status.ErrorMessage}");

                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }

            throw new TimeoutException("The Ollama-backed planning job did not complete.");
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
