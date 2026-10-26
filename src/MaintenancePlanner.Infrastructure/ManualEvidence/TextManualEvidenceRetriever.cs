using System.Text.RegularExpressions;
using MaintenancePlanner.Application.Planning;

namespace MaintenancePlanner.Infrastructure.ManualEvidence;

public sealed class TextManualEvidenceRetriever(string filePath) : IManualEvidenceRetriever
{
    private static readonly Regex DocumentIdPattern = new(
        @"^Fictional planning reference:\s*(?<value>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(
        @"^Version:\s*(?<value>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex SectionPattern = new(
        @"^Section:\s*(?<heading>[^\r\n]+)\r?\n(?<excerpt>.*?)(?=^Section:\s|\z)",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex QueryTermPattern = new(
        @"[\p{L}\p{N}][\p{L}\p{N}-]*",
        RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private ManualDocument? _document;

    public async Task<IReadOnlyList<PlanningEvidenceReference>> SearchAsync(
        string query,
        string? documentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var terms = QueryTermPattern.Matches(query)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0)
            return [];

        var document = await GetDocumentAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(documentId) &&
            !string.Equals(document.DocumentId, documentId, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return document.Sections
            .Select((section, index) => new
            {
                Section = section,
                Index = index,
                MatchCount = terms.Count(term =>
                    section.Heading.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    section.Excerpt.Contains(term, StringComparison.OrdinalIgnoreCase))
            })
            .Where(match => match.MatchCount > 0)
            .OrderByDescending(match => match.MatchCount)
            .ThenBy(match => match.Index)
            .Select(match => new PlanningEvidenceReference(
                PlanningEvidenceSourceKind.MaintenanceManual,
                $"{document.DocumentId}:{match.Section.Heading}",
                document.DocumentId,
                document.Version,
                Page: null,
                match.Section.Heading,
                match.Section.Excerpt))
            .ToArray();
    }

    private async Task<ManualDocument> GetDocumentAsync(CancellationToken cancellationToken)
    {
        if (_document is not null)
            return _document;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_document is not null)
                return _document;

            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            var documentId = DocumentIdPattern.Match(content).Groups["value"].Value;
            var version = VersionPattern.Match(content).Groups["value"].Value;
            var sections = SectionPattern.Matches(content)
                .Select(match => new ManualSection(
                    match.Groups["heading"].Value,
                    match.Groups["excerpt"].Value))
                .ToArray();

            _document = new ManualDocument(
                string.IsNullOrWhiteSpace(documentId) ? null : documentId,
                string.IsNullOrWhiteSpace(version) ? null : version,
                sections);
            return _document;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private sealed record ManualDocument(
        string? DocumentId,
        string? Version,
        IReadOnlyList<ManualSection> Sections);

    private sealed record ManualSection(string Heading, string Excerpt);
}
