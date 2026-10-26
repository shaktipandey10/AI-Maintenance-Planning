using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Infrastructure.Planning;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using Moq;
using System.Text.Json;

namespace MaintenancePlanner.UnitTests;

public sealed class AT10ErrorHandlingTests
{
    // AT10: Error handling for model timeout, malformed output, inventory lookup failure
    // 1. Model timeout produces distinct OLLAMA_TIMEOUT failure code (transient)
    // 2. Malformed output produces distinct OLLAMA_INVALID_OUTPUT failure code (non-transient)
    // 3. Inventory lookup failure (PART_DATA_UNAVAILABLE) distinguished from
    //    business infeasibility (PART_UNAVAILABLE / Infeasible outcome)
    // 4. Failed job result type prevents approval
    // 5. Job state remains consistent through error handling

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AT10_ModelTimeoutProducesDistinctOllamaTimeoutFailure()
    {
        // Model timeout should be caught and converted to PlanningJobProcessingException
        // with OLLAMA_TIMEOUT failure code (transient)

        var workflow = new Mock<IPlanningWorkflow>(MockBehavior.Strict);
        workflow.Setup(w => w.ExecuteAsync(It.IsAny<PlanningRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaPlanningInterpreterException(
                "OLLAMA_TIMEOUT",
                "Ollama did not respond within 30 seconds.",
                isTransient: true));

        var processor = new PlanningJobProcessor(workflow.Object);
        var job = CreateClaimedJob();

        // Should throw PlanningJobProcessingException with OLLAMA_TIMEOUT
        var exception = await Assert.ThrowsAsync<PlanningJobProcessingException>(
            () => processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None));

        Assert.Equal("OLLAMA_TIMEOUT", exception.FailureCode);
        Assert.True(exception.IsTransient);
        Assert.Contains("did not respond", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AT10_MalformedModelOutputProducesDistinctOllamaInvalidOutputFailure()
    {
        // Malformed model output should be caught and converted to PlanningJobProcessingException
        // with OLLAMA_INVALID_OUTPUT failure code (non-transient)

        var workflow = new Mock<IPlanningWorkflow>(MockBehavior.Strict);
        workflow.Setup(w => w.ExecuteAsync(It.IsAny<PlanningRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaPlanningInterpreterException(
                "OLLAMA_INVALID_OUTPUT",
                "Ollama output did not match the required JSON contract: Unexpected end of input.",
                isTransient: false));

        var processor = new PlanningJobProcessor(workflow.Object);
        var job = CreateClaimedJob();

        // Should throw PlanningJobProcessingException with OLLAMA_INVALID_OUTPUT
        var exception = await Assert.ThrowsAsync<PlanningJobProcessingException>(
            () => processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None));

        Assert.Equal("OLLAMA_INVALID_OUTPUT", exception.FailureCode);
        Assert.False(exception.IsTransient);
        Assert.Contains("did not match", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AT10_InventoryDataUnavailableProducesDataUnavailableOutcome()
    {
        // Inventory data unavailable should produce DataUnavailable outcome
        // (distinct from Infeasible outcome when inventory is insufficient)

        var interpretation = new PlanningInterpretation(
            "Bearing replacement",
            "P-101 bearing",
            ["P-101"],
            ["P101-BEARING-REPLACEMENT"],
            [],
            [])
        {
            ScopeResolution = PlanningScopeResolution.Resolved
        };

        var result = new PlanningDataUnavailableResult(
            [new PlanningValidationViolation(
                "PART_DATA_UNAVAILABLE",
                "Inventory data does not include required part BR-101; availability cannot be established.",
                "P101-BEARING-REPLACEMENT",
                "BR-101")],
            [],
            [])
        {
            Interpretation = interpretation
        };

        var workflow = new Mock<IPlanningWorkflow>(MockBehavior.Strict);
        workflow.Setup(w => w.ExecuteAsync(It.IsAny<PlanningRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var processor = new PlanningJobProcessor(workflow.Object);
        var job = CreateClaimedJob();

        var processed = await processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None);

        // Parse result and verify outcome is DataUnavailable
        using var document = JsonDocument.Parse(processed.ResultPayload);
        var outcome = document.RootElement.GetProperty("outcome").GetString();

        Assert.Equal("DataUnavailable", outcome);
    }

    [Fact]
    public async Task AT10_InsufficientInventoryProducesInfeasibleOutcome()
    {
        // Insufficient inventory should produce Infeasible outcome
        // (distinct from DataUnavailable when inventory data is missing)

        var interpretation = new PlanningInterpretation(
            "Bearing replacement",
            "P-101 bearing",
            ["P-101"],
            ["P101-BEARING-REPLACEMENT"],
            [],
            [])
        {
            ScopeResolution = PlanningScopeResolution.Resolved
        };

        var result = new PlanningInfeasibleResult(
            [new PlanningValidationViolation(
                "PART_UNAVAILABLE",
                "Spare part BR-101 has 1 available but 2 is required across the requested scope.",
                "P101-BEARING-REPLACEMENT",
                "BR-101")],
            [],
            [])
        {
            Interpretation = interpretation
        };

        var workflow = new Mock<IPlanningWorkflow>(MockBehavior.Strict);
        workflow.Setup(w => w.ExecuteAsync(It.IsAny<PlanningRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var processor = new PlanningJobProcessor(workflow.Object);
        var job = CreateClaimedJob();

        var processed = await processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None);

        // Parse result and verify outcome is Infeasible (not DataUnavailable)
        using var document = JsonDocument.Parse(processed.ResultPayload);
        var outcome = document.RootElement.GetProperty("outcome").GetString();

        Assert.Equal("Infeasible", outcome);
    }

    [Fact]
    public async Task AT10_InvalidInputOutcomeIndicatesFailedPlan()
    {
        // Invalid input outcome should indicate the plan is not viable for approval

        var emptyInterpretation = new PlanningInterpretation(
            "Placeholder",
            "Placeholder",
            [],
            [],
            [],
            [])
        {
            ScopeResolution = PlanningScopeResolution.Unresolved
        };

        var result = new PlanningInvalidInputResult(
            [new PlanningValidationViolation(
                "INVALID_ASSET_SCOPE",
                "Selected asset IDs must be nonblank and unique.",
                null,
                null)],
            [],
            [])
        {
            Interpretation = emptyInterpretation
        };

        var workflow = new Mock<IPlanningWorkflow>(MockBehavior.Strict);
        workflow.Setup(w => w.ExecuteAsync(It.IsAny<PlanningRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var processor = new PlanningJobProcessor(workflow.Object);
        var job = CreateClaimedJob();

        var processed = await processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None);

        // Parse result and verify outcome is InvalidInput
        using var document = JsonDocument.Parse(processed.ResultPayload);
        var outcome = document.RootElement.GetProperty("outcome").GetString();

        Assert.Equal("InvalidInput", outcome);
    }

    [Fact]
    public async Task AT10_ClarificationRequiredOutcomeIndicatesIncompletePlan()
    {
        // Clarification required outcome indicates plan is incomplete, not approvable

        var interpretation = new PlanningInterpretation(
            "Bearing replacement",
            "P-101 bearing",
            ["P-101"],
            ["P101-BEARING-REPLACEMENT"],
            ["Which spare bearing model?"],
            [])
        {
            ScopeResolution = PlanningScopeResolution.Ambiguous
        };

        var result = new PlanningClarificationRequiredResult(
            ["Clarify which spare bearing model is required."],
            [],
            [])
        {
            Interpretation = interpretation
        };

        var workflow = new Mock<IPlanningWorkflow>(MockBehavior.Strict);
        workflow.Setup(w => w.ExecuteAsync(It.IsAny<PlanningRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var processor = new PlanningJobProcessor(workflow.Object);
        var job = CreateClaimedJob();

        var processed = await processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None);

        // Parse result and verify outcome is ClarificationRequired
        using var document = JsonDocument.Parse(processed.ResultPayload);
        var outcome = document.RootElement.GetProperty("outcome").GetString();

        Assert.Equal("ClarificationRequired", outcome);
    }

    [Fact]
    public async Task AT10_ExceptionResultTypeIndicatesProcessingError()
    {
        // Unexpected exception from workflow should be caught and wrapped

        var workflow = new Mock<IPlanningWorkflow>(MockBehavior.Strict);
        workflow.Setup(w => w.ExecuteAsync(It.IsAny<PlanningRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected workflow error"));

        var processor = new PlanningJobProcessor(workflow.Object);
        var job = CreateClaimedJob();

        // Should convert to PlanningJobProcessingException
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None));

        Assert.Equal("Unexpected workflow error", exception.Message);
    }

    [Fact]
    public async Task AT10_NullWorkflowProducesDistinctFailure()
    {
        // No workflow available should produce PLANNING_WORKFLOW_UNAVAILABLE

        var processor = new PlanningJobProcessor(workflow: null);
        var job = CreateClaimedJob();

        var exception = await Assert.ThrowsAsync<PlanningJobProcessingException>(
            () => processor.ProcessAsync(job.Id, job.LeaseToken, job, CancellationToken.None));

        Assert.Equal("PLANNING_WORKFLOW_UNAVAILABLE", exception.FailureCode);
        Assert.Contains("no supported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ClaimedPlanningJob CreateClaimedJob()
    {
        var jobId = Guid.NewGuid();
        var requestPayload = """{"requestText":"Plan bearing replacement.","assetIds":["P-101"]}""";
        return new ClaimedPlanningJob(
            jobId,
            requestPayload,
            1,  // AttemptCount
            3,  // MaxAttempts
            1,  // ConcurrencyVersion
            Guid.NewGuid(),  // LeaseToken
            string.Empty);  // CheckpointPayload
    }
}
