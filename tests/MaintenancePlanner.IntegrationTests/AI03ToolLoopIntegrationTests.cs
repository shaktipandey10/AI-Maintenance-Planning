using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Api.Workers;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
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
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// AI03 stage 3: the Ollama tool-calling loop with the real planning tools and seeded SQLite data. Every model reply
/// is scripted by a fake HTTP handler (MOCKED MODEL); these tests do not show real Ollama or qwen2.5 behaviour.
/// </summary>
public sealed class AI03ToolLoopIntegrationTests(ITestOutputHelper output)
{
    private const string Baseline = "MW-P101-BASELINE-20261024";

    private const string FinalInterpretation =
        """{"maintenanceIntent":"bearing replacement","scope":"P-101 bearing","scopeResolution":"Resolved","proposedAssetIds":["P-101"],"proposedTaskIds":["P101-BEARING-REPLACEMENT"],"missingInformation":[],"assumptions":[],"scopeMappings":[{"taskId":"P101-BEARING-REPLACEMENT","assetId":"P-101"}]}""";

    private const string BaselineCandidate = """
        {"assetIds":["P-101"],"windowId":"MW-P101-BASELINE-20261024","placements":[
          {"taskId":"P101-PREP","start":"2026-10-24T08:00:00+05:30","end":"2026-10-24T09:00:00+05:30","technicianIds":["TECH-001"]},
          {"taskId":"P101-BEARING-REPLACEMENT","start":"2026-10-24T09:00:00+05:30","end":"2026-10-24T12:00:00+05:30","technicianIds":["TECH-001","TECH-002"]},
          {"taskId":"P101-VERIFICATION","start":"2026-10-24T12:00:00+05:30","end":"2026-10-24T14:00:00+05:30","technicianIds":["TECH-003"]}]}
        """;

