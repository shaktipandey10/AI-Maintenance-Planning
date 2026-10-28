namespace MaintenancePlanner.Application.Imports;

/// <summary>A structured input dataset accepted as CSV or JSON (FR02).</summary>
public enum ImportDataset
{
    Assets,
    SpareParts,
    Technicians,
    TechnicianAvailability,
    Tasks,
    MaintenanceHistory
}

public static class ImportDatasets
{
    private static readonly IReadOnlyDictionary<string, ImportDataset> BySlug =
        new Dictionary<string, ImportDataset>(StringComparer.OrdinalIgnoreCase)
        {
            ["assets"] = ImportDataset.Assets,
            ["spare-parts"] = ImportDataset.SpareParts,
            ["technicians"] = ImportDataset.Technicians,
            ["technician-availability"] = ImportDataset.TechnicianAvailability,
            ["tasks"] = ImportDataset.Tasks,
            ["maintenance-history"] = ImportDataset.MaintenanceHistory
        };

    public static IReadOnlyCollection<string> Slugs => BySlug.Keys.ToArray();

    public static bool TryParse(string? slug, out ImportDataset dataset) =>
        BySlug.TryGetValue(slug ?? string.Empty, out dataset);

    public static string Slug(ImportDataset dataset) => BySlug.Single(entry => entry.Value == dataset).Key;
}

public enum ImportFormat
{
    Csv,
    Json,
    Pdf
}

/// <summary>
/// One problem found in an import. RecordNumber is the CSV line number (the header is line 1), the one-based JSON
/// array position or the one-based PDF page; it is null for problems with the file as a whole.
/// </summary>
public sealed record ImportIssue(
    int? RecordNumber,
    string Location,
    string? RecordId,
    string? Field,
    string Message);

/// <summary>
/// Outcome of one import. Imports are all-or-nothing: Imported is true only when every record was valid and saved;
/// otherwise nothing was saved and Issues lists every invalid record with its reason.
/// </summary>
public sealed record ImportReport(
    string Dataset,
    string Format,
    string? FileName,
    bool Imported,
    int TotalRecords,
    int ImportedRecords,
    int InvalidRecords,
    IReadOnlyList<ImportIssue> Issues)
{
    public static ImportReport Rejected(string dataset, string format, string? fileName, string message) =>
        new(dataset, format, fileName, false, 0, 0, 0, [new ImportIssue(null, "file", null, null, message)]);
}

public interface IReferenceDataImportService
{
    Task<ImportReport> ImportAsync(
        ImportDataset dataset,
        ImportFormat format,
        string? fileName,
        Stream content,
        CancellationToken cancellationToken);
}

public interface IManualImportService
{
    Task<ImportReport> ImportPdfAsync(
        string? fileName,
        string? documentId,
        string? version,
        Stream content,
        CancellationToken cancellationToken);
}
