using System.Collections.Concurrent;
using MaintenancePlanner.Application.Planning;

namespace MaintenancePlanner.Infrastructure.ManualEvidence;

/// <summary>Searches the PDF manuals imported at run time, with the same page and section citations as bundled PDFs.</summary>
public sealed class ImportedManualEvidenceRetriever(ImportedManualStore store) : IManualEvidenceRetriever
{
    private readonly ConcurrentDictionary<string, PdfManualEvidenceRetriever> _retrievers =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<PlanningEvidenceReference>> SearchAsync(
        string query,
        string? documentId,
        CancellationToken cancellationToken)
    {
        var results = new List<PlanningEvidenceReference>();
        foreach (var manual in store.List())
        {
            if (!string.IsNullOrWhiteSpace(documentId) &&
                !string.Equals(manual.DocumentId, documentId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var retriever = _retrievers.GetOrAdd(
                manual.DocumentId,
                _ => new PdfManualEvidenceRetriever(store.PdfPath(manual.DocumentId), manual.DocumentId, manual.Version));
            results.AddRange(await retriever.SearchAsync(query, manual.DocumentId, cancellationToken));
        }

        return results;
    }
}
