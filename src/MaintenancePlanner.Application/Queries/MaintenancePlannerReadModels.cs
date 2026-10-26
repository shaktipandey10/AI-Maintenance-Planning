namespace MaintenancePlanner.Application.Queries;

public sealed record AssetReadModel(string Id, string PlantId, string Name, string Model, int Priority);

public sealed record TechnicianAvailabilityReadModel(DateTimeOffset AvailableFrom, DateTimeOffset AvailableTo);

public sealed record TechnicianReadModel(
    string Id,
    string Name,
    decimal HourlyRate,
    IReadOnlyList<string> Skills,
    IReadOnlyList<TechnicianAvailabilityReadModel> Availability);

public sealed record SparePartReadModel(
    string Id,
    string Name,
    string Unit,
    decimal OnHandQuantity,
    decimal ReservedQuantity,
    decimal AvailableQuantity);

public sealed record MaintenanceWindowReadModel(
    string Id,
    string Name,
    DateTimeOffset Start,
    DateTimeOffset End,
    string TimeZoneId);

public sealed record MaintenanceTaskReadModel(
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

public sealed record MaintenanceHistoryReadModel(
    string Id,
    string AssetId,
    DateTimeOffset PerformedAt,
    string Summary,
    string Outcome,
    string SourceDocumentId,
    string SourceReference);