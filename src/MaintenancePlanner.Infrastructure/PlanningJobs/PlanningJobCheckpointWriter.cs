using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;

namespace MaintenancePlanner.Infrastructure.PlanningJobs;

/// <summary>
/// Persists the validated interpretation of the planning job processed in the current scope as its checkpoint,
/// through the lease- and version-guarded repository write and the scope's execution context.
/// </summary>
public sealed class PlanningJobCheckpointWriter(
    IPlanningJobs planningJobs,
    PlanningJobExecutionContext execution) : IPlanningInterpretationCheckpointSink
{
    public async Task SaveValidatedInterpretationAsync(
        PlanningInterpretation interpretation,
        CancellationToken cancellationToken)
    {
        // The workflow is not running as a planning job in this scope, so there is no job to checkpoint.
        if (!execution.IsActive)
            return;

        var job = execution.Job;
        var checkpointPayload = PlanningInterpretationCheckpoint.Serialize(
            job.Id,
            job.RequestPayload,
            interpretation,
            DateTimeOffset.UtcNow);

        // SaveCheckpointAsync advances the job's version by one when it applies.
        var advancedVersion = await execution.AdvanceAsync(
            async version => await planningJobs.SaveCheckpointAsync(
                job.Id,
                job.LeaseToken,
                version,
                checkpointPayload,
                cancellationToken)
                ? version + 1
                : null,
            cancellationToken);

        if (advancedVersion is null)
            throw new PlanningJobLeaseLostException(job.Id);
    }
}
