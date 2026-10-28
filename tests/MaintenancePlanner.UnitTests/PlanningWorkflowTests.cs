using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.ManualEvidence;
using MaintenancePlanner.Infrastructure.Seeding;
using Moq;

namespace MaintenancePlanner.UnitTests;

public sealed class PlanningWorkflowTests
{
    private static readonly DateTimeOffset WindowStart =
        new(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5));

    private static readonly PlanningRequest CompleteRequest = new(
        "Replace the P-101 bearing.",
        ["P-101"],
        "MW-P101-BASELINE-20261024");

    private static readonly string[] RequiredTasks =
    [
        "P101-BEARING-REPLACEMENT"
    ];

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ExecuteAsync_MissingAssetOrWindow_ReturnsClarification(
        bool includeAsset,
        bool includeWindow)
    {
        var fixture = CreateFixture();
        var request = new PlanningRequest(
            "Replace the bearing.",
            includeAsset ? ["P-101"] : [],
            includeWindow ? "MW-P101-BASELINE-20261024" : null);

        var result = await fixture.Workflow.ExecuteAsync(request, CancellationToken.None);

        var clarification = Assert.IsType<PlanningClarificationRequiredResult>(result);
        Assert.NotEmpty(clarification.Questions);
        Assert.Empty(clarification.Interpretation.ProposedTaskIds);
        fixture.Interpreter.Verify(
            interpreter => interpreter.InterpretAsync(
                It.IsAny<PlanningRequest>(),
                It.IsAny<IReadOnlyList<AssetReadModel>>(),
                It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        fixture.SchedulerDependencies.VerifyNoResourcePlanningQueries();
    }

    [Fact]
    public async Task ExecuteAsync_UnknownAsset_ReturnsInvalidInput()
    {
        var fixture = CreateFixture();
        fixture.Queries.Setup(queries => queries.GetAssetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        var invalid = Assert.IsType<PlanningInvalidInputResult>(result);
        Assert.Contains(invalid.Violations, violation =>
            violation.Code == "ASSET_NOT_FOUND" && violation.ResourceId == "P-101");
        fixture.Interpreter.Verify(
            interpreter => interpreter.InterpretAsync(
                It.IsAny<PlanningRequest>(),
                It.IsAny<IReadOnlyList<AssetReadModel>>(),
                It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTask_ReturnsInvalidInputWithoutScheduling()
    {
        var fixture = CreateFixture();
        var request = CompleteRequest;
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    request,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(Interpretation("P101-UNKNOWN-TASK"));

        var result = await fixture.Workflow.ExecuteAsync(request, CancellationToken.None);

        var invalid = Assert.IsType<PlanningInvalidInputResult>(result);
        Assert.Contains(invalid.Violations, violation =>
            violation.Code == "TASK_NOT_FOUND" &&
            violation.TaskId == "P101-UNKNOWN-TASK" &&
            violation.ResourceId == "P101-UNKNOWN-TASK");
        fixture.SchedulerDependencies.VerifyNoResourcePlanningQueries();
    }

    [Fact]
    public async Task ExecuteAsync_ClearUnconfirmedTaskScopeGeneratesCandidates()
    {
        var fixture = CreateFixture();
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(Interpretation(RequiredTasks));

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        var generated = Assert.IsType<PlanningCandidatesGeneratedResult>(result);
        Assert.NotEmpty(generated.Candidates);
        Assert.All(generated.Candidates, candidate =>
            Assert.Equal(RequiredTasks, candidate.RequestedTaskIds));
    }

    [Fact]
    public async Task ExecuteAsync_TaskFromUnselectedAssetRequiresClarification()
    {
        var fixture = CreateFixture(includeP102: true);
        var request = CompleteRequest;
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    request,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(Interpretation("P102-INSPECTION"));

        var result = await fixture.Workflow.ExecuteAsync(request, CancellationToken.None);

        var clarification = Assert.IsType<PlanningClarificationRequiredResult>(result);
        Assert.Contains(clarification.Questions, question =>
            question.Contains("unselected asset P-102", StringComparison.OrdinalIgnoreCase));
        fixture.SchedulerDependencies.VerifyNoResourcePlanningQueries();
    }

    [Fact]
    public async Task ExecuteAsync_MissingInventoryIsDataUnavailable_WhileConfirmedShortageIsInfeasible()
    {
        var missingInventoryFixture = CreateFixture(spareParts: []);
        missingInventoryFixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(Interpretation(RequiredTasks));

        var missingInventoryResult = await missingInventoryFixture.Workflow.ExecuteAsync(
            CompleteRequest,
            CancellationToken.None);

        var unavailable = Assert.IsType<PlanningDataUnavailableResult>(missingInventoryResult);
        Assert.Contains(unavailable.Violations, violation =>
            violation.Code == "PART_DATA_UNAVAILABLE" &&
            violation.TaskId == "P101-BEARING-REPLACEMENT" &&
            violation.ResourceId == "BR-101");

        var insufficientInventoryFixture = CreateFixture(availableBearingQuantity: 0);
        insufficientInventoryFixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(Interpretation(RequiredTasks));

        var insufficientInventoryResult = await insufficientInventoryFixture.Workflow.ExecuteAsync(
            CompleteRequest,
            CancellationToken.None);

        var infeasible = Assert.IsType<PlanningInfeasibleResult>(insufficientInventoryResult);
        Assert.Contains(infeasible.Violations, violation =>
            violation.Code == "PART_UNAVAILABLE" &&
            violation.TaskId == "P101-BEARING-REPLACEMENT" &&
            violation.ResourceId == "BR-101");
    }

    [Fact]
    public async Task ExecuteAsync_CandidatesGenerated_PreservesManualHistoryAndCandidateEvidence()
    {
        var fixture = CreateFixture();
        var interpretation = Interpretation(RequiredTasks);
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(interpretation);
        var manualEvidence = new PlanningEvidenceReference(
            PlanningEvidenceSourceKind.MaintenanceManual,
            "MAN-P101-001:Planning assumptions: Bearing replacement",
            "MAN-P101-001",
            "1.0",
            null,
            "Planning assumptions: Bearing replacement",
            "Synthetic manual excerpt.");
        fixture.EvidenceRetriever.Setup(retriever => retriever.SearchAsync(
                It.IsAny<string>(),
                "MAN-P101-001",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([manualEvidence]);

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        var generated = Assert.IsType<PlanningCandidatesGeneratedResult>(result);
        Assert.NotEmpty(generated.Candidates);
        Assert.Same(interpretation, generated.Interpretation);
        Assert.Contains(generated.EvidenceReferences, evidence =>
            evidence.SourceKind == PlanningEvidenceSourceKind.MaintenanceHistory &&
            evidence.DocumentId == "HIST-MANUAL-01");
        Assert.Contains(generated.EvidenceReferences, evidence => evidence == manualEvidence);
        Assert.All(generated.Candidates, candidate =>
        {
            Assert.Equal(RequiredTasks, candidate.RequestedTaskIds);
            Assert.Contains("P101-PREP", candidate.DependencyTaskIds);
            Assert.DoesNotContain("P101-PREP", candidate.RequestedTaskIds);
            Assert.Contains(candidate.EvidenceReferences, evidence => evidence == manualEvidence);
            Assert.Contains(candidate.EvidenceReferences, evidence =>
                evidence.SourceKind == PlanningEvidenceSourceKind.MaintenanceHistory &&
                evidence.DocumentId == "HIST-MANUAL-01");
        });
        fixture.EvidenceRetriever.Verify(retriever => retriever.SearchAsync(
            It.IsAny<string>(),
            "MAN-P101-001",
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_MissingManualEvidenceReturnsDataUnavailableWithoutScheduling()
    {
        var fixture = CreateFixture();
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(Interpretation(RequiredTasks));
        fixture.EvidenceRetriever.Setup(retriever => retriever.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        var unavailable = Assert.IsType<PlanningDataUnavailableResult>(result);
        Assert.Contains(unavailable.Violations, violation =>
            violation.Code == "MANUAL_EVIDENCE_UNAVAILABLE");
        fixture.SchedulerDependencies.VerifyNoResourcePlanningQueries();
    }

    [Fact]
    public async Task ExecuteAsync_ConflictingManualVersionsReturnsDataUnavailableWithoutScheduling()
    {
        var fixture = CreateFixture();
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(Interpretation(RequiredTasks));
        fixture.EvidenceRetriever.Setup(retriever => retriever.SearchAsync(
                It.IsAny<string>(),
                "MAN-P101-001",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PlanningEvidenceReference(
                    PlanningEvidenceSourceKind.MaintenanceManual,
                    "MAN-P101-001:section:1",
                    "MAN-P101-001",
                    "1.0",
                    1,
                    "Bearing replacement",
                    "Evidence version one."),
                new PlanningEvidenceReference(
                    PlanningEvidenceSourceKind.MaintenanceManual,
                    "MAN-P101-001:section:1",
                    "MAN-P101-001",
                    "2.0",
                    1,
                    "Bearing replacement",
                    "Evidence version two.")
            ]);

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        var unavailable = Assert.IsType<PlanningDataUnavailableResult>(result);
        Assert.Contains(unavailable.Violations, violation =>
            violation.Code == "MANUAL_EVIDENCE_CONFLICT");
        fixture.SchedulerDependencies.VerifyNoResourcePlanningQueries();
    }

    [Fact]
    public async Task ExecuteAsync_UnresolvedScopeClarifiesWithoutInventingTasksOrQueryingResources()
    {
        var fixture = CreateFixture();
        var unresolvedInterpretation = new PlanningInterpretation(
            "Bearing maintenance",
            string.Empty,
            ["P-101"],
            [],
            [],
            []);
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(unresolvedInterpretation);

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        var clarification = Assert.IsType<PlanningClarificationRequiredResult>(result);
        Assert.Empty(clarification.Interpretation.ProposedTaskIds);
        Assert.Contains(clarification.Questions, question => question.Contains("task scope", StringComparison.OrdinalIgnoreCase));
        fixture.SchedulerDependencies.VerifyNoResourcePlanningQueries();
        fixture.EvidenceRetriever.Verify(retriever => retriever.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ProposedTaskWithoutRequestMapping_ClarifiesInsteadOfScheduling()
    {
        var fixture = CreateFixture();
        var interpretation = Interpretation(RequiredTasks) with { ScopeMappings = [] };
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(interpretation);

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        var clarification = Assert.IsType<PlanningClarificationRequiredResult>(result);
        Assert.Contains(clarification.Questions, question =>
            question.Contains("each required task", StringComparison.OrdinalIgnoreCase));
        fixture.SchedulerDependencies.VerifyNoResourcePlanningQueries();
        fixture.EvidenceRetriever.Verify(retriever => retriever.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_RequestExcerptIsNotRequiredToEstablishScope()
    {
        var fixture = CreateFixture();
        var interpretation = Interpretation(RequiredTasks) with
        {
            ScopeMappings =
            [
                new PlanningRequestScopeMapping(
                    "P101-BEARING-REPLACEMENT",
                    "P-101",
                    null)
            ]
        };
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(interpretation);

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        Assert.IsType<PlanningCandidatesGeneratedResult>(result);
    }

    [Fact]
    public async Task ExecuteAsync_RequestMappingAssetMustMatchAuthoritativeTaskAsset()
    {
        var fixture = CreateFixture();
        var interpretation = Interpretation(RequiredTasks) with
        {
            ScopeMappings =
            [
                new PlanningRequestScopeMapping(
                    "P101-BEARING-REPLACEMENT",
                    "P-101-OTHER",
                    "Replace the P-101 bearing")
            ]
        };
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(interpretation);

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        var clarification = Assert.IsType<PlanningClarificationRequiredResult>(result);
        Assert.Contains(clarification.Questions, question =>
            question.Contains("mapping names P-101-OTHER", StringComparison.OrdinalIgnoreCase));
        fixture.SchedulerDependencies.VerifyNoResourcePlanningQueries();
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedInspectionMappingForBearingRequestRequiresClarification()
    {
        var fixture = CreateFixture(includeP101Inspection: true);
        var interpretation = Interpretation("P101-INSPECTION") with
        {
            ScopeResolution = PlanningScopeResolution.Unsupported,
            MissingInformation = ["Clarify whether inspection or bearing replacement is intended."],
            ScopeMappings =
            [
                new PlanningRequestScopeMapping(
                    "P101-INSPECTION",
                    "P-101",
                    "P-101")
            ]
        };
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(interpretation);

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        var clarification = Assert.IsType<PlanningClarificationRequiredResult>(result);
        Assert.Contains(clarification.Questions, question =>
            question.Contains("Clarify whether inspection or bearing replacement is intended.", StringComparison.OrdinalIgnoreCase));
        fixture.SchedulerDependencies.VerifyNoResourcePlanningQueries();
        fixture.EvidenceRetriever.Verify(retriever => retriever.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedP102MappingForP101RequestRequiresClarification()
    {
        var fixture = CreateFixture(includeP102: true);
        var request = CompleteRequest with
        {
            AssetIds = ["P-101", "P-102"]
        };
        var interpretation = new PlanningInterpretation(
            "Bearing replacement",
            "Replace the P-101 bearing",
            ["P-101", "P-102"],
            ["P102-INSPECTION"],
            [],
            [])
        {
            ScopeResolution = PlanningScopeResolution.Unsupported,
            MissingInformation = ["Clarify which selected asset is intended for the requested bearing work."],
            ScopeMappings =
            [
                new PlanningRequestScopeMapping(
                    "P102-INSPECTION",
                    "P-102",
                    "P-101")
            ]
        };
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    request,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(interpretation);

        var result = await fixture.Workflow.ExecuteAsync(request, CancellationToken.None);

        var clarification = Assert.IsType<PlanningClarificationRequiredResult>(result);
        Assert.Contains(clarification.Questions, question =>
            question.Contains("Clarify which selected asset", StringComparison.OrdinalIgnoreCase));
        fixture.SchedulerDependencies.VerifyNoResourcePlanningQueries();
        fixture.EvidenceRetriever.Verify(retriever => retriever.SearchAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_InterpreterMissingInformationReturnsClarification()
    {
        var fixture = CreateFixture();
        var missingInformation = new[] { "Clarify whether inspection or replacement is requested." };
        var interpretation = new PlanningInterpretation(
            "Bearing work",
            string.Empty,
            ["P-101"],
            [],
            missingInformation,
            []);
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(interpretation);

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        var clarification = Assert.IsType<PlanningClarificationRequiredResult>(result);
        Assert.Equal(missingInformation, clarification.Questions);
        Assert.Same(interpretation, clarification.Interpretation);
        fixture.SchedulerDependencies.VerifyNoResourcePlanningQueries();
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellationFromInterpreter()
    {
        var fixture = CreateFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .Returns<PlanningRequest, IReadOnlyList<AssetReadModel>, IReadOnlyList<MaintenanceTaskReadModel>, CancellationToken>(
                (_, _, _, token) =>
                Task.FromException<PlanningInterpretation>(new OperationCanceledException(token)));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Workflow.ExecuteAsync(CompleteRequest, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        fixture.Interpreter.Verify(interpreter =>
            interpreter.InterpretAsync(
                CompleteRequest,
                It.IsAny<IReadOnlyList<AssetReadModel>>(),
                It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                cancellation.Token), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CrossDocumentScopeLimitation_IsIgnoredAndPlanningSucceeds()
    {
        // When MAN-P102-SCOPE-001 explicitly states "Do not substitute for P-101...",
        // this is a scope boundary, not a conflict. The P-102 evidence is naturally excluded
        // because task evidence is scoped to the task's source document (MAN-P101-001).
        var fixture = CreateFixture(includeP102: true);
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(Interpretation(RequiredTasks));

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        // Planning should succeed - P-102's scope limitation is respected by design
        var generated = Assert.IsType<PlanningCandidatesGeneratedResult>(result);
        Assert.NotEmpty(generated.Candidates);

        // No cross-document conflicts should be reported
        var conflictViolations = generated.Interpretation.ProposedTaskIds
            .SelectMany(taskId => generated.EvidenceReferences.Where(e => e.DocumentId == "MAN-P102-SCOPE-001"))
            .ToArray();
        // P-102 evidence should not appear in the planning result
        Assert.Empty(conflictViolations);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutCrossDocumentConflict_UnrelatedEvidenceDoesNotFlagViolation()
    {
        var fixture = CreateFixture();
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(Interpretation(RequiredTasks));

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        var generated = Assert.IsType<PlanningCandidatesGeneratedResult>(result);
        Assert.NotEmpty(generated.Candidates);
        var crossDocConflicts = generated.EvidenceReferences.Where(e =>
            e.DocumentId != null && e.DocumentId.Contains("CROSS_DOCUMENT", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(crossDocConflicts);
    }

    [Theory]
    [InlineData("This inspection-only scope applies to P-102. Do not use it for other pumps.", false)]
    [InlineData("Do not use the standard bearing replacement method. Use an inspection only method instead.", true)]
    [InlineData("Do not schedule the P-101 bearing replacement before the P-102 inspection.", true)]
    public async Task ExecuteAsync_CrossDocumentStatementNamingOnlyOtherAssets_IsNotAConflictForThisAsset(
        string otherDocumentExcerpt,
        bool expectConflict)
    {
        var fixture = CreateFixture(includeP102: true, crossDocumentExcerpt: otherDocumentExcerpt);
        fixture.Interpreter.Setup(interpreter =>
                interpreter.InterpretAsync(
                    CompleteRequest,
                    It.IsAny<IReadOnlyList<AssetReadModel>>(),
                    It.IsAny<IReadOnlyList<MaintenanceTaskReadModel>>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(Interpretation(RequiredTasks));

        var result = await fixture.Workflow.ExecuteAsync(CompleteRequest, CancellationToken.None);

        if (expectConflict)
        {
            var unavailable = Assert.IsType<PlanningDataUnavailableResult>(result);
            Assert.Contains(unavailable.Violations, violation => violation.Code == "MANUAL_EVIDENCE_CONFLICT_CROSS_DOCUMENT");
        }
        else
        {
            Assert.IsType<PlanningCandidatesGeneratedResult>(result);
        }
    }

    private static WorkflowFixture CreateFixture(
        IReadOnlyList<SparePartReadModel>? spareParts = null,
        decimal availableBearingQuantity = 1,
        bool includeP102 = false,
        bool includeP101Inspection = false,
        string? crossDocumentExcerpt = null)
    {
        var seed = P101AnchorSeed.Create();
        var assets = new List<AssetReadModel>
        {
            new AssetReadModel(seed.Asset.Id, seed.Asset.PlantId, seed.Asset.Name, seed.Asset.Model, seed.Asset.Priority)
        };
        if (includeP102)
        {
            assets.Add(new AssetReadModel("P-102", seed.Asset.PlantId, "Pump P-102", "CP-100", 2));
        }
        var windows = seed.MaintenanceWindows.Select(window => new MaintenanceWindowReadModel(
            window.Id,
            window.Name,
            window.Start,
            window.End,
            window.TimeZoneId)).ToArray();
        var catalogue = seed.Tasks.Select(task => new MaintenanceTaskReadModel(
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
        if (includeP101Inspection)
        {
            catalogue =
            [
                .. catalogue,
                new MaintenanceTaskReadModel(
                    "P101-INSPECTION",
                    "P-101",
                    "Inspection",
                    60,
                    "MECHANICAL",
                    1,
                    null,
                    0,
                    null,
                    "MAN-P101-001",
                    "Planning assumptions: Inspection")
            ];
        }
        var technicians = seed.Technicians.Select(technician => new TechnicianReadModel(
            technician.Id,
            technician.Name,
            technician.HourlyRate,
            technician.TechnicalSkills.Select(skill => skill.SkillCode).ToArray(),
            technician.TechnicianAvailabilities.Select(availability =>
                new TechnicianAvailabilityReadModel(
                    availability.AvailableFrom,
                    availability.AvailableTo)).ToArray())).ToArray();
        var parts = spareParts ??
        [
            new SparePartReadModel(
                seed.SparePart.Id,
                seed.SparePart.Name,
                seed.SparePart.Unit,
                seed.SparePart.OnHandQuantity,
                seed.SparePart.OnHandQuantity - availableBearingQuantity,
                availableBearingQuantity)
        ];

        var queries = new Mock<IMaintenancePlannerQueries>(MockBehavior.Strict);
        queries.Setup(query => query.GetAssetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(assets);
        queries.Setup(query => query.GetMaintenanceWindowsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(windows);
        queries.Setup(query => query.GetTaskCatalogueAsync("P-101", It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalogue);
        if (includeP102)
        {
            IReadOnlyList<MaintenanceTaskReadModel> p102Catalogue =
            [
                new MaintenanceTaskReadModel(
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
                    "Inspection")
            ];
            queries.Setup(query => query.GetTaskCatalogueAsync("P-102", It.IsAny<CancellationToken>()))
                .ReturnsAsync(p102Catalogue);
        }
        queries.Setup(query => query.GetMaintenanceHistoryAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new MaintenanceHistoryReadModel(
                    "HIST-01",
                    "P-101",
                    WindowStart.AddDays(-30),
                    "Previous bearing inspection",
                    "Completed",
                    "HIST-MANUAL-01",
                    "Section 4")
            ]);
        queries.Setup(query => query.GetTechniciansAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(technicians);
        queries.Setup(query => query.GetSparePartsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(parts);
        queries.Setup(query => query.GetCompatibleSparePartsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string assetId, CancellationToken _) =>
                assetId == "P-101"
                    ? Task.FromResult<IReadOnlyList<SparePartReadModel>>(parts)
                    : Task.FromResult<IReadOnlyList<SparePartReadModel>>([]));


        var evidenceRetriever = new Mock<IManualEvidenceRetriever>(MockBehavior.Strict);
        evidenceRetriever.Setup(retriever => retriever.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string query, string? documentId, CancellationToken _) =>
                documentId switch
                {
                    "MAN-P101-001" => new List<PlanningEvidenceReference>
                    {
                        new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P101-001", "MAN-P101-001", null, 1, "Planning assumptions", "Planning evidence")
                    },
                    "MAN-P102-SCOPE-001" => new List<PlanningEvidenceReference>
                    {
                        new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P102-SCOPE-001", "MAN-P102-SCOPE-001", null, 1, "Inspection", "Inspection evidence")
                    },
                    null when crossDocumentExcerpt is not null => new List<PlanningEvidenceReference>
                    {
                        new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P101-001", "MAN-P101-001", null, 1, "Planning assumptions", "Planning evidence"),
                        new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-OTHER-001", "MAN-OTHER-001", null, 1, "Other", crossDocumentExcerpt)
                    },
                    null when includeP102 => new List<PlanningEvidenceReference>
                    {
                        new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P101-001", "MAN-P101-001", null, 1, "Planning assumptions", "Planning evidence"),
                        new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P102-SCOPE-001", "MAN-P102-SCOPE-001", null, 1, "Scope limitation", "Do not substitute this scope for the P-101 bearing-replacement scope")
                    },
                    null => new List<PlanningEvidenceReference>
                    {
                        new(PlanningEvidenceSourceKind.MaintenanceManual, "MAN-P101-001", "MAN-P101-001", null, 1, "Planning assumptions", "Planning evidence")
                    },
                    _ => new List<PlanningEvidenceReference>()
                });

        var interpreter = new Mock<IPlanningRequestInterpreter>(MockBehavior.Strict);
        var sparePartProvider = new Mock<ISparePartReadModelProvider>(MockBehavior.Default);
        sparePartProvider.Setup(p => p.GetSparePartsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parts);
        sparePartProvider.Setup(p => p.GetCompatibleSparePartsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string assetId, string? _, CancellationToken _) =>
                assetId == "P-101"
                    ? Task.FromResult<IReadOnlyList<SparePartReadModel>>(parts)
                    : Task.FromResult<IReadOnlyList<SparePartReadModel>>([]));

        var validator = new PlanningCandidateValidator(sparePartProvider.Object, queries.Object);
        var costCalculator = new CandidateCostCalculator();
        var scheduler = new DeterministicPlanningScheduler(sparePartProvider.Object, queries.Object, validator, costCalculator);
        var workflow = new PlanningWorkflow(
            queries.Object,
            evidenceRetriever.Object,
            interpreter.Object,
            scheduler);
        return new WorkflowFixture(
            workflow,
            queries,
            evidenceRetriever,
            interpreter,
            new SchedulerQueryVerifications(queries));
    }

    private static PlanningInterpretation Interpretation(params string[] taskIds) =>
        new(
            "Bearing replacement",
            "Replace the P-101 bearing",
            ["P-101"],
            taskIds,
            [],
            ["Use authoritative P-101 maintenance data"])
        {
            ScopeResolution = PlanningScopeResolution.Resolved,
            ScopeMappings = taskIds.Select(taskId => new PlanningRequestScopeMapping(
                taskId,
                taskId.StartsWith("P102", StringComparison.OrdinalIgnoreCase) ? "P-102" : "P-101",
                "Replace the P-101 bearing")).ToArray()
        };

    private sealed record WorkflowFixture(
        IPlanningWorkflow Workflow,
        Mock<IMaintenancePlannerQueries> Queries,
        Mock<IManualEvidenceRetriever> EvidenceRetriever,
        Mock<IPlanningRequestInterpreter> Interpreter,
        SchedulerQueryVerifications SchedulerDependencies);

    private sealed class SchedulerQueryVerifications(Mock<IMaintenancePlannerQueries> queries)
    {
        public void VerifyNoResourcePlanningQueries()
        {
            queries.Verify(query => query.GetTechniciansAsync(It.IsAny<CancellationToken>()), Times.Never);
            queries.Verify(query => query.GetSparePartsAsync(It.IsAny<CancellationToken>()), Times.Never);
            queries.Verify(query => query.GetCompatibleSparePartsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
