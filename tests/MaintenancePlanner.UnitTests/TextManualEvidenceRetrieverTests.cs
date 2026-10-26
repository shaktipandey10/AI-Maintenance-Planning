using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Infrastructure.ManualEvidence;

namespace MaintenancePlanner.UnitTests;

public sealed class TextManualEvidenceRetrieverTests
{
    [Fact]
    public async Task SearchAsync_MatchesSectionAndPreservesItsSourceEvidence()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "manuals",
            "MAN-P101-001.txt");
        var sourceText = await File.ReadAllTextAsync(path);
        var retriever = new TextManualEvidenceRetriever(path);

        var result = await retriever.SearchAsync(
            "ROTATING-EQUIPMENT",
            "MAN-P101-001",
            CancellationToken.None);

        var evidence = Assert.Single(result);
        Assert.Equal(PlanningEvidenceSourceKind.MaintenanceManual, evidence.SourceKind);
        Assert.Equal("MAN-P101-001", evidence.DocumentId);
        Assert.Equal("1.0", evidence.Version);
        Assert.Equal("Planning assumptions: Bearing replacement", evidence.Section);
        Assert.Null(evidence.Page);
        Assert.NotNull(evidence.Excerpt);
        Assert.Contains("two technicians, each with ROTATING-EQUIPMENT skill.", evidence.Excerpt);
        Assert.True(sourceText.Contains(evidence.Excerpt, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAsync_NoMatchingTerms_ReturnsNoEvidence()
    {
        var retriever = CreateRetrieverForExistingManual();

        var result = await retriever.SearchAsync(
            "turbine lubrication",
            null,
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_EmptyQueryThrowsArgumentException()
    {
        var retriever = CreateRetrieverForExistingManual();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            retriever.SearchAsync("   ", null, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_MissingManualThrowsFileNotFoundException()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"manual-retriever-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var retriever = new TextManualEvidenceRetriever(Path.Combine(directory, "missing.txt"));

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                retriever.SearchAsync("bearing", null, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    private static TextManualEvidenceRetriever CreateRetrieverForExistingManual()
    {
        return new TextManualEvidenceRetriever(Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "manuals",
            "MAN-P101-001.txt"));
    }

}
