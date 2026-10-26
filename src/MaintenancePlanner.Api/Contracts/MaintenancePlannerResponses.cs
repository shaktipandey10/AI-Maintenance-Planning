namespace MaintenancePlanner.Api.Contracts;

/// <summary>Asset details returned by the maintenance planning API.</summary>
public sealed record AssetResponse(string Id, string PlantId, string Name, string Model, int Priority);

/// <summary>A technician availability interval.</summary>
public sealed record TechnicianAvailabilityResponse(DateTimeOffset AvailableFrom, DateTimeOffset AvailableTo);

/// <summary>Technician skills, rate, and available intervals.</summary>
public sealed record TechnicianResponse(
    string Id,
    string Name,
    decimal HourlyRate,
    IReadOnlyList<string> Skills,
    IReadOnlyList<TechnicianAvailabilityResponse> Availability);

/// <summary>Spare-part stock and reservation quantities.</summary>
public sealed record SparePartResponse(
    string Id,
    string Name,
    string Unit,
    decimal OnHandQuantity,
    decimal ReservedQuantity,
    decimal AvailableQuantity);

/// <summary>A permitted maintenance window.</summary>
public sealed record MaintenanceWindowResponse(
    string Id,
    string Name,
    DateTimeOffset Start,
    DateTimeOffset End,
    string TimeZoneId);

/// <summary>A catalogued task and its scheduling requirements.</summary>
public sealed record MaintenanceTaskResponse(
    string Id,
    string AssetId,
    string Name,
    int DurationMinutes,
    string RequiredSkills,
    int RequiredCrewSize,
    string? RequiredPartId,
    decimal RequiredPartQuantity,
    string? PredecessorTaskId,
    string SourceDocumentId,
    string SourceSection);

/// <summary>Progress for a single spare part within a plan.</summary>
public sealed record TaskProgressResponse(
    string SparePartId,
    decimal RequiredQuantity,
    decimal CompletedQuantity,
    decimal RemainingQuantity);

/// <summary>Overall progress and remaining work for a plan draft.</summary>
public sealed record PlanProgressResponse(
    Guid DraftId,
    decimal TotalRequired,
    decimal CompletedQuantity,
    decimal RemainingQuantity,
    IReadOnlyList<TaskProgressResponse> Tasks);

/// <summary>Result of marking a task complete.</summary>
public sealed record TaskCompletionResponse(
    bool Succeeded,
    string? FailureReason = null);