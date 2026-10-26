using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Seeding;

namespace MaintenancePlanner.UnitTests;

public sealed class P101FeasibilityTests
{
    [Fact]
    public void Validate_BaselineInputs_ArePotentiallyFeasible()
    {
        var seed = P101AnchorSeed.Create();
        var result = new MaintenanceFeasibilityValidator().Validate(
            seed.Tasks,
            seed.MaintenanceWindows.Single(window => window.Id == "MW-P101-BASELINE-20261024"),
            [seed.SparePart],
            seed.Technicians);

        Assert.True(result.IsPotentiallyFeasible);
        Assert.Empty(result.Violations);
        Assert.Equal(360, seed.Tasks.Sum(task => task.DurationMinutes));
        Assert.Equal(480, (seed.MaintenanceWindows[0].End - seed.MaintenanceWindows[0].Start).TotalMinutes);
    }

    [Fact]
    public void Validate_ReducedWindowAndUnavailableBearing_ReturnsBothViolations()
    {
        var seed = P101AnchorSeed.Create();
        var unavailableBearing = new SparePart
        {
            Id = seed.SparePart.Id,
            Name = seed.SparePart.Name,
            Unit = seed.SparePart.Unit,
            OnHandQuantity = 0,
            ReservedQuantity = 0
        };
        var result = new MaintenanceFeasibilityValidator().Validate(
            seed.Tasks,
            seed.MaintenanceWindows.Single(window => window.Id == "MW-P101-REDUCED-20261024"),
            [unavailableBearing],
            seed.Technicians);

        Assert.False(result.IsPotentiallyFeasible);
        Assert.Equal(
            ["PART_UNAVAILABLE", "WINDOW_EXCEEDED"],
            result.Violations.Select(violation => violation.Code).OrderBy(code => code));
    }
}