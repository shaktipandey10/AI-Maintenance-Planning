using System.Text.RegularExpressions;
using MaintenancePlanner.Application.Planning;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MaintenancePlanner.Infrastructure.ManualEvidence;

public sealed class PdfManualEvidenceRetriever(
    string filePath,
    string documentId,
    string version) : IManualEvidenceRetriever
{
    private static readonly Regex SectionPattern = new(
        @"^Section:\s*(?<heading>[^\r\n]+)\r?\n(?<excerpt>.*?)(?=^Section:\s|\z)",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex QueryTermPattern = new(
        @"[\p{L}\p{N}][\p{L}\p{N}-]*",
        RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IReadOnlyList<PdfEvidenceSection>? _sections;

    public async Task<IReadOnlyList<PlanningEvidenceReference>> SearchAsync(
        string query,
        string? requestedDocumentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (!string.IsNullOrWhiteSpace(requestedDocumentId) &&
            !string.Equals(documentId, requestedDocumentId, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var terms = QueryTermPattern.Matches(query)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0)
            return [];

        var sections = await GetSectionsAsync(cancellationToken);
        return sections
            .Select((section, index) => new
            {
                Section = section,
                Index = index,
                MatchCount = terms.Count(term =>
                    section.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
            })
            .Where(match => match.MatchCount > 0)
            .OrderByDescending(match => match.MatchCount)
            .ThenBy(match => match.Index)
            .Select(match => new PlanningEvidenceReference(
                PlanningEvidenceSourceKind.MaintenanceManual,
                $"{documentId}:page:{match.Section.PageNumber}" +
                (match.Section.Heading is null ? string.Empty : $":{match.Section.Heading}"),
                documentId,
                version,
                match.Section.PageNumber,
                match.Section.Heading,
                match.Section.Text))
            .ToArray();
    }

    private async Task<IReadOnlyList<PdfEvidenceSection>> GetSectionsAsync(
        CancellationToken cancellationToken)
    {
        if (_sections is not null)
            return _sections;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_sections is not null)
                return _sections;

            if (!File.Exists(filePath))
                throw new FileNotFoundException("The configured PDF manual was not found.", filePath);

            var loadedSections = await Task.Run(() =>
            {
                using var document = PdfDocument.Open(filePath);
                var pages = new List<PdfEvidenceSection>();
                foreach (var page in document.GetPages())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pageText = ContentOrderTextExtractor.GetText(page);
                    if (string.IsNullOrWhiteSpace(pageText))
                        continue;

                    var sectionMatches = SectionPattern.Matches(pageText);
                    if (sectionMatches.Count == 0)
                    {
                        pages.Add(new PdfEvidenceSection(page.Number, null, pageText));
                        continue;
                    }

                    foreach (Match match in sectionMatches)
                    {
                        pages.Add(new PdfEvidenceSection(
                            page.Number,
                            match.Groups["heading"].Value,
                            match.Value));
                    }
                }

                if (pages.Count == 0)
                {
                    throw new InvalidDataException(
                        $"PDF manual '{documentId}' contains no extractable text. " +
                        "Scanned or image-only PDFs are not searchable.");
                }

                return pages;
            }, cancellationToken);

            _sections = loadedSections;
            return _sections;
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PdfDocumentFormatException)
        {
            throw new InvalidDataException(
                $"PDF manual '{documentId}' could not be read as a valid PDF.",
                exception);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private sealed record PdfEvidenceSection(int PageNumber, string? Heading, string Text);
}
