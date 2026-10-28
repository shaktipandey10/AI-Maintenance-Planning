using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Seeding;
using Moq;

namespace MaintenancePlanner.UnitTests;

public sealed class P101LockingTests
{
    private static readonly DateTimeOffset WindowStart =
        new(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public async Task ScheduleAsync_NoLocksProvided_WorksNormally()
    {
        var fixture = CreateP101Fixture();
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-BEARING-REPLACEMENT"]);

        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None,
            lockedTaskAssignments: null);

        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public async Task ScheduleAsync_LockedTechnicianAvailable_SchedulesWithLockedAssignment()
    {
        var fixture = CreateP101Fixture();
        var lockedAssignments = new Dictionary<string, string>
        {
            { "P101-PREP", "TECH-001" } // TECH-001 is available and qualified for MECHANICAL
        };
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-PREP"]);

        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None,
            lockedAssignments);

        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);
        Assert.NotEmpty(result.Candidates);
        // Verify that TECH-001 is actually assigned to the task
        var candidate = result.Candidates.First();
        var prepPlacement = candidate.TaskPlacements.First(p => p.TaskId == "P101-PREP");
        Assert.Contains("TECH-001", prepPlacement.TechnicianIds, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScheduleAsync_LockedTechnicianUnavailable_ReturnsInfeasibleWithLockConflict()
    {
        var p101 = P101AnchorSeed.Create();
        // Create a technician that is unavailable for the window
        var unavailableTechnician = new TechnicianReadModel(
            "TECH-UNAVAILABLE",
            "Unavailable technician",
            40,
            ["MECHANICAL"],
            [new TechnicianAvailabilityReadModel(
                new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.FromHours(5.5)),
                new DateTimeOffset(2026, 9, 1, 16, 0, 0, TimeSpan.FromHours(5.5)))]);

        var fixture = CreateP101Fixture(
            technicians: [unavailableTechnician, ..ToTechnicianReadModels(p101.Technicians)]);

        var lockedAssignments = new Dictionary<string, string>
        {
            { "P101-PREP", "TECH-UNAVAILABLE" }
        };
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-PREP"]);

        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None,
            lockedAssignments);

