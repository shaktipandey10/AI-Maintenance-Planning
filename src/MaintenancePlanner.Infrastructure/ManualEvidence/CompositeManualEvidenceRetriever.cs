using MaintenancePlanner.Application.Planning;

namespace MaintenancePlanner.Infrastructure.ManualEvidence;

public sealed class CompositeManualEvidenceRetriever(
    IReadOnlyList<IManualEvidenceRetriever> retrievers) : IManualEvidenceRetriever
{
    public async Task<IReadOnlyList<PlanningEvidenceReference>> SearchAsync(
        string query,
        string? documentId,
        CancellationToken cancellationToken)
    {
        var results = new List<PlanningEvidenceReference>();
        foreach (var retriever in retrievers)
        {
            results.AddRange(await retriever.SearchAsync(query, documentId, cancellationToken));
        }

        return results;
    }
}
