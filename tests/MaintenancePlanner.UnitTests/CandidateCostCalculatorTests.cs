using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;

namespace MaintenancePlanner.UnitTests;

public sealed class CandidateCostCalculatorTests
{
    private static readonly DateTimeOffset WindowStart =
        new(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5));

    private readonly CandidateCostCalculator _calculator = new();

    [Fact]
    public void CalculateMetrics_SingleTechnicianSingleTask_CalculatesCorrectLabourCost()
    {
        var placements = new[]
        {
            new PlanningTaskPlacement(
                "TASK-001",
                WindowStart,
                WindowStart.AddHours(2),
                ["TECH-001"])
        };
        var candidate = new PlanningCandidate(
            placements,
            "Test rationale",
            []);
        var technicians = new Dictionary<string, TechnicianReadModel>(StringComparer.OrdinalIgnoreCase)
        {
            {
                "TECH-001",
                new TechnicianReadModel(
                    "TECH-001",
                    "John Doe",
                    50m,
                    ["MECHANICAL"],
                    [])
            }
        };

        var metrics = _calculator.CalculateMetrics(candidate, technicians);

        Assert.Equal(100m, metrics.TotalLabourCostAmount);
        Assert.True(metrics.HasValidCosts);
        Assert.Null(metrics.CostWarning);
        Assert.Equal(120, metrics.CompletionTimeMinutes);
    }

    [Fact]
    public void CalculateMetrics_MultipleTechniciansOneSingleTask_ChargesEachTechnicianRate()
    {
        var placements = new[]
        {
            new PlanningTaskPlacement(
                "TASK-002",
                WindowStart,
                WindowStart.AddMinutes(90),
                ["TECH-001", "TECH-002"])
        };
        var candidate = new PlanningCandidate(placements, "Test", []);
        var technicians = new Dictionary<string, TechnicianReadModel>(StringComparer.OrdinalIgnoreCase)
        {
            { "TECH-001", new("TECH-001", "Operator A", 60m, [], []) },
            { "TECH-002", new("TECH-002", "Operator B", 40m, [], []) }
        };

        var metrics = _calculator.CalculateMetrics(candidate, technicians);

        Assert.Equal(150m, metrics.TotalLabourCostAmount);
        Assert.True(metrics.HasValidCosts);
        Assert.Equal(90, metrics.CompletionTimeMinutes);
    }

    [Fact]
    public void CalculateMetrics_MultipleTasksDifferentTechnicians_SumsAllTaskCosts()
    {
        var placements = new[]
        {
            new PlanningTaskPlacement(
                "TASK-A",
                WindowStart,
                WindowStart.AddHours(1),
                ["TECH-001"]),
            new PlanningTaskPlacement(
                "TASK-B",
                WindowStart.AddHours(1),
                WindowStart.AddHours(3),
                ["TECH-002"])
        };
        var candidate = new PlanningCandidate(placements, "Test", []);
        var technicians = new Dictionary<string, TechnicianReadModel>(StringComparer.OrdinalIgnoreCase)
        {
            { "TECH-001", new("TECH-001", "A", 50m, [], []) },
            { "TECH-002", new("TECH-002", "B", 30m, [], []) }
        };

        var metrics = _calculator.CalculateMetrics(candidate, technicians);

        Assert.Equal(110m, metrics.TotalLabourCostAmount);
        Assert.Equal(180, metrics.CompletionTimeMinutes);
    }

    [Fact]
    public void CalculateMetrics_DifferentHourlyRatesDetermineCostOrdering()
    {
        var placements = new[]
        {
            new PlanningTaskPlacement(
                "TASK-001",
                WindowStart,
                WindowStart.AddHours(4),
                ["TECH-EXPENSIVE"])
        };
        var candidate = new PlanningCandidate(placements, "Expensive crew", []);
        var techniciansExpensive = new Dictionary<string, TechnicianReadModel>(StringComparer.OrdinalIgnoreCase)
        {
            { "TECH-EXPENSIVE", new("TECH-EXPENSIVE", "Expert", 100m, [], []) }
        };

        var metricsExpensive = _calculator.CalculateMetrics(candidate, techniciansExpensive);

        Assert.Equal(400m, metricsExpensive.TotalLabourCostAmount);

        var placementsCheap = new[]
        {
            new PlanningTaskPlacement(
                "TASK-001",
                WindowStart,
                WindowStart.AddHours(4),
                ["TECH-CHEAP"])
        };
        var candidateCheap = new PlanningCandidate(placementsCheap, "Cheap crew", []);
        var techniciansCheap = new Dictionary<string, TechnicianReadModel>(StringComparer.OrdinalIgnoreCase)
        {
            { "TECH-CHEAP", new("TECH-CHEAP", "Junior", 25m, [], []) }
        };

        var metricsCheap = _calculator.CalculateMetrics(candidateCheap, techniciansCheap);

        Assert.Equal(100m, metricsCheap.TotalLabourCostAmount);
        Assert.True(metricsExpensive.TotalLabourCostAmount > metricsCheap.TotalLabourCostAmount);
    }

    [Fact]
    public void CalculateMetrics_CompletionTimeFromFirstTaskStartToLastTaskEnd()
    {
        var placements = new[]
        {
            new PlanningTaskPlacement(
                "TASK-A",
                WindowStart,
                WindowStart.AddHours(1),
                ["TECH-001"]),
            new PlanningTaskPlacement(
                "TASK-B",
                WindowStart.AddHours(0.5),
                WindowStart.AddHours(2),
                ["TECH-001"]),
            new PlanningTaskPlacement(
                "TASK-C",
                WindowStart.AddHours(1.5),
                WindowStart.AddHours(3),
                ["TECH-001"])
        };
        var candidate = new PlanningCandidate(placements, "Test", []);
        var technicians = new Dictionary<string, TechnicianReadModel>(StringComparer.OrdinalIgnoreCase)
        {
            { "TECH-001", new("TECH-001", "Tech", 50m, [], []) }
        };

        var metrics = _calculator.CalculateMetrics(candidate, technicians);

        Assert.Equal(180, metrics.CompletionTimeMinutes);
    }

    [Fact]
    public void CalculateMetrics_MissingTechnicianInLookup_RecordsWarningAndMarksInvalid()
    {
        var placements = new[]
        {
            new PlanningTaskPlacement(
                "TASK-001",
                WindowStart,
                WindowStart.AddHours(1),
                ["TECH-MISSING"])
        };
        var candidate = new PlanningCandidate(placements, "Test", []);
        var technicians = new Dictionary<string, TechnicianReadModel>(StringComparer.OrdinalIgnoreCase)
        {
            { "TECH-PRESENT", new("TECH-PRESENT", "Tech", 50m, [], []) }
        };

        var metrics = _calculator.CalculateMetrics(candidate, technicians);

        Assert.False(metrics.HasValidCosts);
        Assert.NotNull(metrics.CostWarning);
        Assert.Contains("TECH-MISSING", metrics.CostWarning);
        Assert.Equal(0m, metrics.TotalLabourCostAmount);
    }

    [Fact]
    public void CalculateMetrics_NegativeHourlyRate_RecordsWarning()
    {
        var placements = new[]
        {
            new PlanningTaskPlacement(
                "TASK-001",
                WindowStart,
                WindowStart.AddHours(1),
                ["TECH-INVALID"])
        };
        var candidate = new PlanningCandidate(placements, "Test", []);
        var technicians = new Dictionary<string, TechnicianReadModel>(StringComparer.OrdinalIgnoreCase)
        {
            { "TECH-INVALID", new("TECH-INVALID", "Bad Tech", -50m, [], []) }
        };

        var metrics = _calculator.CalculateMetrics(candidate, technicians);

        Assert.False(metrics.HasValidCosts);
        Assert.NotNull(metrics.CostWarning);
        Assert.Contains("invalid hourly rate", metrics.CostWarning);
    }

    [Fact]
    public void CalculateMetrics_EmptyTaskPlacements_ReturnsZeroCostAndTime()
    {
        var candidate = new PlanningCandidate([], "Empty", []);
        var technicians = new Dictionary<string, TechnicianReadModel>(StringComparer.OrdinalIgnoreCase);

        var metrics = _calculator.CalculateMetrics(candidate, technicians);

        Assert.Equal(0m, metrics.TotalLabourCostAmount);
        Assert.Equal(0, metrics.CompletionTimeMinutes);
        Assert.True(metrics.HasValidCosts);
        Assert.Null(metrics.CostWarning);
    }

    [Fact]
    public void ApplyRankingMetadata_LowestCostCandidateMarkedCorrectly()
    {
        var candidates = new List<PlanningCandidate>
        {
            new([], "Expensive", [])
            {
                TotalLabourCostAmount = 500m,
                CostComparabilityWarning = null
            },
            new([], "Cheap", [])
            {
                TotalLabourCostAmount = 100m,
                CostComparabilityWarning = null
            }
        };

        CandidateCostCalculator.ApplyRankingMetadata(candidates);

        Assert.False(candidates[0].IsLowestCostCandidate);
        Assert.True(candidates[1].IsLowestCostCandidate);
    }

    [Fact]
    public void ApplyRankingMetadata_WithCostWarningNotMarkedLowest()
    {
        var candidates = new List<PlanningCandidate>
        {
            new([], "Invalid Cost", [])
            {
                TotalLabourCostAmount = 50m,
                CostComparabilityWarning = "Cost is invalid"
            },
            new([], "Valid Cost", [])
            {
                TotalLabourCostAmount = 100m,
                CostComparabilityWarning = null
            }
        };

        CandidateCostCalculator.ApplyRankingMetadata(candidates);

        Assert.False(candidates[0].IsLowestCostCandidate);
        Assert.True(candidates[1].IsLowestCostCandidate);
    }

    [Fact]
    public void ApplyRankingMetadata_AllCandidatesHaveCostWarnings_NoneMarkedLowest()
    {
        var candidates = new List<PlanningCandidate>
        {
            new([], "C1", [])
            {
                TotalLabourCostAmount = 100m,
                CostComparabilityWarning = "Invalid"
            },
            new([], "C2", [])
            {
                TotalLabourCostAmount = 200m,
                CostComparabilityWarning = "Invalid"
            }
        };

        CandidateCostCalculator.ApplyRankingMetadata(candidates);

        Assert.All(candidates, c => Assert.False(c.IsLowestCostCandidate));
    }

    [Fact]
    public void CalculateMetrics_FractionalMinutesRoundedDeterministically()
    {
        var placements = new[]
        {
            new PlanningTaskPlacement(
                "TASK-001",
                WindowStart,
                WindowStart.AddMinutes(45.5),
                ["TECH-001"])
        };
        var candidate = new PlanningCandidate(placements, "Test", []);
        var technicians = new Dictionary<string, TechnicianReadModel>(StringComparer.OrdinalIgnoreCase)
        {
            { "TECH-001", new("TECH-001", "Tech", 33.33m, [], []) }
        };

        var metrics = _calculator.CalculateMetrics(candidate, technicians);

        var expectedCost = 33.33m * (45.5m / 60m);
        Assert.Equal(Decimal.Round(expectedCost, 2), metrics.TotalLabourCostAmount);
    }
}
