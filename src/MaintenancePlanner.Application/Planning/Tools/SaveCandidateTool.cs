using System.Text.Json;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;

namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>
/// AI03 draft saving (project interpretation): saves a model-proposed candidate as an unapproved candidate of the
/// current planning job. The model supplies only task placements; the assets, window, completed work and releasable
/// parts come from the job (<see cref="ModelCandidateContext"/>). The candidate must pass the application's
/// <see cref="PlanningCandidateValidator"/>, and its cost is calculated from stored rates. Saving never creates a draft,
/// approves, reserves stock, locks tasks or writes business actions.
/// </summary>
public sealed class SaveCandidateTool(
    ModelCandidateProposals proposals,
    PlanningCandidateValidator validator,
    CandidateCostCalculator costCalculator,
    IMaintenancePlannerQueries queries) : IPlanningTool
{
    public const string ToolName = "save_candidate";
    public const string NoPlanningContext = "NO_PLANNING_CONTEXT";
    public const string ProposalLimit = "PROPOSAL_LIMIT";

    public const string Rationale =
        "Proposed by the planning model with save_candidate; validated by the application's candidate validator, " +
        "with labour cost from stored technician rates.";

    private static readonly string[] AllowedArguments = ["placements"];

    public string Name => ToolName;

    public string Description =>
        "Save a complete candidate schedule for this request's selected assets and maintenance window as an unapproved " +
        "candidate of the planning job. Give only the task placements. The application validates the candidate and " +
        "calculates its cost; saving does not approve, reserve or lock anything.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "placements": {
              "type": "array",
              "description": "One placement per scheduled task.",
              "items": {
                "type": "object",
                "properties": {
                  "taskId": { "type": "string" },
                  "start": { "type": "string", "description": "ISO 8601 with an explicit offset." },
                  "end": { "type": "string", "description": "ISO 8601 with an explicit offset." },
                  "technicianIds": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["taskId", "start", "end", "technicianIds"],
                "additionalProperties": false
              }
            }
          },
          "required": ["placements"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public async Task<PlanningToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var context = proposals.Context;
        if (context is null)
            return PlanningToolResult.Failure(NoPlanningContext, "Candidates can only be saved while a planning job is being interpreted.");

        var reader = PlanningToolArguments.Read(arguments, AllowedArguments);
        var placements = reader.RequiredPlacements("placements");
        if (reader.Error is not null)
            return PlanningToolResult.Failure(PlanningToolErrors.InvalidArguments, reader.Error);

        var candidate = new PlanningCandidate(placements!, Rationale, []);
        var validation = await validator.ValidateAsync(
            new PlanningCandidateValidationRequest(context.AssetIds, context.WindowId, candidate)
            {
                CompletedTaskIds = context.CompletedTaskIds,
                ReleasableReservedQuantities = context.ReleasableReservedQuantities
            },
            cancellationToken);
        if (validation?.Violations is null)
            return PlanningToolResult.Failure(PlanningToolErrors.InvalidToolResult, "Schedule validation returned no result.");

        if (!validation.IsFeasible || validation.Violations.Count > 0)
        {
            return PlanningToolResult.Failure(
                CostCalculationTool.InvalidCandidate,
                "The candidate was not saved because it fails schedule validation. Violations: " +
                string.Join("; ", validation.Violations.Select(violation => $"{violation.Code}: {violation.Message}")));
        }

        var techniciansById = (await queries.GetTechniciansAsync(cancellationToken))
            .ToDictionary(technician => technician.Id, StringComparer.OrdinalIgnoreCase);
        var metrics = costCalculator.CalculateMetrics(candidate, techniciansById);
        var (number, alreadySaved) = proposals.Add(candidate with
        {
            TotalLabourCostAmount = metrics.TotalLabourCostAmount,
            TotalLabourCostCurrency = metrics.TotalLabourCostCurrency,
            CompletionTimeMinutes = metrics.CompletionTimeMinutes,
            CostComparabilityWarning = metrics.CostWarning
        });
        if (number is null)
        {
            return PlanningToolResult.Failure(
                ProposalLimit,
                $"At most {ModelCandidateProposals.MaximumProposals} different candidates can be saved for one planning job.");
        }

        return PlanningToolResult.Success(new SaveCandidateToolResult(
            number.Value,
            alreadySaved,
            context.WindowId,
            context.AssetIds,
            metrics,
            "Saved as an unapproved candidate of this planning job. It is added to the job's candidates only if it covers " +
            "exactly the job's final task scope. Saving does not approve, reserve or lock anything."));
    }
}

public sealed record SaveCandidateToolResult(
    int ProposalNumber,
    bool AlreadySaved,
    string WindowId,
    IReadOnlyList<string> AssetIds,
    CandidateCostMetrics Metrics,
    string Note);