    [Fact]
    public async Task Interpreter_WithTheFiveRealTools_CompletesAToolCycleOverSeededData_AndWritesNothing()
    {
        await using var fixture = await AI03ToolFixture.CreateAsync();
        var before = await fixture.SnapshotAsync();
        var handler = new ScriptedHandler(
            output,
            ToolCalls(
                ("get_task_catalogue", """{"assetId":"P-101"}"""),
                ("lookup_stock", $$"""{"assetId":"P-101","windowId":"{{Baseline}}"}"""),
                ("get_technician_availability", $$"""{"windowId":"{{Baseline}}"}""")),
            ToolCalls(
                ("validate_schedule", BaselineCandidate),
                ("calculate_cost", BaselineCandidate)),
            Content(""),
            Content(FinalInterpretation));
        var interpreter = new OllamaPlanningRequestInterpreter(
            new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test:11434/") },
            Options.Create(new OllamaOptions
            {
                BaseUrl = "http://ollama.test:11434", Model = "qwen2.5:7b", TimeoutSeconds = 60, EnableTools = true
            }),
            fixture.Dispatcher);

        var interpretation = await interpreter.InterpretAsync(
            new PlanningRequest("Replace the P-101 bearing.", ["P-101"], Baseline),
            await fixture.Queries.GetAssetsAsync(CancellationToken.None),
            (await fixture.Queries.GetTaskCatalogueAsync("P-101", CancellationToken.None))!,
            CancellationToken.None);

        Assert.Equal(["P101-BEARING-REPLACEMENT"], interpretation.ProposedTaskIds);
        Assert.Equal(4, handler.RequestBodies.Count);

        // The tool results the model received are the application's actual data
        var firstRound = ToolResults(handler.RequestBodies[1]);
        Assert.All(firstRound, result => Assert.True(result.GetProperty("ok").GetBoolean()));
        Assert.Equal(["P101-PREP", "P101-BEARING-REPLACEMENT", "P101-VERIFICATION"],
            firstRound[0].GetProperty("data").GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        Assert.Equal(1m, firstRound[1].GetProperty("data").GetProperty("parts")[0].GetProperty("availableQuantity").GetDecimal());
        Assert.Contains("not subtracted", firstRound[2].GetProperty("data").GetProperty("limitation").GetString());

        // The conversation repeats the first round's results, so the second round's follow them
        var secondRound = ToolResults(handler.RequestBodies[2]).Skip(firstRound.Length).ToArray();
        Assert.Equal(2, secondRound.Length);
        Assert.True(secondRound[0].GetProperty("data").GetProperty("isFeasible").GetBoolean());
        var metrics = secondRound[1].GetProperty("data").GetProperty("metrics");
        Assert.Equal(360.50m, metrics.GetProperty("totalLabourCostAmount").GetDecimal());
        Assert.Equal("UNKNOWN", metrics.GetProperty("totalLabourCostCurrency").GetString());

        var after = await fixture.SnapshotAsync();
        output.WriteLine($"SNAPSHOT before: {before}");
        output.WriteLine($"SNAPSHOT after:  {after}");
        Assert.Equal(before, after);
        Assert.False(fixture.DbContext.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task PlanningJob_ToolsEnabledThroughProductionRegistration_Succeeds_AndWritesNoProtectedRecords()
    {
        var handler = new ScriptedHandler(
            output,
            ToolCalls(
                ("get_task_catalogue", """{"assetId":"P-101"}"""),
                ("lookup_stock", $$"""{"assetId":"P-101","windowId":"{{Baseline}}"}""")),
            Content(""),
            Content(FinalInterpretation));
        await using var fixture = await JobFixture.CreateAsync(handler, enableTools: true);
        var before = await fixture.SnapshotAsync();

        var status = await fixture.SubmitAndWaitAsync();

        Assert.Equal("Succeeded", status.Status);
        using var payload = JsonDocument.Parse(status.ResultPayload!);
        Assert.Equal("CandidatesGenerated", payload.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(3, handler.RequestBodies.Count);
        Assert.Contains("\"tools\":", handler.RequestBodies[0]);
        Assert.DoesNotContain("\"format\":", handler.RequestBodies[0]);
        Assert.Contains("\"format\":\"json\"", handler.RequestBodies[2]);
        Assert.Contains("P101-BEARING-REPLACEMENT", ToolResults(handler.RequestBodies[1])[0].GetRawText());

        // Only the planning job itself is new: no draft, reservation, lock, edit, progress or business action
        var after = await fixture.SnapshotAsync();
        output.WriteLine($"SNAPSHOT before: {before}");
        output.WriteLine($"SNAPSHOT after:  {after}");
        Assert.Equal(before.Replace("jobs=0", "jobs=1"), after);
    }

    [Fact]
    public async Task PlanningJob_ModelUsingAnInventedWindow_IsRejectedByTheTool_AndThePlanUsesTheSelectedWindow()
    {
        // The invented window is the one qwen2.5:7b used in the real-model run (E-AI03-08)
        const string invented = "2023-10-15T08:00:00Z/2023-10-15T18:00:00Z";
        var handler = new ScriptedHandler(
            output,
            ToolCalls(("get_technician_availability", $$"""{"windowId":"{{invented}}"}""")),
            Content(""),
            Content(FinalInterpretation));
        await using var fixture = await JobFixture.CreateAsync(handler, enableTools: true);

        var status = await fixture.SubmitAndWaitAsync();

        using var firstRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        using var prompt = JsonDocument.Parse(firstRequest.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal(Baseline, prompt.RootElement.GetProperty("selectedWindowId").GetString());
        var toolResult = Assert.Single(ToolResults(handler.RequestBodies[1]));
        Assert.Equal("UNKNOWN_WINDOW", toolResult.GetProperty("error").GetProperty("code").GetString());

        Assert.Equal("Succeeded", status.Status);
        using var payload = JsonDocument.Parse(status.ResultPayload!);
        Assert.Equal("CandidatesGenerated", payload.RootElement.GetProperty("outcome").GetString());
        var windowStart = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5));
        var windowEnd = windowStart.AddHours(8);
        Assert.All(payload.RootElement.GetProperty("result").GetProperty("candidates").EnumerateArray(), candidate =>
            Assert.All(candidate.GetProperty("taskPlacements").EnumerateArray(), placement =>
            {
                Assert.InRange(placement.GetProperty("start").GetDateTimeOffset(), windowStart, windowEnd);
                Assert.InRange(placement.GetProperty("end").GetDateTimeOffset(), windowStart, windowEnd);
            }));
        output.WriteLine($"RESULT: invented window rejected with UNKNOWN_WINDOW; job {status.Status}; every placement within {Baseline}");
    }

    // A valid P-101 schedule that neither scheduler policy produces (it starts at 08:30)
    private const string SavedPlacements = """
        {"placements":[
          {"taskId":"P101-PREP","start":"2026-10-24T08:30:00+05:30","end":"2026-10-24T09:30:00+05:30","technicianIds":["TECH-001"]},
          {"taskId":"P101-BEARING-REPLACEMENT","start":"2026-10-24T09:30:00+05:30","end":"2026-10-24T12:30:00+05:30","technicianIds":["TECH-001","TECH-002"]},
          {"taskId":"P101-VERIFICATION","start":"2026-10-24T12:30:00+05:30","end":"2026-10-24T14:30:00+05:30","technicianIds":["TECH-003"]}]}
        """;

    [Fact]
    public async Task PlanningJob_SavedCandidate_IsAddedToTheUnapprovedJobResult_AndOnlyTheJobIsWritten()
    {
        var handler = new ScriptedHandler(
            output,
            ToolCalls(("save_candidate", SavedPlacements)),
            Content(""),
            Content(FinalInterpretation));
        await using var fixture = await JobFixture.CreateAsync(handler, enableTools: true);
        var before = await fixture.SnapshotAsync();

        var status = await fixture.SubmitAndWaitAsync();

        var toolResult = Assert.Single(ToolResults(handler.RequestBodies[1]));
        Assert.True(toolResult.GetProperty("ok").GetBoolean());
        Assert.Equal(1, toolResult.GetProperty("data").GetProperty("proposalNumber").GetInt32());

        Assert.Equal("Succeeded", status.Status);
        using var payload = JsonDocument.Parse(status.ResultPayload!);
        Assert.Equal("CandidatesGenerated", payload.RootElement.GetProperty("outcome").GetString());
        var result = payload.RootElement.GetProperty("result");
        var candidates = result.GetProperty("candidates").EnumerateArray().ToArray();
        var saved = candidates[^1];
        Assert.Equal("Proposed by the planning model with save_candidate; validated by the application's candidate validator, with labour cost from stored technician rates.",
            saved.GetProperty("rationale").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 8, 30, 0, TimeSpan.FromHours(5.5)),
            saved.GetProperty("taskPlacements")[0].GetProperty("start").GetDateTimeOffset());
        Assert.Equal(360.50m, saved.GetProperty("totalLabourCostAmount").GetDecimal());
        Assert.Contains("P101-BEARING-REPLACEMENT", saved.GetProperty("requestedTaskIds").EnumerateArray().Select(t => t.GetString()));
        var note = Assert.Single(result.GetProperty("modelCandidateNotes").EnumerateArray()).GetString();
        Assert.Equal($"Model-proposed candidate 1 was included as candidate {candidates.Length}.", note);

        var after = await fixture.SnapshotAsync();
        output.WriteLine($"SNAPSHOT before: {before}");
        output.WriteLine($"SNAPSHOT after:  {after}");
        output.WriteLine($"RESULT: {candidates.Length} unapproved candidates in the job result; note: {note}");
        Assert.Equal(before.Replace("jobs=0", "jobs=1"), after);
    }

    [Fact]
    public async Task PlanningJob_SavedCandidateNotCoveringTheFinalTaskScope_IsLeftOut_WithItsReason()
    {
        const string prepOnly = """
            {"placements":[{"taskId":"P101-PREP","start":"2026-10-24T08:30:00+05:30","end":"2026-10-24T09:30:00+05:30","technicianIds":["TECH-001"]}]}
            """;
        var handler = new ScriptedHandler(output, ToolCalls(("save_candidate", prepOnly)), Content(""), Content(FinalInterpretation));
        await using var fixture = await JobFixture.CreateAsync(handler, enableTools: true);

        var status = await fixture.SubmitAndWaitAsync();

        Assert.True(Assert.Single(ToolResults(handler.RequestBodies[1])).GetProperty("ok").GetBoolean());
        using var payload = JsonDocument.Parse(status.ResultPayload!);
        var result = payload.RootElement.GetProperty("result");
        Assert.All(result.GetProperty("candidates").EnumerateArray(),
            candidate => Assert.NotEqual(1, candidate.GetProperty("taskPlacements").GetArrayLength()));
        var note = Assert.Single(result.GetProperty("modelCandidateNotes").EnumerateArray()).GetString();
        Assert.StartsWith("Model-proposed candidate 1 was not included: it does not cover exactly the required tasks", note);
        output.WriteLine($"RESULT: {note}");
    }

    [Fact]
    public async Task SavedCandidate_IsApprovedOnlyByTheExplicitApprovalAction_WhichReservesLocksAndRecordsIt()
    {
        var handler = new ScriptedHandler(output, ToolCalls(("save_candidate", SavedPlacements)), Content(""), Content(FinalInterpretation));
        await using var fixture = await JobFixture.CreateAsync(handler, enableTools: true);
        var beforeJob = await fixture.SnapshotAsync();

        var status = await fixture.SubmitAndWaitAsync();
        using var payload = JsonDocument.Parse(status.ResultPayload!);
        var candidates = payload.RootElement.GetProperty("result").GetProperty("candidates").EnumerateArray().ToArray();
        var savedIndex = candidates.Length - 1;
        Assert.StartsWith("Proposed by the planning model with save_candidate", candidates[savedIndex].GetProperty("rationale").GetString());

        // Saving alone approved nothing: only the job row is new
        var afterSave = await fixture.SnapshotAsync();
        Assert.Equal(beforeJob.Replace("jobs=0", "jobs=1"), afterSave);

        // Approval is an explicit selection: without an index the existing endpoint refuses
        var unselected = await fixture.ApproveAsync(status.JobId, candidateIndex: null);
        Assert.Equal(HttpStatusCode.Conflict, unselected.Status);
        Assert.Equal("CANDIDATE_SELECTION_REQUIRED", unselected.Conflict!.Reason);
        Assert.Equal(afterSave, await fixture.SnapshotAsync());

        var approved = await fixture.ApproveAsync(status.JobId, savedIndex);
        Assert.Equal(HttpStatusCode.Accepted, approved.Status);
        var afterApproval = await fixture.SnapshotAsync();

        var state = await fixture.WithDatabaseAsync(async db => new
        {
            Drafts = await db.Drafts.AsNoTracking().ToListAsync(),
            Reservations = await db.Reservations.AsNoTracking().ToListAsync(),
            Locks = await db.LockedTaskAssignments.AsNoTracking().OrderBy(l => l.TaskId).ToListAsync(),
            Actions = await db.BusinessActionRecords.AsNoTracking().ToListAsync(),
            Bearing = await db.SpareParts.AsNoTracking().SingleAsync(p => p.Id == "BR-101")
        });
        var draft = Assert.Single(state.Drafts);
        Assert.Equal(DraftStatus.Approved, draft.Status);
        Assert.Equal(status.JobId, draft.PlanningJobId);
        Assert.Equal($"/api/drafts/{draft.Id}", approved.Location);
        using var snapshot = JsonDocument.Parse(draft.ApprovedCandidatePayload!);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 8, 30, 0, TimeSpan.FromHours(5.5)),
            snapshot.RootElement.GetProperty("taskPlacements")[0].GetProperty("start").GetDateTimeOffset());
        var reservation = Assert.Single(state.Reservations);
        Assert.Equal(("BR-101", 1m, ReservationStatus.Confirmed, draft.Id),
            (reservation.SparePartId, reservation.QuantityReserved, reservation.Status, reservation.DraftId));
        Assert.Equal(1m, state.Bearing.ReservedQuantity);
        Assert.Equal(
            [("P101-BEARING-REPLACEMENT", "TECH-001"), ("P101-PREP", "TECH-001"), ("P101-VERIFICATION", "TECH-003")],
            state.Locks.Select(l => (l.TaskId, l.LockedTechnicianId)));
        var action = Assert.Single(state.Actions);
        Assert.Equal(("Approved", $"Approved alternative {candidates.Length} of {candidates.Length}."), (action.Action, action.Details));

        // A repeated approval is idempotent under the existing rules: nothing more is written
        var repeated = await fixture.ApproveAsync(status.JobId, savedIndex);
        Assert.Equal(HttpStatusCode.Accepted, repeated.Status);
        Assert.Equal(afterApproval, await fixture.SnapshotAsync());

        output.WriteLine($"SNAPSHOT before job:      {beforeJob}");
        output.WriteLine($"SNAPSHOT after save:      {afterSave}");
        output.WriteLine($"SNAPSHOT after approval:  {afterApproval}");
        output.WriteLine($"RESULT: approve without index -> 409 {unselected.Conflict.Reason}; approve candidateIndex {savedIndex} (saved candidate) -> 202 {approved.Location}; " +
                         $"draft {draft.Status}, reservation {reservation.SparePartId} x{reservation.QuantityReserved} {reservation.Status}, {state.Locks.Count} locks, action '{action.Action}' by '{action.PerformedBy}'; repeat -> 202, no new rows");
    }