        Assert.Equal(PlanningScheduleOutcome.Infeasible, result.Outcome);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "LOCKED_TECHNICIAN_UNAVAILABLE" &&
            violation.TaskId == "P101-PREP" &&
            violation.ResourceId == "TECH-UNAVAILABLE");
    }

    [Fact]
    public async Task ScheduleAsync_LockedTechnicianUnqualified_ReturnsInfeasibleWithLockConflict()
    {
        var p101 = P101AnchorSeed.Create();
        // Create a technician with wrong skills
        var unqualifiedTechnician = new TechnicianReadModel(
            "TECH-ELECTRICAL",
            "Electrical specialist",
            40,
            ["ELECTRICAL"],
            [new TechnicianAvailabilityReadModel(WindowStart, WindowStart.AddHours(8))]);

        var fixture = CreateP101Fixture(
            technicians: [unqualifiedTechnician, ..ToTechnicianReadModels(p101.Technicians)]);

        var lockedAssignments = new Dictionary<string, string>
        {
            { "P101-PREP", "TECH-ELECTRICAL" } // P101-PREP requires MECHANICAL
        };
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-PREP"]);

        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None,
            lockedAssignments);

        Assert.Equal(PlanningScheduleOutcome.Infeasible, result.Outcome);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "LOCKED_TECHNICIAN_UNAVAILABLE" &&
            violation.TaskId == "P101-PREP" &&
            violation.ResourceId == "TECH-ELECTRICAL");
    }

    [Fact]
    public async Task ScheduleAsync_LockedTechnicianNotFound_ReturnsInfeasibleWithLockConflict()
    {
        var fixture = CreateP101Fixture();

        var lockedAssignments = new Dictionary<string, string>
        {
            { "P101-PREP", "TECH-NONEXISTENT" }
        };
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-PREP"]);

        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None,
            lockedAssignments);

        Assert.Equal(PlanningScheduleOutcome.Infeasible, result.Outcome);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "LOCKED_TECHNICIAN_UNAVAILABLE" &&
            violation.TaskId == "P101-PREP" &&
            violation.ResourceId == "TECH-NONEXISTENT");
    }

    [Fact]
    public async Task ScheduleAsync_NonLockedTasksRemainsFlexible_WhenOtherTaskIsLocked()
    {
        var fixture = CreateP101Fixture();

        // Lock only PREP, leave BEARING-REPLACEMENT unlockedand flexible
        var lockedAssignments = new Dictionary<string, string>
        {
            { "P101-PREP", "TECH-001" }
        };
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-BEARING-REPLACEMENT"]);

        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None,
            lockedAssignments);

        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);
        Assert.NotEmpty(result.Candidates);

        var candidate = result.Candidates.First();
        var bearingPlacement = candidate.TaskPlacements.First(p => p.TaskId == "P101-BEARING-REPLACEMENT");

        // Bearing replacement requires 2 technicians; should be flexible and not forced to TECH-001
        Assert.Equal(2, bearingPlacement.TechnicianIds.Count);
    }

    [Fact]
    public async Task ScheduleAsync_MultipleLockedTasks_EnforcesAllLocks()
    {
        var fixture = CreateP101Fixture();

        // Lock PREP and BEARING-REPLACEMENT to different technicians
        var lockedAssignments = new Dictionary<string, string>
        {
            { "P101-PREP", "TECH-001" },
            { "P101-BEARING-REPLACEMENT", "TECH-002" } // One of the two required technicians
        };
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-BEARING-REPLACEMENT"]);

        var result = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None,
            lockedAssignments);

        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);
        var candidate = result.Candidates.First();

        var prepPlacement = candidate.TaskPlacements.First(p => p.TaskId == "P101-PREP");
        var bearingPlacement = candidate.TaskPlacements.First(p => p.TaskId == "P101-BEARING-REPLACEMENT");

        Assert.Contains("TECH-001", prepPlacement.TechnicianIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("TECH-002", bearingPlacement.TechnicianIds, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScheduleAsync_LockThatLeavesNoSlot_IsReportedAsALockConflictInsteadOfBeingDropped()
    {
        // The locked technician is qualified and available for the window, but the preparation (in progress at a
        // placement filling the whole window) leaves no slot for the locked bearing replacement
        var fixture = CreateP101Fixture();
        var request = new PlanningScheduleRequest(
            ["P-101"],
            "MW-P101-BASELINE-20261024",
            ["P101-BEARING-REPLACEMENT"])
        {
            InProgressPlacements = [new PlanningTaskPlacement("P101-PREP", WindowStart, WindowStart.AddHours(8), ["TECH-003"])]
        };

        var locked = await fixture.Scheduler.ScheduleAsync(
            request,
            CancellationToken.None,
            new Dictionary<string, string> { { "P101-BEARING-REPLACEMENT", "TECH-002" } });
        var unlocked = await fixture.Scheduler.ScheduleAsync(request, CancellationToken.None, lockedTaskAssignments: null);

        Assert.Empty(locked.Candidates);
        Assert.Contains(locked.Violations, violation =>
            violation.Code == "LOCKED_ASSIGNMENT_NOT_SCHEDULABLE" &&
            violation.TaskId == "P101-BEARING-REPLACEMENT" &&
            violation.ResourceId == "TECH-002" &&
            violation.Message.Contains("locked to technician TECH-002", StringComparison.Ordinal));
        Assert.DoesNotContain(unlocked.Violations, violation => violation.Code == "LOCKED_ASSIGNMENT_NOT_SCHEDULABLE");
    }

    private static SchedulerFixture CreateP101Fixture(
        decimal availableBearingQuantity = 1,
        string windowId = "MW-P101-BASELINE-20261024",
        bool includeP102Inspection = false,
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

        if (includeP102Inspection)
        {
            var p102Asset = CreateAsset("P-102", priority: 2);
            assets.Add(p102Asset);
            catalogues["P-102"] = [CreateTask("P102-INSPECTION", "P-102", "MECHANICAL")];
        }

        var compatibility = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["P-101"] = ["BR-101"]
        };

        var windows = seed.MaintenanceWindows
            .Select(window => new MaintenanceWindowReadModel(
                window.Id,
                window.Name,
                window.Start,
                window.End,
                window.TimeZoneId))
            .ToArray();

        return CreateFixture(
            assets,
            catalogues,
            technicians ?? ToTechnicianReadModels(seed.Technicians),
            spareParts ?? [part],
            compatibility,
            windows);
    }

    private static SchedulerFixture CreateFixture(
        IReadOnlyList<AssetReadModel> assets,
        IReadOnlyDictionary<string, IReadOnlyList<MaintenanceTaskReadModel>> taskCatalogues,
        IReadOnlyList<TechnicianReadModel> technicians,
        IReadOnlyList<SparePartReadModel> spareParts,
        IReadOnlyDictionary<string, IReadOnlyList<string>> compatiblePartIdsByAsset,
        IReadOnlyList<MaintenanceWindowReadModel>? windows = null)
    {
        windows ??= P101AnchorSeed.Create().MaintenanceWindows
            .Select(window => new MaintenanceWindowReadModel(
                window.Id,
                window.Name,
                window.Start,
                window.End,
                window.TimeZoneId))
            .ToArray();

        var queries = new Mock<IMaintenancePlannerQueries>();
        queries.Setup(query => query.GetAssetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(assets);
        queries.Setup(query => query.GetMaintenanceWindowsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(windows);
        queries.Setup(query => query.GetTechniciansAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(technicians);
        queries.Setup(query => query.GetSparePartsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(spareParts);
        queries.Setup(query => query.GetTaskCatalogueAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((string assetId, CancellationToken _) =>
                Task.FromResult(taskCatalogues.TryGetValue(assetId, out var catalogue)
                    ? (IReadOnlyList<MaintenanceTaskReadModel>?)catalogue
                    : null));
        queries.Setup(query => query.GetCompatibleSparePartsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((string assetId, CancellationToken _) =>
            {
                var compatibleIds = compatiblePartIdsByAsset.TryGetValue(assetId, out var ids)
                    ? ids
                    : [];
                return Task.FromResult<IReadOnlyList<SparePartReadModel>>(
                    spareParts.Where(part => compatibleIds.Contains(
                        part.Id,
                        StringComparer.OrdinalIgnoreCase)).ToArray());
            });

        var sparePartProvider = new Mock<ISparePartReadModelProvider>();
        sparePartProvider.Setup(provider => provider.GetSparePartsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(spareParts);
        sparePartProvider.Setup(provider => provider.GetCompatibleSparePartsAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string assetId, string? _, CancellationToken _) =>
            {
                var compatibleIds = compatiblePartIdsByAsset.TryGetValue(assetId, out var ids)
                    ? ids
                    : [];
                return Task.FromResult<IReadOnlyList<SparePartReadModel>>(
                    spareParts.Where(part => compatibleIds.Contains(
                        part.Id,
                        StringComparer.OrdinalIgnoreCase)).ToArray());
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

    private static AssetReadModel CreateAsset(string id, int priority) =>
        new(id, "PLANT-01", $"Synthetic asset {id}", "SYNTHETIC", priority);

    private static MaintenanceTaskReadModel CreateTask(
        string id,
        string assetId,
        string requiredSkill,
        string? requiredPartId = null) =>
        new(
            id,
            assetId,
            $"Synthetic task {id}",
            60,
            requiredSkill,
            1,
            requiredPartId,
            requiredPartId is null ? 0 : 1,
            null,
            "SYNTHETIC-MANUAL",
            "Synthetic section");

    private sealed record SchedulerFixture(
        DeterministicPlanningScheduler Scheduler,
        PlanningCandidateValidator Validator);
}
