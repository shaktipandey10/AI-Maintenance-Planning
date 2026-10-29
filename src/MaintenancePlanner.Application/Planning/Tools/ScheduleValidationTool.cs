using System.Text.Json;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;

namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>
/// Schedule validation through the application's own <see cref="PlanningCandidateValidator"/>. The tool adds no rules:
/// it passes the candidate to the validator and returns its feasibility and violations unchanged.
/// </summary>
public sealed class ScheduleValidationTool(
    PlanningCandidateValidator validator,
    IMaintenancePlannerQueries queries) : IPlanningTool
{
    public const string ToolName = "validate_schedule";

    public static readonly IReadOnlyList<string> Limitations =
    [
        "Technician time already committed to other approved plans is not checked; approval rejects such overlaps.",
        "Work completed on, and parts held by, an approved plan being replaced are not taken into account.",
        "A feasible result is advisory: approval revalidates the selected plan against current data."
    ];

    public string Name => ToolName;

    public string Description =>
        "Validate a candidate schedule against the maintenance window, task catalogues, durations, predecessors, " +
        "technician skills, declared availability and overlaps, crew sizes, part compatibility and stock. " +
        string.Join(" ", Limitations);

    public JsonElement ParametersSchema => PlanningToolCandidates.ParametersSchema;

    public async Task<PlanningToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var (request, failure) = await PlanningToolCandidates.ReadAsync(arguments, queries, cancellationToken);
        if (failure is not null)
            return failure;

        var validation = await validator.ValidateAsync(request!, cancellationToken);
        if (validation?.Violations is null)
        {
            return PlanningToolResult.Failure(
                PlanningToolErrors.InvalidToolResult,
                "Schedule validation returned no result.");
        }

        return PlanningToolResult.Success(new ScheduleValidationToolResult(
            request!.MaintenanceWindowId,
            request.AssetIds,
            validation.IsFeasible,
            validation.Violations,
            Limitations));
    }
}

public sealed record ScheduleValidationToolResult(
    string WindowId,
    IReadOnlyList<string> AssetIds,
    bool IsFeasible,
    IReadOnlyList<PlanningValidationViolation> Violations,
    IReadOnlyList<string> Limitations);
