using System.Net;
using System.Text;
using System.Text.Json;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Infrastructure.Planning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MaintenancePlanner.UnitTests;

public sealed class OllamaPlanningRequestInterpreterTests
{
    private static readonly PlanningRequest Request = new(
        "Replace the bearing on Pump A-1.",
        ["A-1"],
        "WINDOW-1");

    private static readonly AssetReadModel[] Assets =
    [
        new("A-1", "PLANT-1", "Pump A-1", "CP-100", 1)
    ];

    private static readonly MaintenanceTaskReadModel[] Tasks =
    [
        new("A1-BEARING", "A-1", "Bearing replacement", 60, "MECHANICAL", 1, null, 0, null, "MAN-1", "Bearing")
    ];

    [Fact]
    public async Task InterpretAsync_ParsesValidJsonAndUsesAuthoritativeTaskIdentifiers()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Response(ValidOutput())));
        var interpreter = CreateInterpreter(handler);

        var result = await interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None);

        Assert.Equal(PlanningScopeResolution.Resolved, result.ScopeResolution);
        Assert.Equal(["A1-BEARING"], result.ProposedTaskIds);
        Assert.Equal("A-1", result.ScopeMappings.Single().AssetId);
        Assert.Null(result.ScopeMappings.Single().RequestExcerpt);
        Assert.Contains("\"model\":\"qwen2.5:7b\"", handler.RequestBody);
        Assert.Contains("\"format\":\"json\"", handler.RequestBody);
        Assert.Equal("api/chat", handler.RequestUri!.AbsolutePath.TrimStart('/'));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"maintenanceIntent":"replace","scope":"bearing"}""")]
    [InlineData("""{"maintenanceIntent":"replace","scope":"bearing","scopeResolution":"Resolved","proposedAssetIds":["A-1"],"proposedTaskIds":["A1-BEARING","A1-BEARING"],"missingInformation":[],"assumptions":[],"scopeMappings":[]}""")]
    [InlineData("""{"maintenanceIntent":"replace","scope":"bearing","scopeResolution":"Resolved","proposedAssetIds":["A-1"],"proposedTaskIds":[],"missingInformation":[],"assumptions":[],"scopeMappings":[]}""")]
    [InlineData("""{"maintenanceIntent":"replace","scope":"bearing","scopeResolution":"Resolved","proposedAssetIds":["A-1"],"proposedTaskIds":["A1-BEARING"],"missingInformation":[],"assumptions":[],"scopeMappings":[]}""")]
    public async Task InterpretAsync_RejectsMalformedOrStructurallyInvalidOutput(string output)
    {
        var interpreter = CreateInterpreter(new StubHandler((_, _) => Task.FromResult(Response(output))));

        var exception = await Assert.ThrowsAsync<OllamaPlanningInterpreterException>(
            () => interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None));

        Assert.Equal("OLLAMA_INVALID_OUTPUT", exception.FailureCode);
    }

    [Fact]
    public async Task InterpretAsync_PreservesUnknownTaskForApplicationBusinessValidation()
    {
        const string output =
            """{"maintenanceIntent":"replacement","scope":"pump bearing","scopeResolution":"Resolved","proposedAssetIds":["A-1"],"proposedTaskIds":["UNKNOWN"],"missingInformation":[],"assumptions":[],"scopeMappings":[{"taskId":"UNKNOWN","assetId":"A-1"}]}""";
        var interpreter = CreateInterpreter(new StubHandler((_, _) => Task.FromResult(Response(output))));

        var result = await interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None);

        Assert.Equal(["UNKNOWN"], result.ProposedTaskIds);
        Assert.Equal("UNKNOWN", result.ScopeMappings.Single().TaskId);
    }

    [Fact]
    public async Task InterpretAsync_PreservesAmbiguousScopeAndClarificationQuestions()
    {
        const string output =
            """{"maintenanceIntent":"bearing maintenance","scope":"Pump A-1","scopeResolution":"Ambiguous","proposedAssetIds":["A-1"],"proposedTaskIds":[],"missingInformation":["Should the bearing be inspected or replaced?"],"assumptions":[],"scopeMappings":[]}""";
        var interpreter = CreateInterpreter(new StubHandler((_, _) => Task.FromResult(Response(output))));

        var result = await interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None);

        Assert.Equal(PlanningScopeResolution.Ambiguous, result.ScopeResolution);
        Assert.Equal(["Should the bearing be inspected or replaced?"], result.MissingInformation);
        Assert.Empty(result.ProposedTaskIds);
    }

    [Fact]
    public async Task InterpretAsync_MalformedOllamaEnvelopeReturnsInvalidOutput()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json")
        }));
        var interpreter = CreateInterpreter(handler);

        var exception = await Assert.ThrowsAsync<OllamaPlanningInterpreterException>(
            () => interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None));

        Assert.Equal("OLLAMA_INVALID_OUTPUT", exception.FailureCode);
    }

    [Fact]
    public async Task InterpretAsync_HttpFailureReturnsTechnicalProviderError()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var interpreter = CreateInterpreter(handler);

        var exception = await Assert.ThrowsAsync<OllamaPlanningInterpreterException>(
            () => interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None));

        Assert.Equal("OLLAMA_HTTP_ERROR", exception.FailureCode);
        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task InterpretAsync_UnreachableProviderReturnsTechnicalProviderError()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));
        var interpreter = CreateInterpreter(handler);

        var exception = await Assert.ThrowsAsync<OllamaPlanningInterpreterException>(
            () => interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None));

        Assert.Equal("OLLAMA_UNAVAILABLE", exception.FailureCode);
        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task InterpretAsync_TimeoutReturnsTechnicalProviderError()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout")));
        var interpreter = CreateInterpreter(handler);

        var exception = await Assert.ThrowsAsync<OllamaPlanningInterpreterException>(
            () => interpreter.InterpretAsync(Request, Assets, Tasks, CancellationToken.None));

        Assert.Equal("OLLAMA_TIMEOUT", exception.FailureCode);
        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task InterpretAsync_CallerCancellationIsNotConvertedToProviderFailure()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response(ValidOutput());
        });
        var interpreter = CreateInterpreter(handler);
        using var cancellation = new CancellationTokenSource();
        var operation = interpreter.InterpretAsync(
            Request,
            Assets,
            Tasks,
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation);
    }

    [Fact]
    public void AddOllamaPlanningInterpreter_MissingConfigurationFailsValidation()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddOllamaPlanningInterpreter(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<OllamaOptions>>().Value);
    }

    private static OllamaPlanningRequestInterpreter CreateInterpreter(StubHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:11434/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        return new OllamaPlanningRequestInterpreter(
            client,
            Options.Create(new OllamaOptions
            {
                BaseUrl = "http://localhost:11434",
                Model = "qwen2.5:7b",
                TimeoutSeconds = 60
            }));
    }

    private static HttpResponseMessage Response(string output) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = "qwen2.5:7b",
                    created_at = "2026-10-23T00:00:00Z",
                    message = new { role = "assistant", content = output },
                    done = true
                }),
                Encoding.UTF8,
                "application/json")
        };

    private static string ValidOutput() =>
        """{"maintenanceIntent":"bearing replacement","scope":"Pump A-1 bearing","scopeResolution":"Resolved","proposedAssetIds":["A-1"],"proposedTaskIds":["A1-BEARING"],"missingInformation":[],"assumptions":[],"scopeMappings":[{"taskId":"A1-BEARING","assetId":"A-1"}]}""";

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await send(request, cancellationToken);
        }
    }
}
