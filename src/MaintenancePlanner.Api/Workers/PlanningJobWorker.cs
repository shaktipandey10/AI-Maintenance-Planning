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
        var expectedVersion = job.ConcurrencyVersion;
        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await using var processorScope = scopeFactory.CreateAsyncScope();
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

                var renewedVersion = await RenewLeaseAsync(job, expectedVersion, stoppingToken);
                if (renewedVersion is null)
                {
                    leaseLost = true;
                    await processingCancellation.CancelAsync();
                    break;
                }

                expectedVersion = renewedVersion.Value;
            }

            if (leaseLost)
            {
                await ObserveCancellationAsync(processTask);
                return;
            }

            var result = await processTask;
            var saved = await CompleteAsync(job, expectedVersion, result.ResultPayload, stoppingToken);
            if (!saved)
            {
                logger.LogWarning(
                    "Planning job {JobId} completed after its lease was lost; result was not persisted.",
                    job.Id);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The lease expires and makes the job eligible for recovery after shutdown.
        }
        catch (OperationCanceledException) when (processingCancellation.IsCancellationRequested)
        {
            logger.LogWarning("Processing for planning job {JobId} stopped after lease loss.", job.Id);
        }
        catch (PlanningJobProcessingException exception)
        {
            await RecordFailureAsync(
                job,
                expectedVersion,
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
                job,
                expectedVersion,
                "UNEXPECTED_PROCESSING_FAILURE",
                "Planning failed because of an unexpected internal error.",
                isTransient: true);
        }
    }

    private async Task<int?> RenewLeaseAsync(
        ClaimedPlanningJob job,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var planningJobs = scope.ServiceProvider.GetRequiredService<IPlanningJobs>();
        return await planningJobs.RenewLeaseAsync(
            job.Id,
            job.LeaseToken,
            expectedVersion,
            DateTimeOffset.UtcNow,
            _leaseDuration,
            cancellationToken);
    }

    private async Task<bool> CompleteAsync(
        ClaimedPlanningJob job,
        int expectedVersion,
        string resultPayload,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var planningJobs = scope.ServiceProvider.GetRequiredService<IPlanningJobs>();
        return await planningJobs.CompleteAsync(
            job.Id,
            job.LeaseToken,
            expectedVersion,
            resultPayload,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private async Task RecordFailureAsync(
        ClaimedPlanningJob job,
        int expectedVersion,
        string failureCode,
        string errorMessage,
        bool isTransient)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var planningJobs = scope.ServiceProvider.GetRequiredService<IPlanningJobs>();
        bool recorded;
        try
        {
            recorded = await planningJobs.FailAsync(
                job.Id,
                job.LeaseToken,
                expectedVersion,
                failureCode,
                errorMessage,
                isTransient,
                DateTimeOffset.UtcNow,
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
