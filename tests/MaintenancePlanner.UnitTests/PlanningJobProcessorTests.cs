using System.Text.Json;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Infrastructure.Planning;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MaintenancePlanner.UnitTests;

public sealed class PlanningJobProcessorTests
{
    private static readonly PlanningInterpretation Interpretation = new(
        "Bearing replacement",
        "P-101 bearing",
        ["P-101"],
        ["P101-BEARING-REPLACEMENT"],
        [],
        [])
    {
        ScopeResolution = PlanningScopeResolution.Resolved
    };

    public static IEnumerable<object[]> BusinessOutcomes =>
    [
        [new PlanningCandidatesGeneratedResult([], [], []) { Interpretation = Interpretation }, "CandidatesGenerated"],
        [new PlanningClarificationRequiredResult(["Which task?"], [], []) { Interpretation = Interpretation }, "ClarificationRequired"],
        [new PlanningInfeasibleResult([], [], []) { Interpretation = Interpretation }, "Infeasible"],
        [new PlanningInvalidInputResult([], [], []) { Interpretation = Interpretation }, "InvalidInput"],
        [new PlanningDataUnavailableResult([], [], []) { Interpretation = Interpretation }, "DataUnavailable"],
        [new PlanningSearchLimitReachedResult([], [], []) { Interpretation = Interpretation }, "SearchLimitReached"]
    ];

    [Theory]
    [MemberData(nameof(BusinessOutcomes))]
    public async Task ProcessAsync_SerializesEachBusinessOutcome(
        PlanningWorkflowResult workflowResult,
        string expectedOutcome)
    {
        var workflow = new Mock<IPlanningWorkflow>(MockBehavior.Strict);
        workflow.Setup(service => service.ExecuteAsync(
                It.IsAny<PlanningRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflowResult);
        workflow.Setup(service => service.ResumeFromCheckpointAsync(
                It.IsAny<PlanningRequest>(),
                It.IsAny<PlanningInterpretation>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflowResult);
        var processor = new PlanningJobProcessor(workflow.Object);

        var job = CreateJob(ValidPayload());
        var processed = await processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None);

        using var document = JsonDocument.Parse(processed.ResultPayload);
        var envelope = document.RootElement;
        Assert.Equal(expectedOutcome, envelope.GetProperty("outcome").GetString());
        Assert.Equal(
            "P101-BEARING-REPLACEMENT",
            envelope.GetProperty("result")
                .GetProperty("interpretation")
                .GetProperty("proposedTaskIds")[0]
                .GetString());
        workflow.Verify(service => service.ExecuteAsync(
            It.Is<PlanningRequest>(request =>
                request.MaintenanceWindowId == "MW-P101-BASELINE-20261024" &&
                request.AssetIds.SequenceEqual(new[] { "P-101" }) &&
                request.RequestText == "Replace the P-101 bearing."),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("not-json", "INVALID_PERSISTED_REQUEST")]
    [InlineData("null", "INVALID_PERSISTED_REQUEST")]
    [InlineData("""{"requestText":"request","assetIds":[]}""", "INVALID_PERSISTED_REQUEST")]
    public async Task ProcessAsync_InvalidPersistedRequestFailsClearly(
        string payload,
        string expectedCode)
    {
        var processor = new PlanningJobProcessor();
        var job = CreateJob(payload);

        var exception = await Assert.ThrowsAsync<PlanningJobProcessingException>(
            () => processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None));

        Assert.Equal(expectedCode, exception.FailureCode);
    }

    [Fact]
    public async Task ProcessAsync_MissingWorkflowFailsClearly()
    {
        var processor = new PlanningJobProcessor();
        var job = CreateJob(ValidPayload());

        var exception = await Assert.ThrowsAsync<PlanningJobProcessingException>(
            () => processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None));

        Assert.Equal("PLANNING_WORKFLOW_UNAVAILABLE", exception.FailureCode);
    }

    [Fact]
    public async Task ProcessAsync_UnregisteredWorkflowDependencyFailsClearlyThroughDependencyInjection()
    {
        var services = new ServiceCollection();
        services.AddScoped<IPlanningJobProcessor, PlanningJobProcessor>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<IPlanningJobProcessor>();
        var job = CreateJob(ValidPayload());

        var exception = await Assert.ThrowsAsync<PlanningJobProcessingException>(
            () => processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None));

        Assert.Equal("PLANNING_WORKFLOW_UNAVAILABLE", exception.FailureCode);
    }

    [Fact]
    public async Task ProcessAsync_OllamaFailureRemainsTechnicalJobFailure()
    {
        var workflow = new Mock<IPlanningWorkflow>(MockBehavior.Strict);
        workflow.Setup(service => service.ExecuteAsync(
                It.IsAny<PlanningRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaPlanningInterpreterException(
                "OLLAMA_UNAVAILABLE",
                "The provider is unavailable.",
                isTransient: true));
        var processor = new PlanningJobProcessor(workflow.Object);
        var job = CreateJob(ValidPayload());

        var exception = await Assert.ThrowsAsync<PlanningJobProcessingException>(
            () => processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None));

        Assert.Equal("OLLAMA_UNAVAILABLE", exception.FailureCode);
        Assert.True(exception.IsTransient);
    }

    private static string ValidPayload() =>
        JsonSerializer.Serialize(
            new PlanningJobRequestPayload(
                "Replace the P-101 bearing.",
                ["P-101"],
                "MW-P101-BASELINE-20261024"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static ClaimedPlanningJob CreateJob(string requestPayload) =>
        new(Guid.NewGuid(), requestPayload, 1, 3, 1, Guid.NewGuid(), null);
}
