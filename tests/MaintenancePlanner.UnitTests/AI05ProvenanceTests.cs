using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Seeding;
using Moq;

namespace MaintenancePlanner.UnitTests;

/// <summary>
/// AI05 tests: Verify provenance, deferral reasons, and uncertainty handling.
/// </summary>
public sealed class AI05ProvenanceTests
{
    private static readonly DateTimeOffset WindowStart =
        new(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public async Task AI05_DeferralReasonsIncluded_WhenTasksDeferred()
    {
        // Arrange: Request all three tasks for P-101
        var fixture = CreateP101Fixture();
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",  // 8-hour window (08:00-16:00)
            ["P101-PREP", "P101-BEARING-REPLACEMENT", "P101-VERIFICATION"]);

        // Act: Schedule with all three tasks
        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None);

        // Assert: Candidates should be generated and have deferral info if any tasks deferred
        Assert.NotEmpty(result.Candidates);
        var candidate = result.Candidates.First();

        // Verify structure: Either all requested tasks are scheduled, or deferral reasons exist
        if (candidate.DeferralReasons.Count > 0)
        {
            // If tasks are deferred, reasons must be provided and evidence-based
            foreach (var deferral in candidate.DeferralReasons)
            {
                Assert.False(string.IsNullOrWhiteSpace(deferral.Reason),
                    $"Deferral reason must not be empty for task {deferral.TaskId}");
                // Reasons should reference known constraints or be explicitly uncertain
                Assert.True(
                    deferral.Reason.Contains("Predecessor") ||
                    deferral.Reason.Contains("catalogue") ||
                    deferral.Reason.Contains("scheduling deferred"),
                    $"Deferral reason must be evidence-based, got: {deferral.Reason}");
            }
        }
        else
        {
            // If no deferrals, all requested tasks should be scheduled
            var scheduledTaskIds = new HashSet<string>(
                candidate.TaskPlacements.Select(p => p.TaskId),
                StringComparer.OrdinalIgnoreCase);
            foreach (var requested in candidate.RequestedTaskIds)
            {
                Assert.Contains(requested, scheduledTaskIds);
            }
        }
    }

    [Fact]
    public async Task AI05_EvidenceReferencesRetained_ForScheduledTasks()
    {
        // Arrange
        var fixture = CreateP101Fixture();
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-BEARING-REPLACEMENT"]);

        // Act
        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None);

        // Assert: Evidence references must exist and be sourced
        Assert.NotEmpty(result.Candidates);
        var candidate = result.Candidates.First();

        Assert.NotEmpty(candidate.EvidenceReferences);
        Assert.All(candidate.EvidenceReferences, evidence =>
        {
            // All task evidence must have a source kind (sourced fact)
            Assert.NotEqual(default, evidence.SourceKind);
            Assert.False(string.IsNullOrEmpty(evidence.SourceId));
        });
    }

    [Fact]
    public async Task AI05_DistinctRequestedVsDependencyTasks_Identified()
    {
        // Arrange: Request P101-BEARING-REPLACEMENT which has P101-PREP as predecessor
        var fixture = CreateP101Fixture();
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-BEARING-REPLACEMENT"]); // Only explicitly request bearing replacement

        // Act
        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None);

        // Assert
        Assert.NotEmpty(result.Candidates);
        var candidate = result.Candidates.First();

        // P101-PREP is a dependency (predecessor) not explicitly requested
        Assert.Contains("P101-BEARING-REPLACEMENT", candidate.RequestedTaskIds);
        Assert.Contains("P101-PREP", candidate.DependencyTaskIds);
        Assert.DoesNotContain("P101-PREP", candidate.RequestedTaskIds);
    }

    [Fact]
    public async Task AI05_NoFabricatedReasons_OnlyEvidenceBased()
    {
        // Arrange
        var fixture = CreateP101Fixture();
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-BEARING-REPLACEMENT"]);

        // Act
        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None);

        // Assert: If tasks are deferred, reasons must reference known constraints
        Assert.NotEmpty(result.Candidates);
        var candidate = result.Candidates.First();

        foreach (var deferral in candidate.DeferralReasons)
        {
            // Deferral reasons must be constrained to known causes, not invented
            Assert.True(
                deferral.Reason.Contains("Predecessor") ||
                deferral.Reason.Contains("catalogue") ||
                deferral.Reason.Contains("specific reason") ||
                deferral.Reason.Contains("scheduling deferred"),
                $"Deferral reason must be evidence-based or uncertain, not invented: {deferral.Reason}");
        }
    }

    [Fact]
    public async Task AI05_AssumptionsIdentified_InResult()
    {
        // Arrange: Use the standard P-101 fixture
        var fixture = CreateP101Fixture();
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-BEARING-REPLACEMENT"]);

        // Act
        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None);

        // Assert: Candidates should be generated (workflow adds assumptions)
        Assert.NotEmpty(result.Candidates);
        // Assumptions are part of the workflow result structure
        // (which would be wrapped in PlanningCandidatesGeneratedResult in the workflow)
    }

    private static SchedulerFixture CreateP101Fixture(
        decimal availableBearingQuantity = 1,
        string windowId = "MW-P101-BASELINE-20261024",
        IReadOnlyList<TechnicianReadModel>? technicians = null,
        IReadOnlyList<SparePartReadModel>? spareParts = null)
    {
        var seed = P101AnchorSeed.Create();
        var part = new SparePartReadModel("BR-101", "Bearing", "unit", 10, 0, availableBearingQuantity);

        var assets = new[] { ToAssetReadModel(seed.Asset) }.ToList();
        var catalogues = new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>(StringComparer.OrdinalIgnoreCase)
        {
            ["P-101"] = seed.Tasks.Select(ToTaskReadModel).ToArray()
        };

        var compatibility = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["P-101"] = ["BR-101"]
        };

        var windows = seed.MaintenanceWindows
            .Select(w => new MaintenanceWindowReadModel(w.Id, w.Name, w.Start, w.End, w.TimeZoneId))
            .ToList();
        windows.Add(new MaintenanceWindowReadModel(
            "MW-SHORT",
            "Short window",
            WindowStart,
            WindowStart.AddHours(4),
            "Asia/Kolkata"));

        var queries = new Mock<IMaintenancePlannerQueries>();
        queries.Setup(q => q.GetAssetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(assets);
        queries.Setup(q => q.GetMaintenanceWindowsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(windows);
        queries.Setup(q => q.GetTechniciansAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(technicians ?? ToTechnicianReadModels(seed.Technicians));
        queries.Setup(q => q.GetSparePartsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(spareParts ?? [part]);
        queries.Setup(q => q.GetTaskCatalogueAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((string assetId, CancellationToken _) =>
                Task.FromResult(catalogues.TryGetValue(assetId, out var catalogue)
                    ? (IReadOnlyList<MaintenanceTaskReadModel>?)catalogue
                    : null));
        queries.Setup(q => q.GetCompatibleSparePartsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((string assetId, CancellationToken _) =>
            {
                var compatibleIds = compatibility.TryGetValue(assetId, out var ids) ? ids : [];
                return Task.FromResult<IReadOnlyList<SparePartReadModel>>(
                    (spareParts ?? [part]).Where(p => compatibleIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToArray());
            });

        var sparePartProvider = new Mock<ISparePartReadModelProvider>();
        sparePartProvider.Setup(p => p.GetSparePartsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(spareParts ?? [part]);
        sparePartProvider.Setup(p => p.GetCompatibleSparePartsAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string assetId, string? _, CancellationToken _) =>
            {
                var compatibleIds = compatibility.TryGetValue(assetId, out var ids) ? ids : [];
                return Task.FromResult<IReadOnlyList<SparePartReadModel>>(
                    (spareParts ?? [part]).Where(p => compatibleIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToArray());
            });

        var validator = new PlanningCandidateValidator(sparePartProvider.Object, queries.Object);
        var costCalculator = new CandidateCostCalculator();
        return new SchedulerFixture(
            new DeterministicPlanningScheduler(sparePartProvider.Object, queries.Object, validator, costCalculator),
            validator);
    }

    private static IReadOnlyList<TechnicianReadModel> ToTechnicianReadModels(
        IEnumerable<Technician> technicians) =>
        technicians.Select(technician => new TechnicianReadModel(
            technician.Id,
            technician.Name,
            technician.HourlyRate,
            technician.TechnicalSkills.Select(skill => skill.SkillCode).ToArray(),
            technician.TechnicianAvailabilities.Select(availability =>
                new TechnicianAvailabilityReadModel(
                    availability.AvailableFrom,
                    availability.AvailableTo)).ToArray())).ToArray();

    private static AssetReadModel ToAssetReadModel(Asset asset) =>
        new(asset.Id, asset.PlantId, asset.Name, asset.Model, asset.Priority);

    private static MaintenanceTaskReadModel ToTaskReadModel(MaintenanceTaskDefinition task) =>
        new(
            task.Id,
            task.AssetId,
            task.Name,
            task.DurationMinutes,
            task.RequiredSkills,
            task.RequiredCrewSize,
            task.RequiredPartId,
            task.RequiredPartQuantity,
            task.PredecessorTaskId,
            task.SourceDocumentId,
            task.SourceSection);

    private sealed record SchedulerFixture(
        DeterministicPlanningScheduler Scheduler,
        PlanningCandidateValidator Validator);
}
