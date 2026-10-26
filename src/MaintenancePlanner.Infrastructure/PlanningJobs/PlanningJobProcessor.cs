using System.Text.Json;
using MaintenancePlanner.Application.Locks;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;

namespace MaintenancePlanner.Infrastructure.PlanningJobs;

public sealed class PlanningJobProcessor(
    IPlanningWorkflow? workflow = null,
    IPlanningJobs? planningJobs = null,
    MaintenancePlannerDbContext? dbContext = null,
    ITaskLockService? lockService = null) : IPlanningJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<PlanningJobProcessingResult> ProcessAsync(
        Guid jobId,
        Guid leaseToken,
        ClaimedPlanningJob job,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PlanningJobRequestPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PlanningJobRequestPayload>(
                job.RequestPayload,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new PlanningJobProcessingException(
                "INVALID_PERSISTED_REQUEST",
                $"The persisted planning request is not valid JSON: {exception.Message}");
        }

        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.RequestText) ||
            payload.AssetIds is null ||
            payload.AssetIds.Count == 0 ||
            payload.AssetIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new PlanningJobProcessingException(
                "INVALID_PERSISTED_REQUEST",
                "The persisted planning request is missing valid request text or asset IDs.");
        }

        if (workflow is null)
        {
            throw new PlanningJobProcessingException(
                "PLANNING_WORKFLOW_UNAVAILABLE",
                "Planning cannot run because no supported request interpreter and planning workflow are registered.");
        }

        var request = new PlanningRequest(
            payload.RequestText,
            payload.AssetIds.Select(assetId => assetId!).ToArray(),
            payload.WindowId);

        // Fetch locks from the original job's approved draft if this is a replacement job
        IReadOnlyDictionary<string, string>? locks = null;
        if (dbContext is not null && lockService is not null)
        {
            try
            {
                var fullJob = await dbContext.PlanningJobs.FindAsync(new object[] { jobId }, cancellationToken: cancellationToken);
                if (fullJob?.OriginalJobId is not null)
                {
                    locks = await lockService.GetLocksForJobAsync(fullJob.OriginalJobId.Value, cancellationToken);
                }
            }
            catch
            {
                // Silently ignore lock retrieval errors; planning continues without locks
            }
        }

        PlanningWorkflowResult result;
        try
        {
            // Check if we have a checkpoint to resume from
            if (!string.IsNullOrEmpty(job.CheckpointPayload))
            {
                var checkpoint = JsonSerializer.Deserialize<PlanningJobInterpretationCheckpoint>(
                    job.CheckpointPayload,
                    JsonOptions);
                if (checkpoint is not null)
                {
                    var interpretationJson = JsonSerializer.Deserialize<PlanningInterpretation>(
                        checkpoint.InterpretationPayload,
                        JsonOptions);
                    if (interpretationJson is not null)
                    {
                        result = await workflow.ResumeFromCheckpointAsync(request, interpretationJson, cancellationToken, locks);
                    }
                    else
                    {
                        result = await workflow.ExecuteAsync(request, cancellationToken, locks);
                    }
                }
                else
                {
                    result = await workflow.ExecuteAsync(request, cancellationToken, locks);
                }
            }
            else
            {
                result = await workflow.ExecuteAsync(request, cancellationToken, locks);
            }
        }
        catch (OllamaPlanningInterpreterException exception)
        {
            throw new PlanningJobProcessingException(
                exception.FailureCode,
                exception.Message,
                exception.IsTransient);
        }

        if (result is null)
        {
            throw new PlanningJobProcessingException(
                "PLANNING_WORKFLOW_RETURNED_NO_RESULT",
                "The planning workflow returned no business outcome.");
        }

        var envelope = new PlanningJobResultEnvelope(
            GetOutcome(result),
            JsonSerializer.SerializeToElement(result, result.GetType(), JsonOptions));
        return new PlanningJobProcessingResult(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private static string GetOutcome(PlanningWorkflowResult result) =>
        result switch
        {
            PlanningCandidatesGeneratedResult => "CandidatesGenerated",
            PlanningClarificationRequiredResult => "ClarificationRequired",
            PlanningInfeasibleResult => "Infeasible",
            PlanningInvalidInputResult => "InvalidInput",
            PlanningDataUnavailableResult => "DataUnavailable",
            PlanningSearchLimitReachedResult => "SearchLimitReached",
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result.GetType(),
                "The planning workflow returned an unsupported result type.")
        };
}
