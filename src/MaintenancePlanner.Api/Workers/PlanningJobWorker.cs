using MaintenancePlanner.Application.PlanningJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MaintenancePlanner.Api.Workers;

public sealed class PlanningJobWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PlanningJobWorkerOptions> options,
    ILogger<PlanningJobWorker> logger) : BackgroundService
{
    private readonly TimeSpan _pollInterval = options.Value.PollInterval;
    private readonly TimeSpan _leaseDuration = options.Value.LeaseDuration;
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromTicks(options.Value.LeaseDuration.Ticks / 3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await ClaimNextAsync(stoppingToken);
            if (job is null)
            {
                await Task.Delay(_pollInterval, stoppingToken);
                continue;
            }

            await ProcessAsync(job, stoppingToken);
        }
    }

    private async Task<ClaimedPlanningJob?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var planningJobs = scope.ServiceProvider.GetRequiredService<IPlanningJobs>();
        return await planningJobs.ClaimNextAsync(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            _leaseDuration,
            cancellationToken);
    }

    private async Task ProcessAsync(ClaimedPlanningJob job, CancellationToken stoppingToken)
    {
        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await using var processorScope = scopeFactory.CreateAsyncScope();
        // The processor's checkpoint write and this worker's lease writes share the scope's execution context, so
        // they run one at a time and always use the job's latest version.
        var execution = processorScope.ServiceProvider.GetService<PlanningJobExecutionContext>()
            ?? new PlanningJobExecutionContext();
        execution.Begin(job);
        var processor = processorScope.ServiceProvider.GetRequiredService<IPlanningJobProcessor>();
        Task<PlanningJobProcessingResult> processTask;

        try
        {
            processTask = processor.ProcessAsync(job.Id, job.LeaseToken, job, processingCancellation.Token);
            var leaseLost = false;
            while (!processTask.IsCompleted)
            {
                await Task.WhenAny(processTask, Task.Delay(_heartbeatInterval, stoppingToken));
                stoppingToken.ThrowIfCancellationRequested();
                if (processTask.IsCompleted)
                    break;

                if (!await RenewLeaseAsync(execution, stoppingToken))
                {
                    leaseLost = true;
                    await processingCancellation.CancelAsync();
                    break;
                }
            }

            if (leaseLost)
            {
                await ObserveCancellationAsync(processTask);
                return;
            }

            var result = await processTask;
            var saved = await CompleteAsync(execution, result.ResultPayload, stoppingToken);
            if (!saved)
            {
                logger.LogWarning(
                    "Planning job {JobId} completed after its lease was lost; result was not persisted.",
                    job.Id);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown: return the job to the queue without consuming this attempt. If the release
            // cannot be recorded, the lease expires and makes the job eligible for recovery after shutdown.
            await ReleaseAsync(execution);
        }
        catch (OperationCanceledException) when (processingCancellation.IsCancellationRequested)
        {
            logger.LogWarning("Processing for planning job {JobId} stopped after lease loss.", job.Id);
        }
        catch (PlanningJobLeaseLostException)
        {
            // A checkpoint write found that another worker owns the job; leave it to that owner.
            logger.LogWarning("Processing for planning job {JobId} stopped after lease loss.", job.Id);
        }
        catch (PlanningJobProcessingException exception)
        {
            await RecordFailureAsync(
                execution,
                exception.FailureCode,
                exception.Message,
                exception.IsTransient);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            logger.LogWarning(
                exception,
                "Planning job {JobId} update conflicted with a newer version.",
                job.Id);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected failure while processing planning job {JobId}.", job.Id);
            await RecordFailureAsync(
                execution,
                "UNEXPECTED_PROCESSING_FAILURE",
                "Planning failed because of an unexpected internal error.",
                isTransient: true);
        }
    }

    private async Task<bool> RenewLeaseAsync(
        PlanningJobExecutionContext execution,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var planningJobs = scope.ServiceProvider.GetRequiredService<IPlanningJobs>();
        var job = execution.Job;
        var renewedVersion = await execution.AdvanceAsync(
            version => planningJobs.RenewLeaseAsync(
                job.Id,
                job.LeaseToken,
                version,
                DateTimeOffset.UtcNow,
                _leaseDuration,
                cancellationToken),
            cancellationToken);
        return renewedVersion is not null;
    }

    private async Task<bool> CompleteAsync(
        PlanningJobExecutionContext execution,
        string resultPayload,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var planningJobs = scope.ServiceProvider.GetRequiredService<IPlanningJobs>();
        var job = execution.Job;
        return await execution.FinishAsync(
            version => planningJobs.CompleteAsync(
                job.Id,
                job.LeaseToken,
                version,
                resultPayload,
                DateTimeOffset.UtcNow,
                cancellationToken),
            cancellationToken);
    }

    private async Task RecordFailureAsync(
        PlanningJobExecutionContext execution,
        string failureCode,
        string errorMessage,
        bool isTransient)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var planningJobs = scope.ServiceProvider.GetRequiredService<IPlanningJobs>();
        var job = execution.Job;
        bool recorded;
        try
        {
            recorded = await execution.FinishAsync(
                version => planningJobs.FailAsync(
                    job.Id,
                    job.LeaseToken,
                    version,
                    failureCode,
                    errorMessage,
                    isTransient,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None),
                CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            logger.LogWarning(
                exception,
                "Failure for planning job {JobId} conflicted with a newer version.",
                job.Id);
            return;
        }

        if (!recorded)
        {
            logger.LogWarning(
                "Failure for planning job {JobId} was not recorded because its lease is no longer valid.",
                job.Id);
        }
    }

    private async Task ReleaseAsync(PlanningJobExecutionContext execution)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var planningJobs = scope.ServiceProvider.GetRequiredService<IPlanningJobs>();
        var job = execution.Job;
        bool released;
        try
        {
            released = await execution.FinishAsync(
                version => planningJobs.ReleaseAsync(
                    job.Id,
                    job.LeaseToken,
                    version,
                    CancellationToken.None),
                CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            logger.LogWarning(
                exception,
                "Release of planning job {JobId} at shutdown conflicted with a newer version; its lease will expire instead.",
                job.Id);
            return;
        }

        if (released)
        {
            logger.LogInformation(
                "Planning job {JobId} was returned to the queue at shutdown without consuming an attempt.",
                job.Id);
        }
        else
        {
            logger.LogWarning(
                "Planning job {JobId} was not released at shutdown because this worker no longer owns it.",
                job.Id);
        }
    }

    private static async Task ObserveCancellationAsync(Task processTask)
    {
        try
        {
            await processTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
