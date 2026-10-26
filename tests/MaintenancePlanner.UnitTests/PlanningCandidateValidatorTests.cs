using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Seeding;
using Moq;

namespace MaintenancePlanner.UnitTests;

public sealed class PlanningCandidateValidatorTests
{
    private static readonly DateTimeOffset WindowStart =
        new(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public async Task ValidateAsync_P101BaselineCandidate_IsFeasible()
    {
        var seed = P101AnchorSeed.Create();
        var validator = CreateValidator(seed);

        var result = await validator.ValidateAsync(CreateRequest(BaselinePlacements()), CancellationToken.None);

        Assert.True(result.IsFeasible);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task ValidateAsync_ReducedWindowAndUnavailableBearing_IsInfeasible()
    {
        var seed = P101AnchorSeed.Create();
        var unavailableParts = new[]
        {
            new SparePartReadModel("BR-101", "Bearing", "each", 1, 1, 0)
        };
        var validator = CreateValidator(seed, spareParts: unavailableParts);
        var request = CreateRequest(
            BaselinePlacements(),
            "MW-P101-REDUCED-20261024");

        var result = await validator.ValidateAsync(request, CancellationToken.None);

        Assert.False(result.IsFeasible);
        Assert.Contains(result.Violations, violation => violation.Code == "PART_UNAVAILABLE");
        Assert.Contains(result.Violations, violation => violation.Code == "TASK_OUTSIDE_WINDOW");
    }

    [Fact]
    public async Task ValidateAsync_ReducedWindowAlone_RejectsTaskOutsideWindow()
    {
        var seed = P101AnchorSeed.Create();
        var result = await CreateValidator(seed)
            .ValidateAsync(
                CreateRequest(BaselinePlacements(), "MW-P101-REDUCED-20261024"),
                CancellationToken.None);

        Assert.False(result.IsFeasible);
        var violation = Assert.Single(result.Violations);
        Assert.Equal("TASK_OUTSIDE_WINDOW", violation.Code);
        Assert.Equal("P101-VERIFICATION", violation.TaskId);
    }

    [Fact]
    public async Task ValidateAsync_BearingRequiresTwoDistinctQualifiedTechnicians()
    {
        var seed = P101AnchorSeed.Create();
        var placements = BaselinePlacements().ToArray();
        placements[1] = placements[1] with { TechnicianIds = ["TECH-001"] };
        var validator = CreateValidator(seed);

        var result = await validator.ValidateAsync(CreateRequest(placements), CancellationToken.None);

        Assert.False(result.IsFeasible);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "CREW_SIZE_UNMET" &&
            violation.TaskId == "P101-BEARING-REPLACEMENT");
    }

    [Fact]
    public async Task ValidateAsync_MissingOrUnqualifiedTechnicians_AreRejected()
    {
        var seed = P101AnchorSeed.Create();
        var validator = CreateValidator(seed);
        var unknownTechPlacements = BaselinePlacements().ToArray();
        unknownTechPlacements[1] = unknownTechPlacements[1] with
        {
            TechnicianIds = ["TECH-001", "TECH-MISSING"]
        };

        var missingResult = await validator.ValidateAsync(
            CreateRequest(unknownTechPlacements),
            CancellationToken.None);

        Assert.Contains(missingResult.Violations, violation =>
            violation.Code == "TECHNICIAN_NOT_FOUND" && violation.ResourceId == "TECH-MISSING");

        var unqualifiedPlacements = BaselinePlacements().ToArray();
        unqualifiedPlacements[1] = unqualifiedPlacements[1] with
        {
            TechnicianIds = ["TECH-001", "TECH-003"]
        };
        var unqualifiedResult = await validator.ValidateAsync(
            CreateRequest(unqualifiedPlacements),
            CancellationToken.None);

        Assert.Contains(unqualifiedResult.Violations, violation =>
            violation.Code == "TECHNICIAN_UNQUALIFIED" && violation.ResourceId == "TECH-003");
    }

    [Fact]
    public async Task ValidateAsync_TechnicianUnavailableForFullPlacement_IsRejected()
    {
        var seed = P101AnchorSeed.Create();
        var technicians = seed.Technicians.Select(technician => new TechnicianReadModel(
            technician.Id,
            technician.Name,
            technician.HourlyRate,
            technician.TechnicalSkills.Select(skill => skill.SkillCode).ToArray(),
            technician.TechnicianAvailabilities.Select(availability =>
                new TechnicianAvailabilityReadModel(
                    availability.AvailableFrom,
                    availability.AvailableTo)).ToArray())).ToArray();
        technicians[1] = technicians[1] with
        {
            Availability =
            [
                new TechnicianAvailabilityReadModel(WindowStart, WindowStart.AddHours(2))
            ]
        };
        var result = await CreateValidator(seed, technicians: technicians)
            .ValidateAsync(CreateRequest(BaselinePlacements()), CancellationToken.None);

        Assert.Contains(result.Violations, violation =>
            violation.Code == "TECHNICIAN_UNAVAILABLE" &&
            violation.TaskId == "P101-BEARING-REPLACEMENT" &&
            violation.ResourceId == "TECH-002");
    }

    [Fact]
    public async Task ValidateAsync_InsufficientStock_IsRejected()
    {
        var seed = P101AnchorSeed.Create();
        var insufficientParts = new[]
        {
            new SparePartReadModel("BR-101", "Bearing", "each", 1, 0.5m, 0.5m)
        };
        var result = await CreateValidator(seed, spareParts: insufficientParts)
            .ValidateAsync(CreateRequest(BaselinePlacements()), CancellationToken.None);

        Assert.Contains(result.Violations, violation =>
            violation.Code == "PART_UNAVAILABLE" && violation.ResourceId == "BR-101");
    }

    [Fact]
    public async Task ValidateAsync_IncompatiblePart_IsRejected()
    {
        var seed = P101AnchorSeed.Create();
        var result = await CreateValidator(
                seed,
                compatiblePartIds: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["P-101"] = []
                })
            .ValidateAsync(CreateRequest(BaselinePlacements()), CancellationToken.None);

        Assert.Contains(result.Violations, violation =>
            violation.Code == "PART_INCOMPATIBLE" &&
            violation.TaskId == "P101-BEARING-REPLACEMENT" &&
            violation.ResourceId == "BR-101");
    }

