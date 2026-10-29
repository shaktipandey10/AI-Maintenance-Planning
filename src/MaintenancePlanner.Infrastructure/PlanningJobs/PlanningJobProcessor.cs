using System.Text.Json;
using MaintenancePlanner.Application.Locks;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;
using Microsoft.EntityFrameworkCore;

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

        // FR08: tasks completed on the original job's approved draft are not scheduled again in its replacement.
        // Unlike locks, a failure here is not ignored, since planning would then repeat completed work.
        if (dbContext is not null)
        {
            var originalJobId = await dbContext.PlanningJobs
                .Where(planningJob => planningJob.Id == jobId)
                .Select(planningJob => planningJob.OriginalJobId)
                .SingleOrDefaultAsync(cancellationToken);
            if (originalJobId is { } replacedJobId)
            {
                var completedTaskIds = await dbContext.Drafts
                    .Where(draft => draft.PlanningJobId == replacedJobId && draft.Status == DraftStatus.Approved)
                    .Join(dbContext.DraftTaskProgress, draft => draft.Id, progress => progress.DraftId, (_, progress) => progress)
                    .Where(progress => progress.Status == TaskProgressStatus.Completed)
                    .Select(progress => progress.TaskId)
                    .ToArrayAsync(cancellationToken);

                // FR07: the replacement may plan with the parts the replaced draft holds unconsumed, because approving
                // the replacement releases exactly those reservations (DraftApprovalService.ReservePartsAsync). Only
                // read here: nothing is released until that approval commits.
                var releasableReservations = await dbContext.Drafts
                    .Where(draft => draft.PlanningJobId == replacedJobId && draft.Status == DraftStatus.Approved)
                    .SelectMany(draft => draft.Reservations)
                    .Where(reservation => reservation.Status == ReservationStatus.Confirmed && reservation.CompletedAtUtc == null)
                    .Select(reservation => new { reservation.SparePartId, reservation.QuantityReserved })
                    .ToListAsync(cancellationToken);
                // FR08: tasks in progress on the replaced draft keep their approved placement in the replacement
                var inProgressPlacements = await ReadInProgressPlacementsAsync(replacedJobId, cancellationToken);

                request = request with
                {
                    CompletedTaskIds = completedTaskIds,
                    InProgressPlacements = inProgressPlacements,
                    ReleasableReservedQuantities = releasableReservations
                        .GroupBy(reservation => reservation.SparePartId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group => group.Key,
                            group => group.Sum(reservation => reservation.QuantityReserved),
                            StringComparer.OrdinalIgnoreCase)
                };
            }
        }

        PlanningWorkflowResult result;
        try
        {
            if (string.IsNullOrEmpty(job.CheckpointPayload))
            {
                result = await workflow.ExecuteAsync(request, cancellationToken, locks);
            }
            else
            {
                // A checkpoint is only used once validated for this job and request. An unusable one fails the job
                // permanently instead of being retried or silently ignored.
                if (!PlanningInterpretationCheckpoint.TryRead(
                        job.CheckpointPayload,
                        job.Id,
                        job.RequestPayload,
                        out var checkpointInterpretation,
                        out var checkpointError))
                {
                    throw new PlanningJobProcessingException(
                        "INVALID_CHECKPOINT",
                        $"The saved planning checkpoint cannot be used: {checkpointError}.");
                }

                result = await workflow.ResumeFromCheckpointAsync(request, checkpointInterpretation, cancellationToken, locks);
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

    /// <summary>
    /// The approved placements, from the replaced draft's snapshot, of its tasks in progress. A task in progress
    /// without a readable placement fails the job rather than being dropped from, or rescheduled in, the replacement.
    /// </summary>
    private async Task<IReadOnlyList<PlanningTaskPlacement>> ReadInProgressPlacementsAsync(
        Guid replacedJobId,
        CancellationToken cancellationToken)
    {
        var replacedDraft = await dbContext!.Drafts
            .Where(draft => draft.PlanningJobId == replacedJobId && draft.Status == DraftStatus.Approved)
            .Select(draft => new { draft.Id, draft.ApprovedCandidatePayload })
            .SingleOrDefaultAsync(cancellationToken);
        if (replacedDraft is null)
            return [];

        var inProgressTaskIds = await dbContext.DraftTaskProgress
            .Where(progress => progress.DraftId == replacedDraft.Id && progress.Status == TaskProgressStatus.InProgress)
            .Select(progress => progress.TaskId)
            .ToListAsync(cancellationToken);
        if (inProgressTaskIds.Count == 0)
            return [];

        List<PlanningTaskPlacement>? approvedPlacements = null;
        try
        {
            approvedPlacements = JsonSerializer
                .Deserialize<ApprovedSnapshot>(replacedDraft.ApprovedCandidatePayload ?? string.Empty, JsonOptions)?
                .TaskPlacements?
                .Where(placement => placement?.TaskId is not null && placement.TechnicianIds is not null)
                .Select(placement => new PlanningTaskPlacement(
                    placement!.TaskId!, placement.Start, placement.End, placement.TechnicianIds!))
                .ToList();
        }
        catch (JsonException)
        {
            // Reported below as an unavailable placement
        }

        return inProgressTaskIds
            .Select(taskId => approvedPlacements?.FirstOrDefault(placement =>
                    string.Equals(placement.TaskId, taskId, StringComparison.OrdinalIgnoreCase)) ??
                throw new PlanningJobProcessingException(
                    "IN_PROGRESS_PLACEMENT_UNAVAILABLE",
                    $"Task {taskId} is in progress on the approved plan being replaced, but its approved placement cannot be read."))
            .ToList();
    }

    private sealed record ApprovedSnapshot(IReadOnlyList<ApprovedPlacement?>? TaskPlacements);

    private sealed record ApprovedPlacement(
        string? TaskId,
        DateTimeOffset Start,
        DateTimeOffset End,
        IReadOnlyList<string>? TechnicianIds);

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
