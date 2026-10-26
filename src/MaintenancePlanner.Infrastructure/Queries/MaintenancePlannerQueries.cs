using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Queries;

public sealed class MaintenancePlannerQueries(MaintenancePlannerDbContext dbContext)
    : IMaintenancePlannerQueries
{
    public async Task<IReadOnlyList<AssetReadModel>> GetAssetsAsync(CancellationToken cancellationToken) =>
        await dbContext.Assets.AsNoTracking()
            .OrderBy(asset => asset.Id)
            .Select(asset => new AssetReadModel(asset.Id, asset.PlantId, asset.Name, asset.Model, asset.Priority))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TechnicianReadModel>> GetTechniciansAsync(CancellationToken cancellationToken)
    {
        var technicians = await dbContext.Technicians.AsNoTracking()
            .AsSplitQuery()
            .Include(technician => technician.TechnicalSkills)
            .Include(technician => technician.TechnicianAvailabilities)
            .OrderBy(technician => technician.Id)
            .ToListAsync(cancellationToken);

        return technicians.Select(technician => new TechnicianReadModel(
            technician.Id,
            technician.Name,
            technician.HourlyRate,
            technician.TechnicalSkills.Select(skill => skill.SkillCode).Order().ToArray(),
            technician.TechnicianAvailabilities
                .OrderBy(availability => availability.AvailableFrom)
                .Select(availability => new TechnicianAvailabilityReadModel(
                    availability.AvailableFrom,
                    availability.AvailableTo))
                .ToArray()))
            .ToArray();
    }

    public async Task<IReadOnlyList<SparePartReadModel>> GetSparePartsAsync(CancellationToken cancellationToken) =>
        await dbContext.SpareParts.AsNoTracking()
            .OrderBy(part => part.Id)
            .Select(part => new SparePartReadModel(
                part.Id,
                part.Name,
                part.Unit,
                part.OnHandQuantity,
                part.ReservedQuantity,
                part.OnHandQuantity - part.ReservedQuantity))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MaintenanceWindowReadModel>> GetMaintenanceWindowsAsync(
        CancellationToken cancellationToken)
    {
        var windows = await dbContext.MaintenanceWindows.AsNoTracking()
            .Select(window => new MaintenanceWindowReadModel(
                window.Id,
                window.Name,
                window.Start,
                window.End,
                window.TimeZoneId))
            .ToListAsync(cancellationToken);

        return windows.OrderBy(window => window.Start).ToArray();
    }

    public async Task<IReadOnlyList<MaintenanceTaskReadModel>?> GetTaskCatalogueAsync(
        string assetId,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.Assets.AsNoTracking().AnyAsync(asset => asset.Id == assetId, cancellationToken))
            return null;

        var tasks = await dbContext.MaintenanceTaskDefinitions.AsNoTracking()
            .Where(task => task.AssetId == assetId)
            .ToListAsync(cancellationToken);

        var orderedTasks = new List<MaintenanceTaskReadModel>(tasks.Count);
        var remainingTasks = tasks.ToDictionary(task => task.Id);
        string? previousTaskId = null;

        while (remainingTasks.Count > 0)
        {
            var nextTask = remainingTasks.Values
                .Where(task => task.PredecessorTaskId == previousTaskId)
                .OrderBy(task => task.Id)
                .FirstOrDefault();

            if (nextTask is null)
                nextTask = remainingTasks.Values.OrderBy(task => task.Id).First();

            orderedTasks.Add(new MaintenanceTaskReadModel(
                nextTask.Id,
                nextTask.AssetId,
                nextTask.Name,
                nextTask.DurationMinutes,
                nextTask.RequiredSkills,
                nextTask.RequiredCrewSize,
                nextTask.RequiredPartId,
                nextTask.RequiredPartQuantity,
                nextTask.PredecessorTaskId,
                nextTask.SourceDocumentId,
                nextTask.SourceSection));
            previousTaskId = nextTask.Id;
            remainingTasks.Remove(nextTask.Id);
        }

        return orderedTasks;
    }

    public async Task<IReadOnlyList<MaintenanceHistoryReadModel>> GetMaintenanceHistoryAsync(
        IReadOnlyCollection<string> assetIds,
        CancellationToken cancellationToken)
    {
        if (assetIds.Count == 0)
            return [];

        var historyRecords = await dbContext.MaintenanceHistoryRecords.AsNoTracking()
            .Where(record => assetIds.Contains(record.AssetId))
            .ToListAsync(cancellationToken);

        return historyRecords
            .OrderByDescending(record => record.PerformedAt)
            .ThenBy(record => record.Id)
            .Select(record => new MaintenanceHistoryReadModel(
                record.Id,
                record.AssetId,
                record.PerformedAt,
                record.Summary,
                record.Outcome,
                record.SourceDocumentId,
                record.SourceReference))
            .ToArray();
    }

    public async Task<IReadOnlyList<SparePartReadModel>> GetCompatibleSparePartsAsync(
        string assetId,
        CancellationToken cancellationToken) =>
        await dbContext.SpareParts.AsNoTracking()
            .Where(part => part.CompatibleAssets.Any(asset => asset.Id == assetId))
            .OrderBy(part => part.Id)
            .Select(part => new SparePartReadModel(
                part.Id,
                part.Name,
                part.Unit,
                part.OnHandQuantity,
                part.ReservedQuantity,
                part.OnHandQuantity - part.ReservedQuantity))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LockedTaskAssignmentReadModel>> GetLockedTaskAssignmentsAsync(
        Guid draftId,
        CancellationToken cancellationToken) =>
        await dbContext.LockedTaskAssignments.AsNoTracking()
            .Where(lock_ => lock_.DraftId == draftId)
            .OrderBy(lock_ => lock_.TaskId)
            .Select(lock_ => new LockedTaskAssignmentReadModel(lock_.TaskId, lock_.LockedTechnicianId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PlanningJobReadModel>> GetJobRevisionChainAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var allJobs = await dbContext.PlanningJobs.AsNoTracking()
            .Select(j => new JobSnapshot(
                j.Id,
                j.OriginalJobId,
                j.Status,
                j.AttemptCount,
                j.MaxAttempts,
                j.CurrentStep,
                j.ResultPayload,
                j.ClarificationPayload,
                j.FailureCode,
                j.CreatedAtUtc,
                j.StartedAtUtc,
                j.CompletedAtUtc,
                j.ErrorMessage,
                j.RequestPayload))
            .ToListAsync(cancellationToken);

        var requestedJob = allJobs.FirstOrDefault(j => j.Id == jobId);
        if (requestedJob is null)
            return [];

        var rootId = FindRoot(requestedJob.Id, allJobs);
        var chainJobIds = new HashSet<Guid>();
        CollectDescendants(rootId, allJobs, chainJobIds);

        var chainJobs = allJobs
            .Where(j => chainJobIds.Contains(j.Id))
            .OrderBy(j => j.CreatedAtUtc)
            .ThenBy(j => j.OriginalJobId.HasValue ? 1 : 0)
            .ThenBy(j => j.Id)
            .Select(j => new PlanningJobReadModel(
                j.Id,
                j.Status,
                j.AttemptCount,
                j.MaxAttempts,
                j.CurrentStep,
                j.ResultPayload,
                j.ClarificationPayload,
                j.FailureCode,
                j.CreatedAtUtc,
                j.StartedAtUtc,
                j.CompletedAtUtc,
                j.ErrorMessage,
                j.RequestPayload,
                j.OriginalJobId))
            .ToList();

        return chainJobs;
    }

    private static Guid FindRoot(Guid jobId, List<JobSnapshot> allJobs)
    {
        var visited = new HashSet<Guid>();
        var current = allJobs.FirstOrDefault(j => j.Id == jobId);
        if (current is null)
            return jobId;

        while (current.OriginalJobId.HasValue)
        {
            if (!visited.Add(current.Id))
                break;
            var parent = allJobs.FirstOrDefault(j => j.Id == current.OriginalJobId);
            if (parent is null)
                break;
            current = parent;
        }

        return current.Id;
    }

    private static void CollectDescendants(Guid rootId, List<JobSnapshot> allJobs, HashSet<Guid> result)
    {
        if (!result.Add(rootId))
            return;
        var descendants = allJobs.Where(j => j.OriginalJobId == rootId).ToList();
        foreach (var descendant in descendants)
        {
            CollectDescendants(descendant.Id, allJobs, result);
        }
    }

    private sealed record JobSnapshot(
        Guid Id,
        Guid? OriginalJobId,
        Domain.Entities.PlanningJobStatus Status,
        int AttemptCount,
        int MaxAttempts,
        string CurrentStep,
        string? ResultPayload,
        string? ClarificationPayload,
        string? FailureCode,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        string? ErrorMessage,
        string RequestPayload);
}