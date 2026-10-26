using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Infrastructure.Queries;
using Moq;

namespace MaintenancePlanner.UnitTests;

public sealed class ScenarioAwareSparePartProviderTests
{
    [Fact]
    public async Task GetSparePartsAsync_BaselineWindow_PreservesOriginalInventory()
    {
        var br101 = new SparePartReadModel("BR-101", "Bearing", "each", 1m, 0m, 1m);
        var queries = new Mock<IMaintenancePlannerQueries>();
        queries.Setup(q => q.GetSparePartsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([br101]);

        var provider = new ScenarioAwareSparePartReadModelProvider(queries.Object);
        var result = await provider.GetSparePartsAsync("MW-P101-BASELINE-20261024", CancellationToken.None);

        Assert.Single(result);
        var part = result[0];
        Assert.Equal("BR-101", part.Id);
        Assert.Equal(1m, part.OnHandQuantity);
        Assert.Equal(0m, part.ReservedQuantity);
        Assert.Equal(1m, part.AvailableQuantity);
    }

    [Fact]
    public async Task GetSparePartsAsync_ReducedWindow_AppliesOverrideAndRecalculatesAvailable()
    {
        var br101 = new SparePartReadModel("BR-101", "Bearing", "each", 1m, 0m, 1m);
        var queries = new Mock<IMaintenancePlannerQueries>();
        queries.Setup(q => q.GetSparePartsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([br101]);

        var provider = new ScenarioAwareSparePartReadModelProvider(queries.Object);
        var result = await provider.GetSparePartsAsync("MW-P101-REDUCED-20261024", CancellationToken.None);

        Assert.Single(result);
        var part = result[0];
        Assert.Equal("BR-101", part.Id);
        Assert.Equal(1m, part.OnHandQuantity);
        Assert.Equal(1m, part.ReservedQuantity);
        Assert.Equal(0m, part.AvailableQuantity);
    }

    [Fact]
    public async Task GetSparePartsAsync_ReducedWindowWithMultipleParts_OverrideOnlyAffectsBR101()
    {
        var br101 = new SparePartReadModel("BR-101", "Bearing", "each", 1m, 0m, 1m);
        var other = new SparePartReadModel("OTHER-01", "Other Part", "each", 5m, 1m, 4m);
        var queries = new Mock<IMaintenancePlannerQueries>();
        queries.Setup(q => q.GetSparePartsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([br101, other]);

        var provider = new ScenarioAwareSparePartReadModelProvider(queries.Object);
        var result = await provider.GetSparePartsAsync("MW-P101-REDUCED-20261024", CancellationToken.None);

        Assert.Equal(2, result.Count);
        var br = result.First(p => p.Id == "BR-101");
        var oth = result.First(p => p.Id == "OTHER-01");

        Assert.Equal(0m, br.AvailableQuantity);
        Assert.Equal(4m, oth.AvailableQuantity);
    }

    [Fact]
    public async Task GetSparePartsAsync_NullWindowId_ReturnsBaselineInventory()
    {
        var br101 = new SparePartReadModel("BR-101", "Bearing", "each", 1m, 0m, 1m);
        var queries = new Mock<IMaintenancePlannerQueries>();
        queries.Setup(q => q.GetSparePartsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([br101]);

        var provider = new ScenarioAwareSparePartReadModelProvider(queries.Object);
        var result = await provider.GetSparePartsAsync(null, CancellationToken.None);

        Assert.Single(result);
        var part = result[0];
        Assert.Equal(1m, part.AvailableQuantity);
    }

    [Fact]
    public async Task GetCompatibleSparePartsAsync_ReducedWindow_AppliesOverrideAndRecalculatesAvailable()
    {
        var br101 = new SparePartReadModel("BR-101", "Bearing", "each", 1m, 0m, 1m);
        var queries = new Mock<IMaintenancePlannerQueries>();
        queries.Setup(q => q.GetCompatibleSparePartsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([br101]);

        var provider = new ScenarioAwareSparePartReadModelProvider(queries.Object);
        var result = await provider.GetCompatibleSparePartsAsync("P-101", "MW-P101-REDUCED-20261024", CancellationToken.None);

        Assert.Single(result);
        var part = result[0];
        Assert.Equal("BR-101", part.Id);
        Assert.Equal(1m, part.OnHandQuantity);
        Assert.Equal(1m, part.ReservedQuantity);
        Assert.Equal(0m, part.AvailableQuantity);
    }
}
