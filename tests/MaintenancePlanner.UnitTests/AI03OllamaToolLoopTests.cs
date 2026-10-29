using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Planning.Tools;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Infrastructure.Planning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace MaintenancePlanner.UnitTests;

/// <summary>
/// AI03 stage 3: the bounded Ollama tool-calling loop. Every model reply here is scripted by a fake HTTP handler
/// (MOCKED MODEL); these tests show the application's side of the protocol, not real-model behaviour.
/// </summary>
public sealed class AI03OllamaToolLoopTests(ITestOutputHelper output)
{
    private static readonly PlanningRequest Request = new("Replace the bearing on Pump A-1.", ["A-1"], "WINDOW-1");
    private static readonly AssetReadModel[] Assets = [new("A-1", "PLANT-1", "Pump A-1", "CP-100", 1)];
    private static readonly MaintenanceTaskReadModel[] Tasks =
        [new("A1-BEARING", "A-1", "Bearing replacement", 60, "MECHANICAL", 1, null, 0, null, "MAN-1", "Bearing")];

    private const string ValidOutput =
        """{"maintenanceIntent":"bearing replacement","scope":"Pump A-1 bearing","scopeResolution":"Resolved","proposedAssetIds":["A-1"],"proposedTaskIds":["A1-BEARING"],"missingInformation":[],"assumptions":[],"scopeMappings":[{"taskId":"A1-BEARING","assetId":"A-1"}]}""";

    [Fact]
    public async Task FlagOff_WithToolsRegistered_SendsTheSingleUnchangedJsonModeRequest()
    {
        var tool = new FakeTool("lookup_fake", _ => PlanningToolResult.Success(new { value = 1 }));
        var handler = new ScriptedHandler(output, Content(ValidOutput));
        var interpreter = CreateInterpreter(handler, enableTools: false, dispatcher: new PlanningToolDispatcher([tool]));

        var result = await interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None);

