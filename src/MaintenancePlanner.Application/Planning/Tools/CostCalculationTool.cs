using System.Text.Json;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;

namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>
/// Labour cost and completion time through the application's own <see cref="CandidateCostCalculator"/>, using the
/// stored technician rates. Cost is only calculated for a candidate that passes <see cref="PlanningCandidateValidator"/>,
/// so technician references, durations and crews are judged by the existing rules, not re-implemented here.
/// </summary>
public sealed class CostCalculationTool(
    PlanningCandidateValidator validator,
    CandidateCostCalculator costCalculator,
    IMaintenancePlannerQueries queries) : IPlanningTool
{
    public const string ToolName = "calculate_cost";
    public const string InvalidCandidate = "INVALID_CANDIDATE";

    public string Name => ToolName;

    public string Description =>
        "Calculate the labour cost and completion time of a candidate schedule from the stored technician hourly " +
        "rates. The candidate must first pass schedule validation. The currency is reported as stored by the " +
        "application, which may be UNKNOWN.";

    public JsonElement ParametersSchema => PlanningToolCandidates.ParametersSchema;

    public async Task<PlanningToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var (request, failure) = await PlanningToolCandidates.ReadAsync(arguments, queries, cancellationToken);
        if (failure is not null)
            return failure;

        var validation = await validator.ValidateAsync(request!, cancellationToken);
        if (validation?.Violations is null)
            return PlanningToolResult.Failure(PlanningToolErrors.InvalidToolResult, "Schedule validation returned no result.");

        if (!validation.IsFeasible || validation.Violations.Count > 0)
        {
            return PlanningToolResult.Failure(
                InvalidCandidate,
                "Cost is only calculated for a candidate that passes schedule validation. Violations: " +
                string.Join("; ", validation.Violations.Select(violation => $"{violation.Code}: {violation.Message}")));
        }

        // The same lookup the scheduler gives the calculator: stored rates only, never rates from the model
        var techniciansById = (await queries.GetTechniciansAsync(cancellationToken))
            .ToDictionary(technician => technician.Id, StringComparer.OrdinalIgnoreCase);
        var metrics = costCalculator.CalculateMetrics(request!.Candidate, techniciansById);

        return PlanningToolResult.Success(new CostCalculationToolResult(
            request.MaintenanceWindowId,
            request.AssetIds,
            metrics));
    }
}

/// <summary>The calculator's metrics unchanged, including its warning when a rate could not be used.</summary>
public sealed record CostCalculationToolResult(
    string WindowId,
    IReadOnlyList<string> AssetIds,
    CandidateCostMetrics Metrics);
