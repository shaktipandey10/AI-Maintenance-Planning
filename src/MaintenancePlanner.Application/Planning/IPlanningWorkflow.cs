namespace MaintenancePlanner.Application.Planning;

public interface IPlanningWorkflow
{
    Task<PlanningWorkflowResult> ExecuteAsync(
        PlanningRequest request,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? lockedTaskAssignments = null);

    Task<PlanningWorkflowResult> ResumeFromCheckpointAsync(
        PlanningRequest request,
        PlanningInterpretation checkpoint,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? lockedTaskAssignments = null);
}
