using System.Security.Cryptography;
using System.Text;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.PlanningJobs;

public sealed class PlanningJobs(MaintenancePlannerDbContext dbContext) : IPlanningJobs
{
    public async Task<PlanningJobCreationResult> CreateAsync(
        Guid operationId,
        string requestPayload,
        CancellationToken cancellationToken)
    {
        var requestFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(requestPayload)));
        var existingJob = await dbContext.PlanningJobs.AsNoTracking()
            .SingleOrDefaultAsync(job => job.OperationId == operationId, cancellationToken);
        if (existingJob is not null)
            return ExistingResult(existingJob, requestFingerprint);

        var job = new PlanningJob
        {
            Id = Guid.NewGuid(),
            OperationId = operationId,
            RequestPayload = requestPayload,
            RequestFingerprint = requestFingerprint,
            Status = PlanningJobStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await dbContext.PlanningJobs.AddAsync(job, cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            dbContext.Entry(job).State = EntityState.Detached;
            existingJob = await dbContext.PlanningJobs.AsNoTracking()
                .SingleOrDefaultAsync(existing => existing.OperationId == operationId, cancellationToken);

            if (existingJob is null)
                throw;

            return ExistingResult(existingJob, requestFingerprint);
        }

        return new PlanningJobCreationResult(ToReadModel(job), PayloadConflict: false);
    }

    public async Task<PlanningJobReadModel?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await dbContext.PlanningJobs.AsNoTracking()
            .SingleOrDefaultAsync(existing => existing.Id == id, cancellationToken);

        return job is null ? null : ToReadModel(job);
    }

    public async Task<ClaimedPlanningJob?> ClaimNextAsync(
        Guid leaseToken,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var nowUnixMilliseconds = nowUtc.ToUnixTimeMilliseconds();
        var exhaustedJobs = await dbContext.PlanningJobs.AsNoTracking()
            .Where(job =>
                (job.Status == PlanningJobStatus.Queued && job.AttemptCount >= job.MaxAttempts) ||
                (job.Status == PlanningJobStatus.Running &&
                 job.LeaseExpiresAtUnixMilliseconds <= nowUnixMilliseconds &&
                 job.AttemptCount >= job.MaxAttempts))
            .Select(job => new { job.Id, job.ConcurrencyVersion })
            .ToListAsync(cancellationToken);

        foreach (var exhaustedJob in exhaustedJobs)
        {
            await dbContext.PlanningJobs
                .Where(job =>
                    job.Id == exhaustedJob.Id &&
                    job.ConcurrencyVersion == exhaustedJob.ConcurrencyVersion &&
                    ((job.Status == PlanningJobStatus.Queued && job.AttemptCount >= job.MaxAttempts) ||
                     (job.Status == PlanningJobStatus.Running &&
                      job.LeaseExpiresAtUnixMilliseconds <= nowUnixMilliseconds &&
                      job.AttemptCount >= job.MaxAttempts)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, PlanningJobStatus.Failed)
                    .SetProperty(job => job.CurrentStep, "Failed")
                    .SetProperty(job => job.FailureCode, "MAX_ATTEMPTS_EXCEEDED")
                    .SetProperty(job => job.ErrorMessage, "The planning job exhausted its allowed processing attempts.")
                    .SetProperty(job => job.CompletedAtUtc, nowUtc)
                    .SetProperty(job => job.WorkerLeaseToken, (Guid?)null)
                    .SetProperty(job => job.LeaseExpiresAtUnixMilliseconds, (long?)null)
                    .SetProperty(job => job.ConcurrencyVersion, job => job.ConcurrencyVersion + 1),
                    cancellationToken);
        }

        var candidateId = await dbContext.PlanningJobs.AsNoTracking()
            .Where(job =>
                (job.Status == PlanningJobStatus.Queued && job.AttemptCount < job.MaxAttempts) ||
                (job.Status == PlanningJobStatus.Running &&
                 job.LeaseExpiresAtUnixMilliseconds <= nowUnixMilliseconds &&
                 job.AttemptCount < job.MaxAttempts))
            .Select(job => new
            {
                job.Id,
                job.ConcurrencyVersion,
                job.RequestPayload,
                job.CheckpointPayload,
                job.AttemptCount,
                job.MaxAttempts
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (candidateId is null)
            return null;

        var claimedCount = await dbContext.PlanningJobs
            .Where(job =>
                job.Id == candidateId.Id &&
                job.ConcurrencyVersion == candidateId.ConcurrencyVersion &&
                ((job.Status == PlanningJobStatus.Queued && job.AttemptCount < job.MaxAttempts) ||
                 (job.Status == PlanningJobStatus.Running &&
                  job.LeaseExpiresAtUnixMilliseconds <= nowUnixMilliseconds &&
                  job.AttemptCount < job.MaxAttempts)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, PlanningJobStatus.Running)
                .SetProperty(job => job.AttemptCount, job => job.AttemptCount + 1)
                .SetProperty(job => job.CurrentStep, "Processing")
                .SetProperty(job => job.StartedAtUtc, nowUtc)
                .SetProperty(job => job.WorkerLeaseToken, leaseToken)
                .SetProperty(
                    job => job.LeaseExpiresAtUnixMilliseconds,
                    nowUtc.Add(leaseDuration).ToUnixTimeMilliseconds())
                .SetProperty(job => job.FailureCode, (string?)null)
                .SetProperty(job => job.ErrorMessage, (string?)null)
                .SetProperty(job => job.ConcurrencyVersion, job => job.ConcurrencyVersion + 1),
                cancellationToken);

        if (claimedCount == 0)
            return null;

        return new ClaimedPlanningJob(
            candidateId.Id,
            candidateId.RequestPayload,
            candidateId.AttemptCount + 1,
            candidateId.MaxAttempts,
            checked(candidateId.ConcurrencyVersion + 1),
            leaseToken,
            candidateId.CheckpointPayload);
    }

    public async Task<int?> RenewLeaseAsync(
        Guid jobId,
        Guid leaseToken,
        int expectedVersion,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var nowUnixMilliseconds = nowUtc.ToUnixTimeMilliseconds();
        var updatedCount = await dbContext.PlanningJobs
            .Where(job =>
                job.Id == jobId &&
                job.Status == PlanningJobStatus.Running &&
                job.WorkerLeaseToken == leaseToken &&
                job.ConcurrencyVersion == expectedVersion &&
                job.LeaseExpiresAtUnixMilliseconds > nowUnixMilliseconds)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    job => job.LeaseExpiresAtUnixMilliseconds,
                    nowUtc.Add(leaseDuration).ToUnixTimeMilliseconds())
                .SetProperty(job => job.ConcurrencyVersion, job => job.ConcurrencyVersion + 1),
                cancellationToken);

        if (updatedCount == 1)
            return checked(expectedVersion + 1);

        await ThrowIfVersionConflictAsync(
            jobId,
            leaseToken,
            expectedVersion,
            nowUnixMilliseconds,
            cancellationToken);
        return null;
    }

    public async Task<bool> CompleteAsync(
        Guid jobId,
        Guid leaseToken,
        int expectedVersion,
        string resultPayload,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var nowUnixMilliseconds = nowUtc.ToUnixTimeMilliseconds();
        var updatedCount = await dbContext.PlanningJobs
            .Where(job =>
                job.Id == jobId &&
                job.Status == PlanningJobStatus.Running &&
                job.WorkerLeaseToken == leaseToken &&
                job.ConcurrencyVersion == expectedVersion &&
                job.LeaseExpiresAtUnixMilliseconds > nowUnixMilliseconds)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, PlanningJobStatus.Succeeded)
                .SetProperty(job => job.CurrentStep, "Completed")
                .SetProperty(job => job.ResultPayload, resultPayload)
                .SetProperty(job => job.CompletedAtUtc, nowUtc)
                .SetProperty(job => job.WorkerLeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAtUnixMilliseconds, (long?)null)
                .SetProperty(job => job.ConcurrencyVersion, job => job.ConcurrencyVersion + 1),
                cancellationToken);

        if (updatedCount == 1)
            return true;

        await ThrowIfVersionConflictAsync(
            jobId,
            leaseToken,
            expectedVersion,
            nowUnixMilliseconds,
            cancellationToken);
        return false;
    }

    public async Task<bool> FailAsync(
        Guid jobId,
        Guid leaseToken,
        int expectedVersion,
        string failureCode,
        string errorMessage,
        bool isTransient,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var nowUnixMilliseconds = nowUtc.ToUnixTimeMilliseconds();
        var job = await dbContext.PlanningJobs.AsNoTracking()
            .SingleOrDefaultAsync(existing =>
                existing.Id == jobId &&
                existing.Status == PlanningJobStatus.Running &&
                existing.WorkerLeaseToken == leaseToken &&
                existing.ConcurrencyVersion == expectedVersion &&
                existing.LeaseExpiresAtUnixMilliseconds > nowUnixMilliseconds,
                cancellationToken);

        if (job is null)
            return false;

        var shouldRetry = isTransient && job.AttemptCount < job.MaxAttempts;
        var updatedCount = await dbContext.PlanningJobs
            .Where(existing =>
                existing.Id == jobId &&
                existing.Status == PlanningJobStatus.Running &&
                existing.WorkerLeaseToken == leaseToken &&
                existing.ConcurrencyVersion == expectedVersion &&
                existing.LeaseExpiresAtUnixMilliseconds > nowUnixMilliseconds)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    existing => existing.Status,
                    shouldRetry ? PlanningJobStatus.Queued : PlanningJobStatus.Failed)
                .SetProperty(
                    existing => existing.CurrentStep,
                    shouldRetry ? "Queued" : "Failed")
                .SetProperty(existing => existing.FailureCode, failureCode)
                .SetProperty(existing => existing.ErrorMessage, errorMessage)
                .SetProperty(
                    existing => existing.CompletedAtUtc,
                    shouldRetry ? (DateTimeOffset?)null : nowUtc)
                .SetProperty(existing => existing.WorkerLeaseToken, (Guid?)null)
                .SetProperty(existing => existing.LeaseExpiresAtUnixMilliseconds, (long?)null)
                .SetProperty(existing => existing.ConcurrencyVersion, existing => existing.ConcurrencyVersion + 1),
                cancellationToken);

        if (updatedCount == 1)
            return true;

        await ThrowIfVersionConflictAsync(
            jobId,
            leaseToken,
            expectedVersion,
            nowUnixMilliseconds,
            cancellationToken);
        return false;
    }

    private async Task ThrowIfVersionConflictAsync(
        Guid jobId,
        Guid leaseToken,
        int expectedVersion,
        long nowUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        var currentVersion = await dbContext.PlanningJobs.AsNoTracking()
            .Where(job =>
                job.Id == jobId &&
                job.Status == PlanningJobStatus.Running &&
                job.WorkerLeaseToken == leaseToken &&
                job.LeaseExpiresAtUnixMilliseconds > nowUnixMilliseconds)
            .Select(job => (int?)job.ConcurrencyVersion)
            .SingleOrDefaultAsync(cancellationToken);

        if (currentVersion is not null && currentVersion != expectedVersion)
        {
            throw new DbUpdateConcurrencyException(
                $"Planning job {jobId} was updated using concurrency version {currentVersion}; " +
                $"version {expectedVersion} is stale.");
        }
    }

    private static PlanningJobReadModel ToReadModel(PlanningJob job) =>
        new(
            job.Id,
            job.Status,
            job.AttemptCount,
            job.MaxAttempts,
            job.CurrentStep,
            job.ResultPayload,
            job.ClarificationPayload,
            job.FailureCode,
            job.CreatedAtUtc,
            job.StartedAtUtc,
            job.CompletedAtUtc,
            job.ErrorMessage,
            job.RequestPayload,
            job.OriginalJobId);

    public async Task<bool> SaveCheckpointAsync(
        Guid jobId,
        Guid leaseToken,
        int expectedVersion,
        string checkpointPayload,
        CancellationToken cancellationToken)
    {
        var nowUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var updatedCount = await dbContext.PlanningJobs
            .Where(job =>
                job.Id == jobId &&
                job.Status == PlanningJobStatus.Running &&
                job.WorkerLeaseToken == leaseToken &&
                job.ConcurrencyVersion == expectedVersion &&
                job.LeaseExpiresAtUnixMilliseconds > nowUnixMilliseconds)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.CheckpointPayload, checkpointPayload)
                .SetProperty(job => job.ConcurrencyVersion, job => job.ConcurrencyVersion + 1),
                cancellationToken);

        if (updatedCount == 1)
            return true;

        await ThrowIfVersionConflictAsync(
            jobId,
            leaseToken,
            expectedVersion,
            nowUnixMilliseconds,
            cancellationToken);
        return false;
    }

    public async Task<bool> UpdateAndResetAsync(
        Guid jobId,
        string updatedRequestPayload,
        CancellationToken cancellationToken)
    {
        var job = await dbContext.PlanningJobs.AsNoTracking()
            .SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job is null)
            return false;

        var clarificationPayload = job.ResultPayload;

        var updatedCount = await dbContext.PlanningJobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.RequestPayload, updatedRequestPayload)
                .SetProperty(j => j.Status, PlanningJobStatus.Queued)
                .SetProperty(j => j.CurrentStep, "Queued")
                .SetProperty(j => j.ResultPayload, (string?)null)
                .SetProperty(j => j.ClarificationPayload, clarificationPayload)
                .SetProperty(j => j.CheckpointPayload, (string?)null)
                .SetProperty(j => j.FailureCode, (string?)null)
                .SetProperty(j => j.ErrorMessage, (string?)null)
                .SetProperty(j => j.WorkerLeaseToken, (Guid?)null)
                .SetProperty(j => j.LeaseExpiresAtUnixMilliseconds, (long?)null)
                .SetProperty(j => j.AttemptCount, 0)
                .SetProperty(j => j.ConcurrencyVersion, j => j.ConcurrencyVersion + 1),
                cancellationToken);

        return updatedCount == 1;
    }

    public async Task<PlanningJobCreationResult> CreateReplacementAsync(
        Guid operationId,
        Guid originalJobId,
        string requestPayload,
        CancellationToken cancellationToken)
    {
        var requestFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(requestPayload)));
        var existingJob = await dbContext.PlanningJobs.AsNoTracking()
            .SingleOrDefaultAsync(job => job.OperationId == operationId, cancellationToken);
        if (existingJob is not null)
            return ExistingResult(existingJob, requestFingerprint);

        var job = new PlanningJob
        {
            Id = Guid.NewGuid(),
            OperationId = operationId,
            OriginalJobId = originalJobId,
            RequestPayload = requestPayload,
            RequestFingerprint = requestFingerprint,
            Status = PlanningJobStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await dbContext.PlanningJobs.AddAsync(job, cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            dbContext.Entry(job).State = EntityState.Detached;
            existingJob = await dbContext.PlanningJobs.AsNoTracking()
                .SingleOrDefaultAsync(existing => existing.OperationId == operationId, cancellationToken);

            if (existingJob is null)
                throw;

            return ExistingResult(existingJob, requestFingerprint);
        }

        return new PlanningJobCreationResult(ToReadModel(job), PayloadConflict: false);
    }

    private static PlanningJobCreationResult ExistingResult(
        PlanningJob existingJob,
        string requestFingerprint) =>
        new(
            ToReadModel(existingJob),
            PayloadConflict: !string.Equals(
                existingJob.RequestFingerprint,
                requestFingerprint,
                StringComparison.Ordinal));
}