    [Fact]
    public async Task ValidateAsync_MalformedRequiredSkills_AreRejected()
    {
        var seed = P101AnchorSeed.Create();
        var task = new MaintenanceTaskReadModel(
            "P101-MALFORMED-SKILLS",
            "P-101",
            "Malformed skill fixture",
            60,
            " , ",
            1,
            null,
            0,
            null,
            "MAN-P101-001",
            "Synthetic test section");
        var validator = CreateValidator(
            seed,
            taskCatalogues: new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = [task]
            });
        var candidate = new PlanningCandidate(
            [
                new PlanningTaskPlacement(
                    task.Id,
                    WindowStart,
                    WindowStart.AddHours(1),
                    ["TECH-001"])
            ],
            "Malformed-skill test",
            []);

        var result = await validator.ValidateAsync(
            new PlanningCandidateValidationRequest(
                ["P-101"],
                "MW-P101-BASELINE-20261024",
                candidate),
            CancellationToken.None);

        Assert.False(result.IsFeasible);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "INVALID_REQUIRED_SKILLS" &&
            violation.TaskId == task.Id);
        Assert.DoesNotContain(result.Violations, violation =>
            violation.Code == "TECHNICIAN_UNQUALIFIED" &&
            violation.ResourceId == "TECH-001");
    }

    [Fact]
    public async Task ValidateAsync_MissingRequiredPartRecord_IsDataUnavailableNotConfirmedUnavailability()
    {
        var seed = P101AnchorSeed.Create();
        var task = new MaintenanceTaskReadModel(
            "P101-MISSING-PART-RECORD",
            "P-101",
            "Missing part fixture",
            60,
            "MECHANICAL",
            1,
            "UNKNOWN-PART",
            1,
            null,
            "MAN-P101-001",
            "Synthetic test section");
        var validator = CreateValidator(
            seed,
            spareParts: [],
            taskCatalogues: new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = [task]
            });
        var candidate = new PlanningCandidate(
            [
                new PlanningTaskPlacement(
                    task.Id,
                    WindowStart,
                    WindowStart.AddHours(1),
                    ["TECH-001"])
            ],
            "Missing-part test",
            []);

        var result = await validator.ValidateAsync(
            new PlanningCandidateValidationRequest(
                ["P-101"],
                "MW-P101-BASELINE-20261024",
                candidate),
            CancellationToken.None);

        Assert.False(result.IsFeasible);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "PART_DATA_UNAVAILABLE" &&
            violation.TaskId == task.Id &&
            violation.ResourceId == "UNKNOWN-PART");
        Assert.DoesNotContain(result.Violations, violation => violation.Code == "PART_UNAVAILABLE");
    }

    [Fact]
    public async Task ValidateAsync_InvalidIntervalAndDurationMismatch_AreRejected()
    {
        var seed = P101AnchorSeed.Create();
        var placements = BaselinePlacements().ToArray();
        placements[0] = placements[0] with
        {
            Start = WindowStart.AddHours(1),
            End = WindowStart
        };
        placements[1] = placements[1] with { End = placements[1].End.AddMinutes(-1) };
        var result = await CreateValidator(seed)
            .ValidateAsync(CreateRequest(placements), CancellationToken.None);

        Assert.Contains(result.Violations, violation =>
            violation.Code == "INVALID_TASK_INTERVAL" && violation.TaskId == "P101-PREP");
        Assert.Contains(result.Violations, violation =>
            violation.Code == "TASK_DURATION_MISMATCH" &&
            violation.TaskId == "P101-BEARING-REPLACEMENT");
    }

    [Fact]
    public async Task ValidateAsync_PredecessorViolation_IsRejected()
    {
        var seed = P101AnchorSeed.Create();
        var placements = BaselinePlacements().ToArray();
        placements[1] = placements[1] with
        {
            Start = WindowStart.AddMinutes(30),
            End = WindowStart.AddMinutes(210)
        };
        var result = await CreateValidator(seed)
            .ValidateAsync(CreateRequest(placements), CancellationToken.None);

        Assert.Contains(result.Violations, violation =>
            violation.Code == "PREDECESSOR_ORDER_VIOLATION" &&
            violation.TaskId == "P101-BEARING-REPLACEMENT");
    }

    [Fact]
    public async Task ValidateAsync_OverlappingTechnicianAssignments_AreRejected()
    {
        var seed = P101AnchorSeed.Create();
        var placements = BaselinePlacements().ToArray();
        placements[1] = placements[1] with
        {
            Start = WindowStart.AddMinutes(30),
            End = WindowStart.AddMinutes(210),
            TechnicianIds = ["TECH-001", "TECH-002"]
        };
        var result = await CreateValidator(seed)
            .ValidateAsync(CreateRequest(placements), CancellationToken.None);

        Assert.Contains(result.Violations, violation =>
            violation.Code == "TECHNICIAN_SLOT_CONFLICT" &&
            violation.ResourceId == "TECH-001");
    }

    [Fact]
    public async Task ValidateAsync_UnknownDuplicateAndOutOfScopeTasks_AreRejected()
    {
        var seed = P101AnchorSeed.Create();
        var outsideAsset = new AssetReadModel("P-102", "PLANT-01", "Other pump", "CP-100", 2);
        var outsideTask = new MaintenanceTaskReadModel(
            "P102-INSPECTION",
            "P-102",
            "Inspection",
            60,
            "MECHANICAL",
            1,
            null,
            0,
            null,
            "MAN-P102-SCOPE-001",
            "Inspection-only scope");
        var taskCatalogues = new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
        {
            ["P-101"] = ToReadModels(seed.Tasks),
            ["P-102"] = [outsideTask]
        };
        var validator = CreateValidator(
            seed,
            assets: [ToReadModel(seed.Asset), outsideAsset],
            taskCatalogues: taskCatalogues);
        var placements = BaselinePlacements().ToList();
        placements.Add(placements[0]);
        placements.Add(new PlanningTaskPlacement(
            "UNKNOWN-TASK",
            WindowStart.AddHours(6),
            WindowStart.AddHours(7),
            ["TECH-001"]));
        placements.Add(new PlanningTaskPlacement(
            "P102-INSPECTION",
            WindowStart.AddHours(6),
            WindowStart.AddHours(7),
            ["TECH-001"]));

        var result = await validator.ValidateAsync(
            CreateRequest(placements),
            CancellationToken.None);

        Assert.Contains(result.Violations, violation => violation.Code == "DUPLICATE_TASK_PLACEMENT");
        Assert.Contains(result.Violations, violation => violation.Code == "TASK_NOT_FOUND");
        Assert.Contains(result.Violations, violation =>
            violation.Code == "TASK_ASSET_MISMATCH" && violation.TaskId == "P102-INSPECTION");
    }

    [Fact]
    public async Task ValidateAsync_MultiAssetCandidate_ValidatesTasksAgainstTheirAssets()
    {
        var seed = P101AnchorSeed.Create();
        var secondAsset = new AssetReadModel("P-102", "PLANT-01", "Second pump", "CP-100", 2);
        var secondTask = new MaintenanceTaskReadModel(
            "P102-INSPECTION",
            "P-102",
            "Inspection",
            60,
            "MECHANICAL",
            1,
            null,
            0,
            null,
            "MAN-P102-SCOPE-001",
            "Inspection-only scope");
        var taskCatalogues = new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
        {
            ["P-101"] = ToReadModels(seed.Tasks),
            ["P-102"] = [secondTask]
        };
        var validator = CreateValidator(
            seed,
            assets: [ToReadModel(seed.Asset), secondAsset],
            taskCatalogues: taskCatalogues,
            compatiblePartIds: new Dictionary<string, IReadOnlyList<string>>
            {
                ["P-101"] = [seed.SparePart.Id],
                ["P-102"] = []
            });
        var request = new PlanningCandidateValidationRequest(
            ["P-101", "P-102"],
            "MW-P101-BASELINE-20261024",
            new PlanningCandidate(
            [
                new PlanningTaskPlacement(
                    "P101-PREP",
                    WindowStart,
                    WindowStart.AddHours(1),
                    ["TECH-001"]),
                new PlanningTaskPlacement(
                    "P102-INSPECTION",
                    WindowStart.AddHours(1),
                    WindowStart.AddHours(2),
                    ["TECH-003"])
            ],
            "Tasks on both selected assets",
            []));

        var result = await validator.ValidateAsync(request, CancellationToken.None);

        Assert.True(result.IsFeasible);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task ValidateAsync_SharedTechnicianOverlappingAcrossAssets_IsRejected()
    {
        var seed = P101AnchorSeed.Create();
        var secondAsset = new AssetReadModel("P-102", "PLANT-01", "Second pump", "CP-100", 2);
        var secondTask = new MaintenanceTaskReadModel(
            "P102-INSPECTION",
            "P-102",
            "Inspection",
            60,
            "MECHANICAL",
            1,
            null,
            0,
            null,
            "MAN-P102-SCOPE-001",
            "Inspection-only scope");
        var validator = CreateValidator(
            seed,
            assets: [ToReadModel(seed.Asset), secondAsset],
            taskCatalogues: new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = ToReadModels(seed.Tasks),
                ["P-102"] = [secondTask]
            },
            compatiblePartIds: new Dictionary<string, IReadOnlyList<string>>
            {
                ["P-101"] = [seed.SparePart.Id],
                ["P-102"] = []
            });
        var request = new PlanningCandidateValidationRequest(
            ["P-101", "P-102"],
            "MW-P101-BASELINE-20261024",
            new PlanningCandidate(
            [
                new PlanningTaskPlacement(
                    "P101-PREP",
                    WindowStart,
                    WindowStart.AddHours(1),
                    ["TECH-001"]),
                new PlanningTaskPlacement(
                    "P102-INSPECTION",
                    WindowStart,
                    WindowStart.AddHours(1),
                    ["TECH-001"])
            ],
            "Tasks share a technician",
            []));

        var result = await validator.ValidateAsync(request, CancellationToken.None);

        Assert.False(result.IsFeasible);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "TECHNICIAN_SLOT_CONFLICT" &&
            violation.ResourceId == "TECH-001");
    }

    [Fact]
    public async Task ValidateAsync_SharedSparePartRequirementsAcrossAssets_UseAggregateStock()
    {
        var seed = P101AnchorSeed.Create();
        const string sharedPartId = "SHARED-FILTER";
        var secondAsset = new AssetReadModel("P-102", "PLANT-01", "Second pump", "CP-100", 2);
        var firstTask = new MaintenanceTaskReadModel(
            "P101-FILTER-REPLACEMENT",
            "P-101",
            "Filter replacement",
            60,
            "MECHANICAL",
            1,
            sharedPartId,
            1,
            null,
            "MAN-P101-001",
            "Filter replacement");
        var secondTask = new MaintenanceTaskReadModel(
            "P102-FILTER-REPLACEMENT",
            "P-102",
            "Filter replacement",
            60,
            "MECHANICAL",
            1,
            sharedPartId,
            1,
            null,
            "MAN-P102-SCOPE-001",
            "Filter replacement");
        var sharedPart = new SparePartReadModel(sharedPartId, "Shared filter", "each", 1, 0, 1);
        var validator = CreateValidator(
            seed,
            assets: [ToReadModel(seed.Asset), secondAsset],
            spareParts: [sharedPart],
            taskCatalogues: new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = [firstTask],
                ["P-102"] = [secondTask]
            },
            compatiblePartIds: new Dictionary<string, IReadOnlyList<string>>
            {
                ["P-101"] = [sharedPartId],
                ["P-102"] = [sharedPartId]
            });
        var request = new PlanningCandidateValidationRequest(
            ["P-101", "P-102"],
            "MW-P101-BASELINE-20261024",
            new PlanningCandidate(
            [
                new PlanningTaskPlacement(
                    firstTask.Id,
                    WindowStart,
                    WindowStart.AddHours(1),
                    ["TECH-001"]),
                new PlanningTaskPlacement(
                    secondTask.Id,
                    WindowStart.AddHours(1),
                    WindowStart.AddHours(2),
                    ["TECH-003"])
            ],
            "Both tasks use the shared part",
            []));

        var result = await validator.ValidateAsync(request, CancellationToken.None);

        Assert.False(result.IsFeasible);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "PART_UNAVAILABLE" &&
            violation.ResourceId == sharedPartId &&
            violation.Message.Contains("2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateAsync_UnknownWindowAndMalformedRequest_ReturnStructuredViolations()
    {
        var seed = P101AnchorSeed.Create();
        var validator = CreateValidator(seed);
        var missingWindowResult = await validator.ValidateAsync(
            CreateRequest(BaselinePlacements(), "UNKNOWN-WINDOW"),
            CancellationToken.None);

        Assert.Contains(missingWindowResult.Violations, violation => violation.Code == "WINDOW_NOT_FOUND");

        var malformedRequest = new PlanningCandidateValidationRequest(
            [],
            " ",
            new PlanningCandidate(null!, "test", []));
        var malformedResult = await validator.ValidateAsync(malformedRequest, CancellationToken.None);

        Assert.False(malformedResult.IsFeasible);
        Assert.Contains(malformedResult.Violations, violation => violation.Code == "ASSETS_REQUIRED");
        Assert.Contains(malformedResult.Violations, violation => violation.Code == "WINDOW_REQUIRED");
    }

    [Fact]
    public async Task ValidateAsync_InvalidMaintenanceWindowInterval_IsRejected()
    {
        var seed = P101AnchorSeed.Create();
        var windows = seed.MaintenanceWindows.Select(window =>
            window.Id == "MW-P101-BASELINE-20261024"
                ? new MaintenanceWindowReadModel(
                    window.Id,
                    window.Name,
                    window.Start,
                    window.Start,
                    window.TimeZoneId)
                : new MaintenanceWindowReadModel(
                    window.Id,
                    window.Name,
                    window.Start,
                    window.End,
                    window.TimeZoneId)).ToArray();
        var result = await CreateValidator(seed, windows: windows)
            .ValidateAsync(CreateRequest(BaselinePlacements()), CancellationToken.None);

        Assert.Contains(result.Violations, violation =>
            violation.Code == "INVALID_WINDOW_INTERVAL" &&
            violation.ResourceId == "MW-P101-BASELINE-20261024");
    }

    private static PlanningCandidateValidationRequest CreateRequest(
        IReadOnlyList<PlanningTaskPlacement> placements,
        string windowId = "MW-P101-BASELINE-20261024") =>
        new(
            ["P-101"],
            windowId,
            new PlanningCandidate(placements, "test candidate", []));

    private static IReadOnlyList<PlanningTaskPlacement> BaselinePlacements() =>
    [
        new(
            "P101-PREP",
            WindowStart,
            WindowStart.AddHours(1),
            ["TECH-001"]),
        new(
            "P101-BEARING-REPLACEMENT",
            WindowStart.AddHours(1),
            WindowStart.AddHours(4),
            ["TECH-001", "TECH-002"]),
        new(
            "P101-VERIFICATION",
            WindowStart.AddHours(4),
            WindowStart.AddHours(6),
            ["TECH-003"])
    ];

    private static PlanningCandidateValidator CreateValidator(
        P101SeedData seed,
        IReadOnlyList<AssetReadModel>? assets = null,
        IReadOnlyList<MaintenanceWindowReadModel>? windows = null,
        IReadOnlyList<TechnicianReadModel>? technicians = null,
        IReadOnlyList<SparePartReadModel>? spareParts = null,
        IReadOnlyDictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>? taskCatalogues = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? compatiblePartIds = null)
    {
        assets ??= [ToReadModel(seed.Asset)];
        windows ??= seed.MaintenanceWindows.Select(window => new MaintenanceWindowReadModel(
            window.Id,
            window.Name,
            window.Start,
            window.End,
            window.TimeZoneId)).ToArray();
        technicians ??= seed.Technicians.Select(technician => new TechnicianReadModel(
            technician.Id,
            technician.Name,
            technician.HourlyRate,
            technician.TechnicalSkills.Select(skill => skill.SkillCode).ToArray(),
            technician.TechnicianAvailabilities.Select(availability =>
                new TechnicianAvailabilityReadModel(
                    availability.AvailableFrom,
                    availability.AvailableTo)).ToArray())).ToArray();
        spareParts ??=
        [
            new SparePartReadModel(
                seed.SparePart.Id,
                seed.SparePart.Name,
                seed.SparePart.Unit,
                seed.SparePart.OnHandQuantity,
                seed.SparePart.ReservedQuantity,
                seed.SparePart.AvailableQuantity)
        ];
        taskCatalogues ??= new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
        {
            [seed.Asset.Id] = ToReadModels(seed.Tasks)
        };
        compatiblePartIds ??= new Dictionary<string, IReadOnlyList<string>>
        {
            [seed.Asset.Id] = [seed.SparePart.Id]
        };

        var mockQueries = new Mock<IMaintenancePlannerQueries>();
        mockQueries.Setup(queries => queries.GetAssetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(assets);
        mockQueries.Setup(queries => queries.GetMaintenanceWindowsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(windows);
        mockQueries.Setup(queries => queries.GetTechniciansAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(technicians);
        mockQueries.Setup(queries => queries.GetSparePartsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(spareParts);
        mockQueries.Setup(queries => queries.GetTaskCatalogueAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((string assetId, CancellationToken _) =>
                Task.FromResult(taskCatalogues.TryGetValue(assetId, out var catalogue)
                    ? (IReadOnlyList<MaintenanceTaskReadModel>?)catalogue
                    : null));
        mockQueries.Setup(queries => queries.GetCompatibleSparePartsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((string assetId, CancellationToken _) =>
            {
                var compatibleIds = compatiblePartIds.TryGetValue(assetId, out var ids)
                    ? ids
                    : [];
                return Task.FromResult<IReadOnlyList<SparePartReadModel>>(
                    spareParts.Where(part => compatibleIds.Contains(
                        part.Id,
                        StringComparer.OrdinalIgnoreCase)).ToArray());
            });

        var sparePartProvider = new Mock<ISparePartReadModelProvider>();
        sparePartProvider.Setup(p => p.GetSparePartsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(spareParts);
        sparePartProvider.Setup(p => p.GetCompatibleSparePartsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string assetId, string? _, CancellationToken _) =>
            {
                var compatibleIds = compatiblePartIds.TryGetValue(assetId, out var ids) ? ids : [];
                return Task.FromResult<IReadOnlyList<SparePartReadModel>>(
                    spareParts.Where(part => compatibleIds.Contains(part.Id, StringComparer.OrdinalIgnoreCase)).ToArray());
            });

        return new PlanningCandidateValidator(sparePartProvider.Object, mockQueries.Object);
    }

    private static AssetReadModel ToReadModel(Asset asset) =>
        new(asset.Id, asset.PlantId, asset.Name, asset.Model, asset.Priority);

    private static IReadOnlyList<MaintenanceTaskReadModel> ToReadModels(
        IEnumerable<MaintenanceTaskDefinition> tasks) =>
        tasks.Select(task => new MaintenanceTaskReadModel(
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
            task.SourceSection)).ToArray();
}
