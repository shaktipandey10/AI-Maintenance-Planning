using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Seeding;
using Moq;

namespace MaintenancePlanner.UnitTests;

public sealed class DeterministicPlanningSchedulerTests
{
    private static readonly DateTimeOffset WindowStart =
        new(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5));

    private static readonly string[] P101FullScope =
    [
        "P101-PREP",
        "P101-BEARING-REPLACEMENT",
        "P101-VERIFICATION"
    ];

    [Fact]
    public async Task ScheduleAsync_P101Baseline_SchedulesRequiredTasksAndValidatesAlternatives()
    {
        var fixture = CreateP101Fixture();
        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(P101FullScope),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);
        Assert.NotEmpty(result.Candidates);

        foreach (var candidate in result.Candidates)
        {
            var placements = candidate.TaskPlacements.ToDictionary(placement => placement.TaskId);

            // Validate baseline sequence: P101-PREP → P101-BEARING-REPLACEMENT → P101-VERIFICATION
            Assert.Equal(WindowStart, placements["P101-PREP"].Start);
            Assert.Equal(WindowStart.AddHours(1), placements["P101-PREP"].End);

            Assert.True(placements["P101-PREP"].End <= placements["P101-BEARING-REPLACEMENT"].Start);
            Assert.Equal(TimeSpan.FromHours(3),
                placements["P101-BEARING-REPLACEMENT"].End - placements["P101-BEARING-REPLACEMENT"].Start);

            Assert.True(placements["P101-BEARING-REPLACEMENT"].End <= placements["P101-VERIFICATION"].Start);
            Assert.Equal(TimeSpan.FromHours(2),
                placements["P101-VERIFICATION"].End - placements["P101-VERIFICATION"].Start);

            // Verify crew size for bearing replacement (must be 2)
            Assert.Equal(2, placements["P101-BEARING-REPLACEMENT"].TechnicianIds.Count);

            // Verify all tasks are in requested list
            Assert.Contains("P101-PREP", candidate.RequestedTaskIds);
            Assert.Contains("P101-BEARING-REPLACEMENT", candidate.RequestedTaskIds);
            Assert.Contains("P101-VERIFICATION", candidate.RequestedTaskIds);

            var validation = await fixture.Validator.ValidateAsync(
                new PlanningCandidateValidationRequest(
                    ["P-101"],
                    "MW-P101-BASELINE-20261024",
                    candidate),
                CancellationToken.None);
            Assert.True(validation.IsFeasible);
        }
    }

    [Fact]
    public async Task ScheduleAsync_UnavailableBearingAndReducedWindow_ReturnsInfeasibleWithoutSubstitution()
    {
        var fixture = CreateP101Fixture(
            availableBearingQuantity: 0,
            windowId: "MW-P101-REDUCED-20261024",
            includeP102Inspection: true);
        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(P101FullScope, "MW-P101-REDUCED-20261024"),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.Infeasible, result.Outcome);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Violations, violation => violation.Code == "PART_UNAVAILABLE");
        Assert.Contains(result.Violations, violation => violation.Code == "TASKS_EXCEED_WINDOW");
        Assert.DoesNotContain(result.Candidates.SelectMany(candidate => candidate.TaskPlacements),
            placement => placement.TaskId == "P102-INSPECTION");
    }

    [Fact]
    public async Task ScheduleAsync_InsufficientQualifiedBearingTechnicians_ReturnsInfeasible()
    {
        var seed = P101AnchorSeed.Create();
        var technicians = ToTechnicianReadModels(seed.Technicians)
            .Where(technician => technician.Id != "TECH-002")
            .ToArray();
        var fixture = CreateP101Fixture(technicians: technicians);

        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(P101FullScope),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.Infeasible, result.Outcome);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "INSUFFICIENT_QUALIFIED_TECHNICIANS" &&
            violation.TaskId == "P101-BEARING-REPLACEMENT");
    }

    [Fact]
    public async Task ScheduleAsync_EmptyTechnicianData_ReturnsDataUnavailable()
    {
        var fixture = CreateP101Fixture(technicians: []);

        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(P101FullScope),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.DataUnavailable, result.Outcome);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "TECHNICIAN_DATA_UNAVAILABLE" &&
            violation.ResourceId == "Technicians");
    }

    [Fact]
    public async Task ScheduleAsync_SharedTechnicianAcrossAssets_SerializesAssignments()
    {
        var p101 = P101AnchorSeed.Create();
        var p102 = CreateAsset("P-102", priority: 2);
        var secondTask = CreateTask("P102-INSPECTION", "P-102", "MECHANICAL");
        var technician = new TechnicianReadModel(
            "TECH-001",
            "Shared technician",
            40,
            ["MECHANICAL"],
            [new TechnicianAvailabilityReadModel(WindowStart, WindowStart.AddHours(8))]);
        var fixture = CreateFixture(
            [ToAssetReadModel(p101.Asset), p102],
            new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = [ToTaskReadModel(p101.Tasks[0])],
                ["P-102"] = [secondTask]
            },
            [technician],
            [],
            new Dictionary<string, IReadOnlyList<string>>());
        var request = new PlanningScheduleRequest(
            ["P-101", "P-102"],
            "MW-P101-BASELINE-20261024",
            ["P101-PREP", "P102-INSPECTION"]);

        var result = await fixture.Scheduler.ScheduleAsync(request, CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);
        var candidate = Assert.Single(result.Candidates);
        var prep = candidate.TaskPlacements.Single(placement => placement.TaskId == "P101-PREP");
        var inspection = candidate.TaskPlacements.Single(placement => placement.TaskId == "P102-INSPECTION");
        Assert.True(prep.End <= inspection.Start || inspection.End <= prep.Start);
        Assert.All(candidate.TaskPlacements, placement =>
            Assert.Contains("TECH-001", placement.TechnicianIds));
    }

    [Fact]
    public async Task ScheduleAsync_RequiredTaskAutomaticallyIncludesItsPredecessors()
    {
        var fixture = CreateP101Fixture();

        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(["P101-BEARING-REPLACEMENT"]),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);
        Assert.All(result.Candidates, candidate =>
        {
            // Requesting bearing replacement should include full sequence: prep → bearing → verification
            Assert.Contains(candidate.TaskPlacements, placement => placement.TaskId == "P101-PREP");
            Assert.Contains(candidate.TaskPlacements,
                placement => placement.TaskId == "P101-BEARING-REPLACEMENT");
            Assert.Contains(candidate.TaskPlacements,
                placement => placement.TaskId == "P101-VERIFICATION");
        });
    }

    [Fact]
    public async Task ScheduleAsync_AggregateSharedPartDemandExceedsStock_ReturnsInfeasible()
    {
        var p101 = P101AnchorSeed.Create();
        var p102 = CreateAsset("P-102", priority: 2);
        const string sharedPartId = "SHARED-FILTER";
        var firstTask = CreateTask("P101-FILTER", "P-101", "MECHANICAL", sharedPartId);
        var secondTask = CreateTask("P102-FILTER", "P-102", "MECHANICAL", sharedPartId);
        var sharedPart = new SparePartReadModel(sharedPartId, "Shared filter", "each", 1, 0, 1);
        var fixture = CreateFixture(
            [ToAssetReadModel(p101.Asset), p102],
            new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = [firstTask],
                ["P-102"] = [secondTask]
            },
            ToTechnicianReadModels(p101.Technicians),
            [sharedPart],
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["P-101"] = [sharedPartId],
                ["P-102"] = [sharedPartId]
            });

        var result = await fixture.Scheduler.ScheduleAsync(
            new PlanningScheduleRequest(
                ["P-101", "P-102"],
                "MW-P101-BASELINE-20261024",
                [firstTask.Id, secondTask.Id]),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.Infeasible, result.Outcome);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "PART_UNAVAILABLE" && violation.ResourceId == sharedPartId);
    }

    [Fact]
    public async Task ScheduleAsync_MissingRequiredPartData_ReturnsDataUnavailable()
    {
        var fixture = CreateP101Fixture(spareParts: []);

        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(["P101-BEARING-REPLACEMENT"]),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.DataUnavailable, result.Outcome);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "PART_DATA_UNAVAILABLE" &&
            violation.TaskId == "P101-BEARING-REPLACEMENT" &&
            violation.ResourceId == "BR-101");
    }

    [Fact]
    public async Task ScheduleAsync_MissingPredecessorRecord_IsRejectedAsInvalidInput()
    {
        var p101 = P101AnchorSeed.Create();
        var taskWithMissingPredecessor = CreateTask("P101-REPAIR", "P-101", "MECHANICAL") with
        {
            PredecessorTaskId = "P101-MISSING-PREDECESSOR"
        };
        var fixture = CreateFixture(
            [ToAssetReadModel(p101.Asset)],
            new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = [taskWithMissingPredecessor]
            },
            ToTechnicianReadModels(p101.Technicians),
            [],
            new Dictionary<string, IReadOnlyList<string>>());

        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest([taskWithMissingPredecessor.Id]),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.InvalidInput, result.Outcome);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "TASK_NOT_FOUND" &&
            violation.ResourceId == "P101-MISSING-PREDECESSOR");
    }

    [Fact]
    public async Task ScheduleAsync_DependencyCycle_IsRejectedAsInvalidInput()
    {
        var p101 = P101AnchorSeed.Create();
        var first = CreateTask("P101-TASK-A", "P-101", "MECHANICAL") with
        {
            PredecessorTaskId = "P101-TASK-B"
        };
        var second = CreateTask("P101-TASK-B", "P-101", "MECHANICAL") with
        {
            PredecessorTaskId = "P101-TASK-A"
        };
        var fixture = CreateFixture(
            [ToAssetReadModel(p101.Asset)],
            new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = [first, second]
            },
            ToTechnicianReadModels(p101.Technicians),
            [],
            new Dictionary<string, IReadOnlyList<string>>());

        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest([first.Id]),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.InvalidInput, result.Outcome);
        Assert.Contains(result.Violations, violation => violation.Code == "TASK_DEPENDENCY_CYCLE");
    }

    [Fact]
    public async Task ScheduleAsync_InvalidDurationAndMalformedRequiredSkills_AreRejected()
    {
        var p101 = P101AnchorSeed.Create();
        var invalidDuration = CreateTask("P101-INVALID-DURATION", "P-101", "MECHANICAL") with
        {
            DurationMinutes = 0
        };
        var invalidSkills = CreateTask("P101-INVALID-SKILLS", "P-101", " , ");

        foreach (var invalidTask in new[] { invalidDuration, invalidSkills })
        {
            var fixture = CreateFixture(
                [ToAssetReadModel(p101.Asset)],
                new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
                {
                    ["P-101"] = [invalidTask]
                },
                ToTechnicianReadModels(p101.Technicians),
                [],
                new Dictionary<string, IReadOnlyList<string>>());

            var result = await fixture.Scheduler.ScheduleAsync(
                CreateRequest([invalidTask.Id]),
                CancellationToken.None);

            Assert.Equal(PlanningScheduleOutcome.InvalidInput, result.Outcome);
            Assert.Contains(result.Violations, violation =>
                violation.Code == "INVALID_TASK_REQUIREMENTS" &&
                violation.TaskId == invalidTask.Id);
        }
    }

    [Fact]
    public async Task ScheduleAsync_RepeatedCallsProduceDeterministicCandidates()
    {
        var fixture = CreateP101Fixture();
        var request = CreateRequest(P101FullScope);

        var first = await fixture.Scheduler.ScheduleAsync(request, CancellationToken.None);
        var second = await fixture.Scheduler.ScheduleAsync(request, CancellationToken.None);

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(CandidateSignature(first.Candidates), CandidateSignature(second.Candidates));
    }

    [Fact]
    public async Task ScheduleAsync_WhenPoliciesFindDistinctFeasibleSchedules_ReturnsDistinctAlternatives()
    {
        var fixture = CreateP101Fixture();

        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(P101FullScope),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);
        Assert.Equal(2, result.Candidates.Count);
        Assert.NotEqual(
            CandidateSignature([result.Candidates[0]]),
            CandidateSignature([result.Candidates[1]]));

        foreach (var candidate in result.Candidates)
        {
            var validation = await fixture.Validator.ValidateAsync(
                new PlanningCandidateValidationRequest(
                    ["P-101"],
                    "MW-P101-BASELINE-20261024",
                    candidate),
                CancellationToken.None);
            Assert.True(validation.IsFeasible);
        }
    }

    [Fact]
    public async Task ScheduleAsync_CrewsDifferingOnlyByTechnicianOrder_AreDeduplicated()
    {
        var p101 = P101AnchorSeed.Create();
        var twoPersonTask = CreateTask("P101-TWO-PERSON", "P-101", "MECHANICAL") with
        {
            RequiredCrewSize = 2
        };
        var technicians = new[]
        {
            new TechnicianReadModel(
                "TECH-A",
                "Higher-cost technician",
                50,
                ["MECHANICAL"],
                [new TechnicianAvailabilityReadModel(WindowStart, WindowStart.AddHours(8))]),
            new TechnicianReadModel(
                "TECH-Z",
                "Lower-cost technician",
                10,
                ["MECHANICAL"],
                [new TechnicianAvailabilityReadModel(WindowStart, WindowStart.AddHours(8))])
        };
        var fixture = CreateFixture(
            [ToAssetReadModel(p101.Asset)],
            new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = [twoPersonTask]
            },
            technicians,
            [],
            new Dictionary<string, IReadOnlyList<string>>());

        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest([twoPersonTask.Id]),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(
            ["TECH-A", "TECH-Z"],
            candidate.TaskPlacements.Single().TechnicianIds.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScheduleAsync_MissingAndUnknownScopeOrWindow_ReturnsStructuredOutcomes()
    {
        var fixture = CreateP101Fixture();

        var missingScope = await fixture.Scheduler.ScheduleAsync(
            new PlanningScheduleRequest(["P-101"], "MW-P101-BASELINE-20261024", []),
            CancellationToken.None);
        Assert.Equal(PlanningScheduleOutcome.ClarificationRequired, missingScope.Outcome);
        Assert.NotEmpty(missingScope.ClarificationQuestions);

        var missingWindow = await fixture.Scheduler.ScheduleAsync(
            new PlanningScheduleRequest(["P-101"], null, ["P101-PREP"]),
            CancellationToken.None);
        Assert.Equal(PlanningScheduleOutcome.ClarificationRequired, missingWindow.Outcome);

        var unknownTask = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(["UNKNOWN-TASK"]),
            CancellationToken.None);
        Assert.Equal(PlanningScheduleOutcome.InvalidInput, unknownTask.Outcome);
        Assert.Contains(unknownTask.Violations, violation => violation.Code == "TASK_NOT_FOUND");

        var unknownWindow = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(["P101-PREP"], "UNKNOWN-WINDOW"),
            CancellationToken.None);
        Assert.Equal(PlanningScheduleOutcome.InvalidInput, unknownWindow.Outcome);
        Assert.Contains(unknownWindow.Violations, violation => violation.Code == "WINDOW_NOT_FOUND");
    }

    [Fact]
    public async Task ScheduleAsync_AmbiguousTaskId_IsRejected()
    {
        var p101 = P101AnchorSeed.Create();
        var p102 = CreateAsset("P-102", priority: 2);
        var duplicateTask = CreateTask("P101-PREP", "P-102", "MECHANICAL");
        var fixture = CreateFixture(
            [ToAssetReadModel(p101.Asset), p102],
            new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = [ToTaskReadModel(p101.Tasks[0])],
                ["P-102"] = [duplicateTask]
            },
            ToTechnicianReadModels(p101.Technicians),
            [],
            new Dictionary<string, IReadOnlyList<string>>());

        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(["P101-PREP"]),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.InvalidInput, result.Outcome);
        Assert.Contains(result.Violations, violation => violation.Code == "AMBIGUOUS_TASK_ID");
    }

    [Fact]
    public async Task ScheduleAsync_BoundedGreedyFailure_IsNotReportedAsProvenInfeasibility()
    {
        var p101 = P101AnchorSeed.Create();
        var p102 = CreateAsset("P-102", priority: 2);
        var secondTask = CreateTask("P102-INSPECTION", "P-102", "MECHANICAL");
        var technician = new TechnicianReadModel(
            "TECH-001",
            "Shared technician",
            40,
            ["MECHANICAL"],
            [new TechnicianAvailabilityReadModel(WindowStart, WindowStart.AddHours(1))]);
        var fixture = CreateFixture(
            [ToAssetReadModel(p101.Asset), p102],
            new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = [ToTaskReadModel(p101.Tasks[0])],
                ["P-102"] = [secondTask]
            },
            [technician],
            [],
            new Dictionary<string, IReadOnlyList<string>>(),
            [
                new MaintenanceWindowReadModel(
                    "MW-SHORT",
                    "Synthetic one-hour window",
                    WindowStart,
                    WindowStart.AddHours(1),
                    "Asia/Kolkata")
            ]);

        var result = await fixture.Scheduler.ScheduleAsync(
            new PlanningScheduleRequest(
                ["P-101", "P-102"],
                "MW-SHORT",
                ["P101-PREP", "P102-INSPECTION"]),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.SearchLimitReached, result.Outcome);
        Assert.Empty(result.Candidates);
        Assert.DoesNotContain(result.Violations, violation => violation.Code == "INFEASIBLE");
    }

    [Fact]
    public async Task ScheduleAsync_RequiredTaskOnUnselectedAsset_IsRejectedAsOutOfScope()
    {
        var fixture = CreateP101Fixture(includeP102Inspection: true);

        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(["P102-INSPECTION"]),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.InvalidInput, result.Outcome);
        Assert.Contains(result.Violations, violation => violation.Code == "TASK_OUT_OF_SCOPE");
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task ScheduleAsync_P101BaselineWithBearingRequested_SchedulesFullSequenceWithCorrectTiming()
    {
        var fixture = CreateP101Fixture();
        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(["P101-BEARING-REPLACEMENT"]),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);
        Assert.All(result.Candidates, candidate =>
        {
            var placements = candidate.TaskPlacements.ToDictionary(placement => placement.TaskId);

            // Verify all three tasks are scheduled when bearing replacement is requested
            Assert.NotNull(placements["P101-PREP"]);
            Assert.NotNull(placements["P101-BEARING-REPLACEMENT"]);
            Assert.NotNull(placements["P101-VERIFICATION"]);

            // Verify exact timing: 08:00–09:00, 09:00–12:00, 12:00–14:00
            Assert.Equal(WindowStart, placements["P101-PREP"].Start);
            Assert.Equal(WindowStart.AddHours(1), placements["P101-PREP"].End);

            Assert.Equal(WindowStart.AddHours(1), placements["P101-BEARING-REPLACEMENT"].Start);
            Assert.Equal(WindowStart.AddHours(4), placements["P101-BEARING-REPLACEMENT"].End);

            Assert.Equal(WindowStart.AddHours(4), placements["P101-VERIFICATION"].Start);
            Assert.Equal(WindowStart.AddHours(6), placements["P101-VERIFICATION"].End);

            // Verify requested vs dependency classification
            Assert.Single(candidate.RequestedTaskIds, id =>
                string.Equals(id, "P101-BEARING-REPLACEMENT", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, candidate.DependencyTaskIds.Count);
            Assert.Contains("P101-PREP", candidate.DependencyTaskIds, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("P101-VERIFICATION", candidate.DependencyTaskIds, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ScheduleAsync_InspectionOnlyScope_DoesNotIncludeBearingTasks()
    {
        var p101 = P101AnchorSeed.Create();
        var p102 = CreateAsset("P-102", priority: 2);
        var inspectionTask = CreateTask("P102-INSPECTION", "P-102", "MECHANICAL");
        var fixture = CreateFixture(
            [ToAssetReadModel(p101.Asset), p102],
            new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
            {
                ["P-101"] = [ToTaskReadModel(p101.Tasks[0]), ToTaskReadModel(p101.Tasks[1]), ToTaskReadModel(p101.Tasks[2])],
                ["P-102"] = [inspectionTask]
            },
            ToTechnicianReadModels(p101.Technicians),
            [new SparePartReadModel(p101.SparePart.Id, p101.SparePart.Name, p101.SparePart.Unit, 1, 0, 1)],
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["P-101"] = [p101.SparePart.Id],
                ["P-102"] = []
            });

        var result = await fixture.Scheduler.ScheduleAsync(
            new PlanningScheduleRequest(
                ["P-101", "P-102"],
                "MW-P101-BASELINE-20261024",
                [inspectionTask.Id]),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Single(candidate.TaskPlacements, p => p.TaskId == "P102-INSPECTION");
            Assert.DoesNotContain(candidate.TaskPlacements, p => p.TaskId == "P101-BEARING-REPLACEMENT");
            Assert.DoesNotContain(candidate.TaskPlacements, p => p.TaskId == "P101-VERIFICATION");
        });
    }

    [Fact]
    public async Task ScheduleAsync_FourHourWindowWithUnavailableBearing_ReturnsInfeasibleWithoutGeneration()
    {
        var fixture = CreateP101Fixture(
            availableBearingQuantity: 0,
            windowId: "MW-P101-REDUCED-20261024");
        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(["P101-BEARING-REPLACEMENT"], "MW-P101-REDUCED-20261024"),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.Infeasible, result.Outcome);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Violations, violation =>
            violation.Code == "PART_UNAVAILABLE" &&
            violation.ResourceId == "BR-101" &&
            violation.TaskId == "P101-BEARING-REPLACEMENT");
        Assert.Contains(result.Violations, violation =>
            violation.Code == "TASKS_EXCEED_WINDOW" &&
            violation.TaskId == "P101-VERIFICATION");
    }

    [Fact]
    public async Task ScheduleAsync_ReducedWindowOverride_BothPartUnavailableAndTasksExceedWindowViolations()
    {
        var fixture = CreateP101Fixture(
            availableBearingQuantity: 0,
            windowId: "MW-P101-REDUCED-20261024");
        var result = await fixture.Scheduler.ScheduleAsync(
            CreateRequest(["P101-BEARING-REPLACEMENT"], "MW-P101-REDUCED-20261024"),
            CancellationToken.None);

        Assert.Equal(PlanningScheduleOutcome.Infeasible, result.Outcome);
        var violations = result.Violations;
        Assert.NotEmpty(violations);

        var partUnavailable = violations.FirstOrDefault(v => v.Code == "PART_UNAVAILABLE");
        Assert.NotNull(partUnavailable);
        Assert.Equal("BR-101", partUnavailable.ResourceId);

        var tasksExceed = violations.FirstOrDefault(v => v.Code == "TASKS_EXCEED_WINDOW");
        Assert.NotNull(tasksExceed);
        Assert.Equal("P101-VERIFICATION", tasksExceed.TaskId);
    }

    private static PlanningScheduleRequest CreateRequest(
        IReadOnlyList<string> requiredTaskIds,
        string windowId = "MW-P101-BASELINE-20261024") =>
        new(["P-101"], windowId, requiredTaskIds);

    private static SchedulerFixture CreateP101Fixture(
        decimal availableBearingQuantity = 1,
        string windowId = "MW-P101-BASELINE-20261024",
        IReadOnlyList<TechnicianReadModel>? technicians = null,
        bool includeP102Inspection = false,
        IReadOnlyList<SparePartReadModel>? spareParts = null)
    {
        var seed = P101AnchorSeed.Create();
        var assets = new List<AssetReadModel> { ToAssetReadModel(seed.Asset) };
        var catalogues = new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>
        {
            [seed.Asset.Id] = seed.Tasks.Select(ToTaskReadModel).ToArray()
        };
        var compatibility = new Dictionary<string, IReadOnlyList<string>>
        {
            [seed.Asset.Id] = [seed.SparePart.Id]
        };
        if (includeP102Inspection)
        {
            assets.Add(CreateAsset("P-102", priority: 2));
            catalogues["P-102"] = [CreateTask("P102-INSPECTION", "P-102", "MECHANICAL")];
            compatibility["P-102"] = [];
        }

        var windows = seed.MaintenanceWindows.Select(window => new MaintenanceWindowReadModel(
            window.Id,
            window.Name,
            window.Start,
            window.Id == windowId && window.Id == "MW-P101-REDUCED-20261024"
                ? new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(5.5))
                : window.End,
            window.TimeZoneId)).ToArray();
        var part = new SparePartReadModel(
            seed.SparePart.Id,
            seed.SparePart.Name,
            seed.SparePart.Unit,
            1,
            1 - availableBearingQuantity,
            availableBearingQuantity);

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

    private static string CandidateSignature(IEnumerable<PlanningCandidate> candidates) =>
        string.Join(
            "||",
            candidates.Select(candidate =>
                string.Join(
                    ";",
                    candidate.TaskPlacements
                        .OrderBy(placement => placement.TaskId, StringComparer.OrdinalIgnoreCase)
                        .Select(placement =>
                            $"{placement.TaskId}|{placement.Start:O}|{placement.End:O}|" +
                            string.Join(",", placement.TechnicianIds.Order(StringComparer.OrdinalIgnoreCase))))));

    private sealed record SchedulerFixture(
        DeterministicPlanningScheduler Scheduler,
        PlanningCandidateValidator Validator);
}
