using MaintenancePlanner.Application.Planning.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.Infrastructure.Planning;

public static class PlanningToolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the read-only AI03 planning tools and their dispatcher. They depend on the query services, the
    /// spare-part provider, the candidate validator and the cost calculator, which the host registers.
    /// </summary>
    public static IServiceCollection AddPlanningTools(this IServiceCollection services)
    {
        services.AddScoped<IPlanningTool, StockLookupTool>();
        services.AddScoped<IPlanningTool, TechnicianAvailabilityTool>();
        services.AddScoped<IPlanningTool, TaskCatalogueTool>();
        services.AddScoped<IPlanningTool, ScheduleValidationTool>();
        services.AddScoped<IPlanningTool, CostCalculationTool>();
        // Draft saving: one proposal store per scope (per planning job), shared by the tool and the planning workflow
        services.AddScoped<ModelCandidateProposals>();
        services.AddScoped<IPlanningTool, SaveCandidateTool>();
        services.AddScoped<PlanningToolDispatcher>();
        return services;
    }
}
