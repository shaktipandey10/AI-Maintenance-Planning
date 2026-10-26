using MaintenancePlanner.Application.Queries;

namespace MaintenancePlanner.Application.Planning;

public interface IPlanningRequestInterpreter
{
    Task<PlanningInterpretation> InterpretAsync(
        PlanningRequest request,
        IReadOnlyList<AssetReadModel> authoritativeAssets,
        IReadOnlyList<MaintenanceTaskReadModel> authoritativeTasks,
        CancellationToken cancellationToken);
}
