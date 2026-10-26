using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Infrastructure.ManualEvidence;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Writer;

namespace MaintenancePlanner.UnitTests;

public sealed class PdfManualEvidenceRetrieverTests
{
    [Fact]
    public async Task SearchAsync_ReturnsExactSectionWithOneBasedPageAndConfiguredMetadata()
    {
        var path = GetFixturePath("MAN-P101-PLANNING-001.pdf");
        var sourcePageText = ReadPdfPageText(path, 2);
        var retriever = CreateP101Retriever(path);

        var result = await retriever.SearchAsync(
            "BR-101",
            "MAN-P101-PLANNING-001",
            CancellationToken.None);

        var evidence = Assert.Single(result);
        Assert.Equal(PlanningEvidenceSourceKind.MaintenanceManual, evidence.SourceKind);
        Assert.Equal("MAN-P101-PLANNING-001", evidence.DocumentId);
        Assert.Equal("1.0", evidence.Version);
        Assert.Equal(2, evidence.Page);
        Assert.Equal("Bearing replacement planning assumptions", evidence.Section);
        Assert.NotNull(evidence.Excerpt);
        Assert.Contains("Required part: one BR-101 bearing.", evidence.Excerpt);
        Assert.True(sourcePageText.Contains(evidence.Excerpt, StringComparison.Ordinal));
        Assert.StartsWith("Section:", evidence.Excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_RetrievesEvidenceFromBothPdfFixtures()
    {
        var p101 = await CreateP101Retriever(GetFixturePath("MAN-P101-PLANNING-001.pdf"))
            .SearchAsync("bearing replacement", null, CancellationToken.None);
        var p102 = await new PdfManualEvidenceRetriever(
                GetFixturePath("MAN-P102-SCOPE-001.pdf"),
                "MAN-P102-SCOPE-001",
                "1.0")
            .SearchAsync("inspection only", null, CancellationToken.None);

        Assert.Contains(p101, evidence => evidence.DocumentId == "MAN-P101-PLANNING-001");
        Assert.Contains(p102, evidence => evidence.DocumentId == "MAN-P102-SCOPE-001");
        Assert.Contains(p102, evidence =>
            evidence.Section == "Scope limitation" &&
            evidence.Excerpt!.Contains("Do not substitute this scope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAsync_NoMatchingTerms_ReturnsEmpty()
    {
        var result = await CreateP101Retriever(GetFixturePath("MAN-P101-PLANNING-001.pdf"))
            .SearchAsync("turbine lubrication", null, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_MissingPdfThrowsFileNotFoundException()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var resultPath = Path.Combine(temporaryDirectory.Path, "missing.pdf");
        var retriever = CreateP101Retriever(resultPath);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            retriever.SearchAsync("bearing", null, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_UnreadablePdfThrowsInvalidDataException()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "unreadable.pdf");
        await File.WriteAllTextAsync(path, "not a PDF");
        var retriever = CreateP101Retriever(path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            retriever.SearchAsync("bearing", null, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_PdfWithoutExtractableTextThrowsInvalidDataException()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "empty.pdf");
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        await File.WriteAllBytesAsync(path, builder.Build());
        var retriever = CreateP101Retriever(path);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            retriever.SearchAsync("bearing", null, CancellationToken.None));

        Assert.Contains("no extractable text", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Scanned or image-only PDFs", exception.Message, StringComparison.Ordinal);
    }

    private static PdfManualEvidenceRetriever CreateP101Retriever(string path) =>
        new(path, "MAN-P101-PLANNING-001", "1.0");

    private static string GetFixturePath(string filename) =>
        Path.Combine(AppContext.BaseDirectory, "data", "manuals", filename);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"pdf-evidence-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static string ReadPdfPageText(string filePath, int pageNumber)
    {
        using var document = PdfDocument.Open(filePath);
        return ContentOrderTextExtractor.GetText(document.GetPage(pageNumber));
    }
}
