using MaintenancePlanner.Application.PlanningJobs;

namespace MaintenancePlanner.Application.Queries;

public interface IMaintenancePlannerQueries
{
    Task<IReadOnlyList<AssetReadModel>> GetAssetsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TechnicianReadModel>> GetTechniciansAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SparePartReadModel>> GetSparePartsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MaintenanceWindowReadModel>> GetMaintenanceWindowsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MaintenanceTaskReadModel>?> GetTaskCatalogueAsync(
        string assetId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<MaintenanceHistoryReadModel>> GetMaintenanceHistoryAsync(
        IReadOnlyCollection<string> assetIds,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SparePartReadModel>> GetCompatibleSparePartsAsync(
        string assetId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LockedTaskAssignmentReadModel>> GetLockedTaskAssignmentsAsync(
        Guid draftId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PlanningJobReadModel>> GetJobRevisionChainAsync(
        Guid jobId,
        CancellationToken cancellationToken);
}

public sealed record LockedTaskAssignmentReadModel(
    string TaskId,
    string LockedTechnicianId);