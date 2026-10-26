namespace MaintenancePlanner.Application.PlanningJobs;

public interface IPlanningJobProcessor
{
    Task<PlanningJobProcessingResult> ProcessAsync(
        Guid jobId,
        Guid leaseToken,
        ClaimedPlanningJob job,
        CancellationToken cancellationToken);
}

public sealed class PlanningJobProcessingException(
    string failureCode,
    string message,
    bool isTransient = false) : Exception(message)
{
    public string FailureCode { get; } = failureCode;
    public bool IsTransient { get; } = isTransient;
}
