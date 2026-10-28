using System.Text.RegularExpressions;
using MaintenancePlanner.Application.Imports;
using MaintenancePlanner.Infrastructure.ManualEvidence;
using UglyToad.PdfPig;

namespace MaintenancePlanner.Infrastructure.Imports;

/// <summary>
/// Imports a text-based PDF manual (FR02). Every page must have extractable text, so each page can be cited, and the
/// manual must contain at least one "Section:" heading, so tasks can cite a section. A "Document ID:" line in the
/// PDF, if present, must match the supplied document ID. Invalid manuals are rejected with every problem reported.
/// </summary>
public sealed class ManualImportService(ImportedManualStore store) : IManualImportService
{
    public const long MaximumPdfBytes = 20 * 1024 * 1024;
    private const string Dataset = "manuals";
    private const string Format = "pdf";

    private static readonly Regex DeclaredDocumentIdPattern = new(
        @"^\s*Document ID:\s*(?<value>\S+)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$", RegexOptions.CultureInvariant);

    public async Task<ImportReport> ImportPdfAsync(
        string? fileName,
        string? documentId,
        string? version,
        Stream content,
        CancellationToken cancellationToken)
    {
        documentId = documentId?.Trim();
        version = version?.Trim();
        var issues = new List<ImportIssue>();

        if (string.IsNullOrEmpty(documentId))
            issues.Add(FileIssue("documentId", "A document ID is required."));
        else if (!ImportIdentifiers.IsValid(documentId))
            issues.Add(FileIssue("documentId", ImportIdentifiers.Rule));
        else if (store.IsBundled(documentId) || store.Contains(documentId))
            issues.Add(FileIssue("documentId", $"A manual with document ID '{documentId}' already exists."));

        if (string.IsNullOrEmpty(version))
            issues.Add(FileIssue("version", "A version is required."));
        else if (!VersionPattern.IsMatch(version))
            issues.Add(FileIssue("version", "The version must be 1-32 letters, digits, '.', '_' or '-'."));

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var pdf = buffer.ToArray();
        if (pdf.Length == 0)
            return Report(fileName, 0, 0, [.. issues, FileIssue(null, "The PDF file is empty.")]);
        if (pdf.Length > MaximumPdfBytes)
            return Report(fileName, 0, 0, [.. issues, FileIssue(null, $"The PDF exceeds {MaximumPdfBytes / (1024 * 1024)} MB.")]);

        IReadOnlyList<PdfManualEvidenceRetriever.PdfPageText> pages;
        try
        {
            using var document = PdfDocument.Open(pdf);
            pages = PdfManualEvidenceRetriever.ReadPages(document, cancellationToken);
        }
        // PdfPig reports malformed, truncated and encrypted files with several exception types
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Report(fileName, 0, 0,
                [.. issues, FileIssue(null, "The file could not be read as a PDF (it may be corrupt or encrypted).")]);
        }

        if (pages.Count == 0)
            issues.Add(FileIssue(null, "The PDF has no pages."));

        foreach (var page in pages.Where(page => string.IsNullOrWhiteSpace(page.Text)))
        {
            issues.Add(new ImportIssue(page.PageNumber, $"page {page.PageNumber}", documentId, null,
                "The page has no extractable text; scanned or image-only pages cannot be cited."));
        }

        var sectionCount = pages
            .SelectMany(page => PdfManualEvidenceRetriever.ToSections(page.PageNumber, page.Text))
            .Count(section => section.Heading is not null);
        if (pages.Count > 0 && sectionCount == 0)
            issues.Add(FileIssue(null, "The manual has no 'Section:' headings, so tasks could not cite a section of it."));

        var declaredIds = pages
            .SelectMany(page => DeclaredDocumentIdPattern.Matches(page.Text).Select(match => (page.PageNumber, match.Groups["value"].Value)))
            .Where(declared => !string.IsNullOrEmpty(documentId) &&
                               !string.Equals(declared.Value, documentId, StringComparison.OrdinalIgnoreCase));
        foreach (var (pageNumber, declared) in declaredIds)
        {
            issues.Add(new ImportIssue(pageNumber, $"page {pageNumber}", documentId, "documentId",
                $"The page declares document ID '{declared}', which does not match '{documentId}'."));
        }

        if (issues.Count > 0)
            return Report(fileName, pages.Count, 0, issues);

        var manual = new ImportedManual(documentId!, version!, fileName, pages.Count, sectionCount, DateTimeOffset.UtcNow);
        if (!await store.TrySaveAsync(manual, pdf, cancellationToken))
        {
            return Report(fileName, pages.Count, 0,
                [FileIssue("documentId", $"A manual with document ID '{documentId}' already exists.")]);
        }

        return Report(fileName, pages.Count, pages.Count, []);
    }

    private static ImportReport Report(string? fileName, int total, int imported, IReadOnlyList<ImportIssue> issues) =>
        new(Dataset, Format, fileName, imported > 0 && issues.Count == 0, total, imported,
            issues.Where(issue => issue.RecordNumber is not null).Select(issue => issue.RecordNumber).Distinct().Count(),
            issues);

    private static ImportIssue FileIssue(string? field, string message) => new(null, "file", null, field, message);
}