        Assert.Equal(["A1-BEARING"], result.ProposedTaskIds);
        var body = Assert.Single(handler.RequestBodies);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["model", "stream", "format", "messages"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("json", json.RootElement.GetProperty("format").GetString());
        var messages = json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["system", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.All(messages, m => Assert.Equal(["role", "content"], m.EnumerateObject().Select(p => p.Name)));
        Assert.DoesNotContain("provided tools", messages[0].GetProperty("content").GetString());
        Assert.Equal(0, tool.Calls);
        // The window is only added for tool calls: without tools the prompt is the pre-AI03 prompt
        using var userContent = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal(["requestText", "selectedAssetIds", "assets", "taskCatalogue"],
            userContent.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task FlagOn_TheModelIsGivenTheApplicationSelectedWindowId_AndToldToUseIt()
    {
        var handler = new ScriptedHandler(output, Content(ValidOutput), Content(ValidOutput));
        var interpreter = CreateInterpreter(handler, enableTools: true, dispatcher: new PlanningToolDispatcher(
            [new FakeTool("lookup_fake", _ => PlanningToolResult.Success(new { }))]));

        await interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None);

        foreach (var body in handler.RequestBodies)
        {
            using var json = JsonDocument.Parse(body);
            var messages = json.RootElement.GetProperty("messages");
            using var userContent = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            Assert.Equal(Request.MaintenanceWindowId, userContent.RootElement.GetProperty("selectedWindowId").GetString());
            Assert.Contains("selectedWindowId is the maintenance window the application selected", messages[0].GetProperty("content").GetString());
        }
    }

    [Fact]
    public async Task FlagOn_AFinalAnswerCarryingItsOwnWindow_IsRejected_SoTheModelCannotReplaceTheSelectedWindow()
    {
        var withWindow = ValidOutput.Replace("\"assumptions\":[]", "\"assumptions\":[],\"windowId\":\"2023-10-15T08:00:00Z/2023-10-15T18:00:00Z\"");
        var handler = new ScriptedHandler(output, Content(""), Content(withWindow));
        var interpreter = CreateInterpreter(handler, enableTools: true, dispatcher: new PlanningToolDispatcher(
            [new FakeTool("lookup_fake", _ => PlanningToolResult.Success(new { }))]));

        var exception = await Assert.ThrowsAsync<OllamaPlanningInterpreterException>(
            () => interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None));

        Assert.Equal("OLLAMA_INVALID_OUTPUT", exception.FailureCode);
        // The interpretation contract has no window at all: scheduling always uses the request's window
        Assert.DoesNotContain(typeof(PlanningInterpretation).GetProperties(), p => p.Name.Contains("Window", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FlagOn_MultipleToolTurns_ThenAFinalJsonModeRequest_ReturnTheValidatedInterpretation()
    {
        var lookup = new FakeTool("lookup_fake", args => PlanningToolResult.Success(new { echoed = args.GetRawText() }));
        var other = new FakeTool("other_fake", _ => PlanningToolResult.Success(new { stock = 3 }));
        var handler = new ScriptedHandler(
            output,
            ToolCalls(("lookup_fake", """{"assetId":"A-1"}""")),
            ToolCalls(("other_fake", "{}"), ("lookup_fake", """{"assetId":"A-1","windowId":"WINDOW-1"}""")),
            Content("I have what I need."),
            Content(ValidOutput));
        var interpreter = CreateInterpreter(handler, enableTools: true, dispatcher: new PlanningToolDispatcher([lookup, other]));

        var result = await interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None);

        Assert.Equal(PlanningScopeResolution.Resolved, result.ScopeResolution);
        Assert.Equal(["A1-BEARING"], result.ProposedTaskIds);
        Assert.Equal(2, lookup.Calls);
        Assert.Equal(1, other.Calls);
        Assert.Equal(4, handler.RequestBodies.Count);

        // Tool rounds offer the tools without format; the final request asks for JSON without tools
        foreach (var body in handler.RequestBodies.Take(3))
        {
            using var round = JsonDocument.Parse(body);
            Assert.False(round.RootElement.TryGetProperty("format", out _));
            Assert.Equal(["lookup_fake", "other_fake"], round.RootElement.GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("function").GetProperty("name").GetString()));
            Assert.All(round.RootElement.GetProperty("tools").EnumerateArray(),
                t => Assert.Equal("function", t.GetProperty("type").GetString()));
        }

        using var final = JsonDocument.Parse(handler.RequestBodies[3]);
        Assert.Equal("json", final.RootElement.GetProperty("format").GetString());
        Assert.False(final.RootElement.TryGetProperty("tools", out _));
        var messages = final.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(
            ["system", "user", "assistant", "tool", "assistant", "tool", "tool", "assistant", "user"],
            messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("lookup_fake", messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("lookup_fake", messages[3].GetProperty("tool_name").GetString());
        var firstResult = JsonDocument.Parse(messages[3].GetProperty("content").GetString()!).RootElement;
        Assert.True(firstResult.GetProperty("ok").GetBoolean());
        Assert.Contains("A-1", firstResult.GetProperty("data").GetProperty("echoed").GetString());
        Assert.Equal("other_fake", messages[5].GetProperty("tool_name").GetString());
    }

    [Fact]
    public async Task FlagOn_ModelAnsweringWithoutTools_StillFinishesWithAJsonModeRequest()
    {
        var handler = new ScriptedHandler(output, Content(ValidOutput), Content(ValidOutput));
        var interpreter = CreateInterpreter(handler, enableTools: true, dispatcher: new PlanningToolDispatcher(
            [new FakeTool("lookup_fake", _ => PlanningToolResult.Success(new { }))]));

        var result = await interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None);

        Assert.Equal(["A1-BEARING"], result.ProposedTaskIds);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("\"format\":\"json\"", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task FlagOn_UnknownToolAndMalformedArguments_AreReturnedToTheModelAsErrors_AndNothingRuns()
    {
        var lookup = new FakeTool("lookup_fake", _ => PlanningToolResult.Success(new { }));
        var handler = new ScriptedHandler(
            output,
            ToolCalls(("approve_draft", """{"planningJobId":"00000000-0000-0000-0000-000000000001"}"""),
                      ("lookup_fake", "[1,2]"),
                      ("lookup_fake", "\"{not json\"")),
            Content(""),
            Content(ValidOutput));
        var interpreter = CreateInterpreter(handler, enableTools: true, dispatcher: new PlanningToolDispatcher([lookup]));

        await interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None);

        Assert.Equal(0, lookup.Calls);
        var results = ToolResults(handler.RequestBodies[1]);
        Assert.Equal([PlanningToolErrors.UnknownTool, PlanningToolErrors.InvalidArguments, PlanningToolErrors.InvalidArguments],
            results.Select(r => r.GetProperty("error").GetProperty("code").GetString()));
        Assert.All(results, r => Assert.False(r.GetProperty("ok").GetBoolean()));
    }

    [Fact]
    public async Task FlagOn_StringEncodedArguments_AreParsedLikeObjectArguments()
    {
        JsonElement? received = null;
        var lookup = new FakeTool("lookup_fake", args =>
        {
            received = args;
            return PlanningToolResult.Success(new { });
        });
        var handler = new ScriptedHandler(
            output,
            ToolCalls(("lookup_fake", JsonSerializer.Serialize("""{"assetId":"A-1"}"""))),
            Content(""),
            Content(ValidOutput));
        var interpreter = CreateInterpreter(handler, enableTools: true, dispatcher: new PlanningToolDispatcher([lookup]));

        await interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None);

        Assert.Equal("A-1", received!.Value.GetProperty("assetId").GetString());
    }

    [Fact]
    public async Task FlagOn_MalformedOrOversizedToolResults_ReachTheModelOnlyAsInvalidToolResult()
    {
        var malformed = new FakeTool("malformed_fake", _ => new PlanningToolResult(true, null, null, null));
        var oversized = new FakeTool("oversized_fake", _ =>
            PlanningToolResult.Success(new { text = new string('x', OllamaPlanningRequestInterpreter.MaximumToolResultLength) }));
        var handler = new ScriptedHandler(
            output,
            ToolCalls(("malformed_fake", "{}"), ("oversized_fake", "{}")),
            Content(""),
            Content(ValidOutput));
        var interpreter = CreateInterpreter(handler, enableTools: true, dispatcher: new PlanningToolDispatcher([malformed, oversized]));

        await interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None);

        var results = ToolResults(handler.RequestBodies[1]);
        Assert.Equal([PlanningToolErrors.InvalidToolResult, PlanningToolErrors.InvalidToolResult],
            results.Select(r => r.GetProperty("error").GetProperty("code").GetString()));
        Assert.True(handler.RequestBodies[1].Length < OllamaPlanningRequestInterpreter.MaximumToolResultLength);
    }

    [Fact]
    public async Task FlagOn_ToolException_IsReportedToTheModelWithoutItsMessage()
    {
        var failing = new FakeTool("failing_fake", _ => throw new InvalidOperationException("Server=db;Password=secret"));
        var handler = new ScriptedHandler(output, ToolCalls(("failing_fake", "{}")), Content(""), Content(ValidOutput));
        var interpreter = CreateInterpreter(handler, enableTools: true, dispatcher: new PlanningToolDispatcher([failing]));

        await interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None);

        Assert.Equal(PlanningToolErrors.ToolExecutionFailed,
            Assert.Single(ToolResults(handler.RequestBodies[1])).GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("secret", string.Join("\n", handler.RequestBodies));
    }

    [Fact]
    public async Task FlagOn_CallLimit_FailsExplicitly_BeforeRunningTheRoundThatWouldExceedIt()
    {
        var lookup = new FakeTool("lookup_fake", _ => PlanningToolResult.Success(new { }));
        var handler = new ScriptedHandler(
            output,
            ToolCalls(("lookup_fake", "{}"), ("lookup_fake", "{}")),
            ToolCalls(("lookup_fake", "{}")));
        var interpreter = CreateInterpreter(handler, enableTools: true, maxToolCalls: 2, dispatcher: new PlanningToolDispatcher([lookup]));

        var exception = await Assert.ThrowsAsync<OllamaPlanningInterpreterException>(
            () => interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None));

        output.WriteLine($"RESULT: {exception.FailureCode} (transient: {exception.IsTransient}): {exception.Message}");
        Assert.Equal("OLLAMA_TOOL_CALL_LIMIT", exception.FailureCode);
        Assert.False(exception.IsTransient);
        Assert.Equal(2, lookup.Calls);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task FlagOn_CancellationDuringATool_Propagates_AndNoFurtherRequestIsSent()
    {
        using var cancellation = new CancellationTokenSource();
        var lookup = new FakeTool("lookup_fake", _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return PlanningToolResult.Success(new { });
        });
        var handler = new ScriptedHandler(output, ToolCalls(("lookup_fake", "{}")), Content(ValidOutput));
        var interpreter = CreateInterpreter(handler, enableTools: true, dispatcher: new PlanningToolDispatcher([lookup]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => interpreter.InterpretAsync(Request, Assets, Tasks, cancellation.Token));

        output.WriteLine("RESULT: OperationCanceledException propagated; requests sent: " + handler.RequestBodies.Count);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task FlagOn_WithoutRegisteredTools_FailsClearly_WithoutContactingTheModel()
    {
        var handler = new ScriptedHandler(output);
        var interpreter = CreateInterpreter(handler, enableTools: true, dispatcher: null);

        var exception = await Assert.ThrowsAsync<OllamaPlanningInterpreterException>(
            () => interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None));

        Assert.Equal("OLLAMA_TOOLS_UNAVAILABLE", exception.FailureCode);
        Assert.False(exception.IsTransient);
        Assert.Empty(handler.RequestBodies);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"maintenanceIntent":"replace","scope":"bearing"}""")]
    [InlineData("")]
    public async Task FlagOn_InvalidFinalAnswer_IsStillRejectedByTheSchemaCheck(string finalOutput)
    {
        var handler = new ScriptedHandler(output, ToolCalls(("lookup_fake", "{}")), Content(""), Content(finalOutput));
        var interpreter = CreateInterpreter(handler, enableTools: true, dispatcher: new PlanningToolDispatcher(
            [new FakeTool("lookup_fake", _ => PlanningToolResult.Success(new { }))]));

        var exception = await Assert.ThrowsAsync<OllamaPlanningInterpreterException>(
            () => interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None));

        Assert.Equal("OLLAMA_INVALID_OUTPUT", exception.FailureCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("21")]
    public void Options_MaxToolCallsOutOfRange_FailsValidation(string maxToolCalls)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ollama:BaseUrl"] = "http://localhost:11434",
            ["Ollama:Model"] = "qwen2.5:7b",
            ["Ollama:TimeoutSeconds"] = "60",
            ["Ollama:EnableTools"] = "true",
            ["Ollama:MaxToolCalls"] = maxToolCalls
        }).Build();
        var services = new ServiceCollection();
        services.AddOllamaPlanningInterpreter(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<OllamaOptions>>().Value);
    }

    [Fact]
    public void Options_ToolsAreOffByDefault()
    {
        var options = new OllamaOptions();

        Assert.False(options.EnableTools);
        Assert.Equal(6, options.MaxToolCalls);
    }

    private static OllamaPlanningRequestInterpreter CreateInterpreter(
        ScriptedHandler handler,
        bool enableTools,
        PlanningToolDispatcher? dispatcher,
        int maxToolCalls = 6) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/"), Timeout = TimeSpan.FromSeconds(60) },
            Options.Create(new OllamaOptions
            {
                BaseUrl = "http://localhost:11434",
                Model = "qwen2.5:7b",
                TimeoutSeconds = 60,
                EnableTools = enableTools,
                MaxToolCalls = maxToolCalls
            }),
            dispatcher);

    /// <summary>The parsed contents of the tool-result messages in a request body.</summary>
    private static JsonElement[] ToolResults(string requestBody) =>
        JsonDocument.Parse(requestBody).RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "tool")
            .Select(m => JsonDocument.Parse(m.GetProperty("content").GetString()!).RootElement)
            .ToArray();

    private static string Content(string content) =>
        JsonSerializer.Serialize(new { model = "qwen2.5:7b", message = new { role = "assistant", content }, done = true });

    // Ollama's documented shape: message.tool_calls[].function { index, name, arguments as a JSON object }
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

    /// <summary>Fake Ollama: replies with the scripted responses in order and logs each exchange to the test output.</summary>
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

    private sealed class FakeTool(string name, Func<JsonElement, PlanningToolResult> execute) : IPlanningTool
    {
        public int Calls { get; private set; }
        public string Name => name;
        public string Description => $"Fake tool {name}";
        public JsonElement ParametersSchema { get; } =
            JsonDocument.Parse("""{"type":"object","additionalProperties":false}""").RootElement.Clone();

        public Task<PlanningToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(execute(arguments));
        }
    }
}
