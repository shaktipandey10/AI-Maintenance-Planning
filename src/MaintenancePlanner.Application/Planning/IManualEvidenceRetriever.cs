namespace MaintenancePlanner.Application.Planning;

public interface IManualEvidenceRetriever
{
    Task<IReadOnlyList<PlanningEvidenceReference>> SearchAsync(
        string query,
        string? documentId,
        CancellationToken cancellationToken);
}
