namespace MaintenancePlanner.Application.PlanningJobs;

public interface IPlanningJobs
{
    Task<PlanningJobCreationResult> CreateAsync(
        Guid operationId,
        string requestPayload,
        CancellationToken cancellationToken);
    Task<PlanningJobReadModel?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ClaimedPlanningJob?> ClaimNextAsync(
        Guid leaseToken,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
    Task<int?> RenewLeaseAsync(
        Guid jobId,
        Guid leaseToken,
        int expectedVersion,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
    Task<bool> CompleteAsync(
        Guid jobId,
        Guid leaseToken,
        int expectedVersion,
        string resultPayload,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
    Task<bool> FailAsync(
        Guid jobId,
        Guid leaseToken,
        int expectedVersion,
        string failureCode,
        string errorMessage,
        bool isTransient,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
    Task<bool> SaveCheckpointAsync(
        Guid jobId,
        Guid leaseToken,
        int expectedVersion,
        string checkpointPayload,
        CancellationToken cancellationToken);
    Task<bool> UpdateAndResetAsync(
        Guid jobId,
        string updatedRequestPayload,
        CancellationToken cancellationToken);

    Task<PlanningJobCreationResult> CreateReplacementAsync(
        Guid operationId,
        Guid originalJobId,
        string requestPayload,
        CancellationToken cancellationToken);
}