    [Theory]
    [InlineData("technician-availability-removed", "CANDIDATE_NO_LONGER_VALID", "TECHNICIAN_UNAVAILABLE")]
    [InlineData("bearing-stock-removed", "INSUFFICIENT_INVENTORY", "BR-101")]
    public async Task SavedCandidate_ThatBecameStaleAfterSaving_IsRejectedAtApproval_AndNothingIsWritten(
        string change, string expectedReason, string expectedDetail)
    {
        var handler = new ScriptedHandler(output, ToolCalls(("save_candidate", SavedPlacements)), Content(""), Content(FinalInterpretation));
        await using var fixture = await JobFixture.CreateAsync(handler, enableTools: true);
        var status = await fixture.SubmitAndWaitAsync();
        using var payload = JsonDocument.Parse(status.ResultPayload!);
        var savedIndex = payload.RootElement.GetProperty("result").GetProperty("candidates").GetArrayLength() - 1;

        // The saved candidate was valid when saved; the data it depends on changes before the reviewer approves it
        await fixture.WithDatabaseAsync(async db => change switch
        {
            "technician-availability-removed" => await db.TechnicianAvailabilities.Where(a => a.TechnicianId == "TECH-003").ExecuteDeleteAsync(),
            _ => await db.SpareParts.Where(p => p.Id == "BR-101").ExecuteUpdateAsync(s => s.SetProperty(p => p.OnHandQuantity, 0m))
        });
        var beforeApproval = await fixture.SnapshotAsync();

        var approval = await fixture.ApproveAsync(status.JobId, savedIndex);

        Assert.Equal(HttpStatusCode.Conflict, approval.Status);
        Assert.Equal(expectedReason, approval.Conflict!.Reason);
        Assert.Contains(expectedDetail, approval.Conflict.Message + " " + approval.Conflict.ResourceId);
        var afterApproval = await fixture.SnapshotAsync();
        Assert.Equal(beforeApproval, afterApproval);
        output.WriteLine($"SNAPSHOT before approval: {beforeApproval}");
        output.WriteLine($"SNAPSHOT after approval:  {afterApproval}");
        output.WriteLine($"RESULT: {change}: approve candidateIndex {savedIndex} (saved candidate) -> 409 {approval.Conflict.Reason} ({approval.Conflict.ResourceId}): {approval.Conflict.Message}");
    }

