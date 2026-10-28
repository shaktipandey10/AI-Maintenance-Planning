namespace MaintenancePlanner.Application.PlanningJobs;

/// <summary>
/// The lease and concurrency version of the planning job processed in the current scope. Every lease-guarded write
/// for the job (lease renewal, checkpoint, completion, failure, release) goes through this context one at a time and
/// with the latest version, so a checkpoint written by the processor and a lease renewal written by the worker can
/// neither use the same version nor leave the other holding a stale one.
/// </summary>
public sealed class PlanningJobExecutionContext
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaimedPlanningJob? _job;
    private int _version;

    public bool IsActive => _job is not null;

    public ClaimedPlanningJob Job =>
        _job ?? throw new InvalidOperationException("No planning job is being processed in this scope.");

    public int Version => _version;

    public void Begin(ClaimedPlanningJob job)
    {
        if (_job is not null)
            throw new InvalidOperationException("This scope is already processing a planning job.");

        _job = job;
        _version = job.ConcurrencyVersion;
    }

    /// <summary>
    /// Runs a write that keeps the lease (renewal or checkpoint). The write receives the current version and returns
    /// the version it advanced the job to, or null when this lease no longer owns the job.
    /// </summary>
    public async Task<int?> AdvanceAsync(Func<int, Task<int?>> write, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var advancedVersion = await write(_version);
            if (advancedVersion is { } version)
                _version = version;

            return advancedVersion;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs a write that ends the lease (completion, failure or release) with the current version.</summary>
    public async Task<bool> FinishAsync(Func<int, Task<bool>> write, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await write(_version);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Raised when a lease-guarded write finds that the current worker no longer owns the planning job.</summary>
public sealed class PlanningJobLeaseLostException(Guid jobId)
    : Exception($"The worker no longer holds the lease for planning job {jobId}.")
{
    public Guid JobId { get; } = jobId;
}
