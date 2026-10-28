using System.Text.Json;

namespace MaintenancePlanner.Infrastructure.ManualEvidence;

public sealed record ImportedManual(
    string DocumentId,
    string Version,
    string? OriginalFileName,
    int PageCount,
    int SectionCount,
    DateTimeOffset ImportedAtUtc);

/// <summary>
/// Keeps imported PDF manuals in one directory: {documentId}.pdf plus a {documentId}.json manifest. The manifest is
/// written last, so a manual counts as imported only once its PDF is fully in place. Document IDs of the manuals
/// bundled with the application are reserved and cannot be imported again.
/// </summary>
public sealed class ImportedManualStore(string directory, IReadOnlyCollection<string> bundledDocumentIds)
{
    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    public string Directory { get; } = directory;

    public bool IsBundled(string documentId) =>
        bundledDocumentIds.Contains(documentId, StringComparer.OrdinalIgnoreCase);

    public bool Contains(string documentId) => File.Exists(ManifestPath(documentId));

    public string PdfPath(string documentId) => Path.Combine(Directory, $"{documentId}.pdf");

    public IReadOnlyList<ImportedManual> List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];

        return System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(ReadManifest)
            .Where(manual => File.Exists(PdfPath(manual.DocumentId)))
            .OrderBy(manual => manual.DocumentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // An unreadable manifest is reported like an unreadable manual, which planning already treats as missing evidence
    private static ImportedManual ReadManifest(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ImportedManual>(File.ReadAllText(path), ManifestOptions)
                   ?? throw new InvalidDataException($"Imported manual manifest '{path}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Imported manual manifest '{path}' could not be read.", exception);
        }
    }

    /// <summary>Saves a validated manual; returns false without changing anything if its ID is already taken.</summary>
    public async Task<bool> TrySaveAsync(ImportedManual manual, byte[] pdf, CancellationToken cancellationToken)
    {
        await SaveLock.WaitAsync(cancellationToken);
        try
        {
            if (IsBundled(manual.DocumentId) || Contains(manual.DocumentId))
                return false;

            System.IO.Directory.CreateDirectory(Directory);
            var pdfTemp = Path.Combine(Directory, $"{manual.DocumentId}.{Guid.NewGuid():N}.pdf.tmp");
            var manifestTemp = Path.Combine(Directory, $"{manual.DocumentId}.{Guid.NewGuid():N}.json.tmp");
            try
            {
                await File.WriteAllBytesAsync(pdfTemp, pdf, cancellationToken);
                await File.WriteAllTextAsync(
                    manifestTemp,
                    JsonSerializer.Serialize(manual, ManifestOptions),
                    cancellationToken);

                // A PDF left without a manifest by an interrupted save is not an imported manual and is replaced
                File.Move(pdfTemp, PdfPath(manual.DocumentId), overwrite: true);
                File.Move(manifestTemp, ManifestPath(manual.DocumentId), overwrite: false);
                return true;
            }
            finally
            {
                File.Delete(pdfTemp);
                File.Delete(manifestTemp);
            }
        }
        finally
        {
            SaveLock.Release();
        }
    }

    private string ManifestPath(string documentId) => Path.Combine(Directory, $"{documentId}.json");
}
