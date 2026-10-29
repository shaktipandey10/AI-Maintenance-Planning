using System.Text.Json;
using MaintenancePlanner.Application.Queries;

namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>
/// Technician skills and declared availability. This is not authoritative free time: time already committed to other
/// approved plans is not subtracted. Approval checks committed time; every result states this limitation.
/// </summary>
public sealed class TechnicianAvailabilityTool(IMaintenancePlannerQueries queries) : IPlanningTool
{
    public const string ToolName = "get_technician_availability";

    public const string Limitation =
        "Declared availability only. Time already committed to other approved plans is not subtracted; " +
        "approval rejects a plan that overlaps committed technician time.";

    private static readonly string[] AllowedArguments = ["technicianIds", "windowId"];

    public string Name => ToolName;

    public string Description =>
        "Get technicians' skills and declared availability intervals, optionally for specific technicians or only " +
        "the intervals overlapping a maintenance window. " + Limitation;

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "technicianIds": { "type": "array", "items": { "type": "string" }, "description": "Only these technician IDs." },
            "windowId": { "type": "string", "description": "Only availability intervals overlapping this maintenance window." }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public async Task<PlanningToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var reader = PlanningToolArguments.Read(arguments, AllowedArguments);
        var technicianIds = reader.OptionalIdentifierList("technicianIds");
        var windowId = reader.OptionalIdentifier("windowId");
        if (reader.Error is not null)
            return PlanningToolResult.Failure(PlanningToolErrors.InvalidArguments, reader.Error);

        MaintenanceWindowReadModel? window = null;
        if (windowId is not null)
        {
            window = (await queries.GetMaintenanceWindowsAsync(cancellationToken))
                .FirstOrDefault(candidate => string.Equals(candidate.Id, windowId, StringComparison.OrdinalIgnoreCase));
            if (window is null)
                return PlanningToolResult.Failure(PlanningToolErrors.UnknownWindow, $"Maintenance window {windowId} does not exist.");
        }

        var technicians = await queries.GetTechniciansAsync(cancellationToken);
        if (technicianIds is not null)
        {
            var missing = technicianIds.FirstOrDefault(technicianId =>
                !technicians.Any(technician => string.Equals(technician.Id, technicianId, StringComparison.OrdinalIgnoreCase)));
            if (missing is not null)
                return PlanningToolResult.Failure(PlanningToolErrors.UnknownTechnician, $"Technician {missing} does not exist.");

            technicians = technicians
                .Where(technician => technicianIds.Contains(technician.Id, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        var invalid = technicians.FirstOrDefault(technician =>
            string.IsNullOrWhiteSpace(technician.Id) ||
            technician.Availability.Any(interval => interval.AvailableFrom >= interval.AvailableTo));
        if (invalid is not null)
        {
            return PlanningToolResult.Failure(
                PlanningToolErrors.InvalidToolResult,
                $"The availability record of technician {invalid.Id} is inconsistent and cannot be used.");
        }

        var items = technicians
            .Select(technician => new TechnicianAvailabilityItem(
                technician.Id,
                technician.Name,
                technician.Skills,
                technician.Availability
                    .Where(interval => window is null ||
                                       (interval.AvailableFrom < window.End && window.Start < interval.AvailableTo))
                    .ToArray()))
            .ToArray();
        return PlanningToolResult.Success(new TechnicianAvailabilityToolResult(window?.Id, items, Limitation));
    }
}

public sealed record TechnicianAvailabilityItem(
    string Id,
    string Name,
    IReadOnlyList<string> Skills,
    IReadOnlyList<TechnicianAvailabilityReadModel> DeclaredAvailability);

public sealed record TechnicianAvailabilityToolResult(
    string? WindowId,
    IReadOnlyList<TechnicianAvailabilityItem> Technicians,
    string Limitation);