    [Fact]
    public async Task PlanningJob_ToolsDisabled_SendsTheSingleUnchangedRequest_EvenWithToolsRegistered()
    {
        var handler = new ScriptedHandler(output, Content(FinalInterpretation));
        await using var fixture = await JobFixture.CreateAsync(handler, enableTools: false);

        var status = await fixture.SubmitAndWaitAsync();

        Assert.Equal("Succeeded", status.Status);
        var body = Assert.Single(handler.RequestBodies);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["model", "stream", "format", "messages"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("json", json.RootElement.GetProperty("format").GetString());
    }

    private static JsonElement[] ToolResults(string requestBody) =>
        JsonDocument.Parse(requestBody).RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "tool")
            .Select(m => JsonDocument.Parse(m.GetProperty("content").GetString()!).RootElement)
            .ToArray();

    private static string Content(string content) =>
        JsonSerializer.Serialize(new { model = "qwen2.5:7b", message = new { role = "assistant", content }, done = true });

    private static string ToolCalls(params (string Name, string ArgumentsJson)[] calls) =>
        new JsonObject
        {
            ["model"] = "qwen2.5:7b",
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = new JsonArray(calls.Select((call, index) => (JsonNode)new JsonObject
                {
                    ["function"] = new JsonObject
                    {
                        ["index"] = index,
                        ["name"] = call.Name,
                        ["arguments"] = JsonNode.Parse(call.ArgumentsJson)
                    }
                }).ToArray())
            },
            ["done"] = true
        }.ToJsonString();

    private sealed class ScriptedHandler(ITestOutputHelper output, params string[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            output.WriteLine($"--- MOCKED MODEL request {RequestBodies.Count} ---");
            output.WriteLine(body);
            if (_next >= responses.Length)
                throw new InvalidOperationException("The scripted model has no further responses.");

            var response = responses[_next++];
            output.WriteLine($"--- MOCKED MODEL response {_next} ---");
            output.WriteLine(response);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>
    /// A planning API host whose Ollama interpreter and planning tools are registered with the production extension
    /// methods (<c>AddOllamaPlanningInterpreter</c>, <c>AddPlanningTools</c>); only the HTTP handler is the fake model.
    /// </summary>
    private sealed class JobFixture(SqliteConnection keeperConnection, string connectionString, WebApplication app, HttpClient client)
        : IAsyncDisposable
    {
        public static async Task<JobFixture> CreateAsync(HttpMessageHandler ollamaHandler, bool enableTools)
        {
            var connectionString = $"Data Source=AI03ToolLoop-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();
            await using (var dbContext = new MaintenancePlannerDbContext(
                             new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(connectionString).Options))
                await DatabaseInitializer.InitializeAsync(dbContext);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Configuration["Ollama:BaseUrl"] = "http://ollama.test:11434";
            builder.Configuration["Ollama:Model"] = "qwen2.5:7b";
            builder.Configuration["Ollama:TimeoutSeconds"] = "60";
            builder.Configuration["Ollama:EnableTools"] = enableTools ? "true" : "false";
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(options => options.UseSqlite(connectionString));
            builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            builder.Services.AddScoped<ISparePartReadModelProvider, ScenarioAwareSparePartReadModelProvider>();
            builder.Services.AddScoped<PlanningCandidateValidator>();
            builder.Services.AddScoped<CandidateCostCalculator>();
            builder.Services.AddScoped<DeterministicPlanningScheduler>();
            builder.Services.AddPlanningTools();
            var root = FindRepositoryRoot();
            builder.Services.AddSingleton<IManualEvidenceRetriever>(new CompositeManualEvidenceRetriever(
            [
                new TextManualEvidenceRetriever(Path.Combine(root, "data", "manuals", "MAN-P101-001.txt")),
                new PdfManualEvidenceRetriever(
                    Path.Combine(root, "data", "manuals", "MAN-P101-PLANNING-001.pdf"), "MAN-P101-PLANNING-001", "1.0")
            ]));
            builder.Services.AddOllamaPlanningInterpreter(builder.Configuration);
            builder.Services.AddHttpClient<IPlanningRequestInterpreter, OllamaPlanningRequestInterpreter>()
                .ConfigurePrimaryHttpMessageHandler(() => ollamaHandler);
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
            app.MapApprovalEndpoints();
            await app.StartAsync();
            return new JobFixture(keeperConnection, connectionString, app, app.GetTestClient());
        }

        /// <summary>The existing reviewer approval endpoint; a null index sends an empty body, as the API allows.</summary>
        public async Task<(HttpStatusCode Status, string? Location, ApprovalErrorResponse? Conflict)> ApproveAsync(Guid jobId, int? candidateIndex)
        {
            using var response = candidateIndex is null
                ? await client.PostAsync($"/api/drafts/{jobId}/approve", new StringContent("", Encoding.UTF8, "application/json"))
                : await client.PostAsJsonAsync($"/api/drafts/{jobId}/approve", new { candidateIndex });
            var conflict = response.StatusCode == HttpStatusCode.Conflict
                ? await response.Content.ReadFromJsonAsync<ApprovalErrorResponse>()
                : null;
            return (response.StatusCode, response.Headers.Location?.ToString(), conflict);
        }

        /// <summary>Direct access to this fixture's isolated in-memory database, to arrange or inspect a scenario.</summary>
        public async Task<T> WithDatabaseAsync<T>(Func<MaintenancePlannerDbContext, Task<T>> action)
        {
            await using var db = new MaintenancePlannerDbContext(
                new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(connectionString).Options);
            return await action(db);
        }

        public async Task<PlanningJobStatusResponse> SubmitAndWaitAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
            {
                Content = JsonContent.Create(new CreatePlanningJobRequest
                {
                    RequestText = "Replace the P-101 bearing.",
                    AssetIds = ["P-101"],
                    WindowId = Baseline
                })
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var accepted = await client.SendAsync(request);
            accepted.EnsureSuccessStatusCode();
            var jobId = (await accepted.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>())!.JobId;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var status = (await client.GetFromJsonAsync<PlanningJobStatusResponse>($"/api/planning-jobs/{jobId}", timeout.Token))!;
                if (status.Status == "Succeeded")
                    return status;
                if (status.Status == "Failed")
                    throw new InvalidOperationException($"Planning job failed: {status.FailureCode} - {status.ErrorMessage}");

                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
        }

        public async Task<string> SnapshotAsync()
        {
            await using var db = new MaintenancePlannerDbContext(
                new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(connectionString).Options);
            var stock = await db.SpareParts.AsNoTracking().OrderBy(p => p.Id)
                .Select(p => $"{p.Id}:{p.OnHandQuantity}/{p.ReservedQuantity}").ToListAsync();
            return string.Join(";",
                $"jobs={await db.PlanningJobs.CountAsync()}",
                $"drafts={await db.Drafts.CountAsync()}",
                $"reservations={await db.Reservations.CountAsync()}",
                $"locks={await db.LockedTaskAssignments.CountAsync()}",
                $"edits={await db.EditedTaskPlacements.CountAsync()}",
                $"progress={await db.DraftTaskProgress.CountAsync()}",
                $"actions={await db.BusinessActionRecords.CountAsync()}",
                $"stock={string.Join(",", stock)}");
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            await keeperConnection.DisposeAsync();
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "docs", "requirementsforpoc.md")))
                    return directory.FullName;
            }

            throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
        }
    }
}
