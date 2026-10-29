using System.Text.RegularExpressions;
using MaintenancePlanner.Application.Planning.Tools;
using MaintenancePlanner.Application.Queries;

namespace MaintenancePlanner.Application.Planning;

public sealed class PlanningWorkflow(
    IMaintenancePlannerQueries queries,
    IManualEvidenceRetriever evidenceRetriever,
    IPlanningRequestInterpreter interpreter,
    DeterministicPlanningScheduler scheduler,
    IPlanningInterpretationCheckpointSink? checkpointSink = null,
    ModelCandidateProposals? modelCandidates = null) : IPlanningWorkflow
{
    public Task<PlanningWorkflowResult> ExecuteAsync(
        PlanningRequest request,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? lockedTaskAssignments = null) =>
        ExecuteCoreAsync(request, resumedInterpretation: null, cancellationToken, lockedTaskAssignments);

    /// <summary>
    /// Continues a planning job from an interpretation that was validated and checkpointed earlier. Only the
    /// interpreter call is skipped: the request, the interpretation and its task scope are checked exactly as on the
    /// normal path, against current data, before evidence retrieval and scheduling run.
    /// </summary>
    public async Task<PlanningWorkflowResult> ResumeFromCheckpointAsync(
        PlanningRequest request,
        PlanningInterpretation checkpoint,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? lockedTaskAssignments = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request is null || checkpoint is null)
            return Invalid(checkpoint ?? UnresolvedInterpretation(request, []), "INVALID_CHECKPOINT", "Checkpoint resumption requires valid request and interpretation.", null);

        return await ExecuteCoreAsync(request, checkpoint, cancellationToken, lockedTaskAssignments);
    }

    private async Task<PlanningWorkflowResult> ExecuteCoreAsync(
        PlanningRequest request,
        PlanningInterpretation? resumedInterpretation,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? lockedTaskAssignments)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request is null)
        {
            return Invalid(
                UnresolvedInterpretation(null, ["Provide a planning request."]),
                "INVALID_REQUEST",
                "A planning request is required.",
                resourceId: null);
        }

        if (string.IsNullOrWhiteSpace(request.RequestText))
        {
            return Clarification(
                UnresolvedInterpretation(request, ["Describe the maintenance work to plan."]),
                "Describe the maintenance work to plan.");
        }

        if (request.AssetIds is null || request.AssetIds.Count == 0)
        {
            return Clarification(
                UnresolvedInterpretation(request, ["Select at least one asset."]),
                "Select at least one asset.");
        }

        if (request.AssetIds.Any(string.IsNullOrWhiteSpace) || HasDuplicates(request.AssetIds))
        {
            var unresolvedInterpretation = UnresolvedInterpretation(request, []);
            return Invalid(
                unresolvedInterpretation,
                "INVALID_ASSET_SCOPE",
                "Selected asset IDs must be nonblank and unique.",
                resourceId: null);
        }

        if (string.IsNullOrWhiteSpace(request.MaintenanceWindowId))
        {
            return Clarification(
                UnresolvedInterpretation(request, ["Select a maintenance window."]),
                "Select a maintenance window.");
        }

        var assets = await queries.GetAssetsAsync(cancellationToken);
        var assetsById = assets.ToDictionary(asset => asset.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var assetId in request.AssetIds)
        {
            if (!assetsById.ContainsKey(assetId))
            {
                return Invalid(
                    UnresolvedInterpretation(request, []),
                    "ASSET_NOT_FOUND",
                    $"Selected asset {assetId} does not exist in the authoritative asset records.",
                    assetId);
            }
        }

        var window = (await queries.GetMaintenanceWindowsAsync(cancellationToken))
            .FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                request.MaintenanceWindowId,
                StringComparison.OrdinalIgnoreCase));
        if (window is null)
        {
            return Invalid(
                UnresolvedInterpretation(request, []),
                "WINDOW_NOT_FOUND",
                $"Maintenance window {request.MaintenanceWindowId} does not exist.",
                request.MaintenanceWindowId);
        }

        var taskCatalogues = new Dictionary<string, IReadOnlyList<MaintenanceTaskReadModel>>(
            StringComparer.OrdinalIgnoreCase);
        var selectedAssetIds = request.AssetIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.OrderBy(asset => asset.Id, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var catalogue = await queries.GetTaskCatalogueAsync(asset.Id, cancellationToken);
            if (catalogue is null)
            {
                if (!selectedAssetIds.Contains(asset.Id))
                    continue;

                return DataUnavailable(
                    UnresolvedInterpretation(request, []),
                    new PlanningValidationViolation(
                        "TASK_CATALOGUE_UNAVAILABLE",
                        $"The task catalogue for asset {asset.Id} is unavailable.",
                        null,
                        asset.Id));
            }

            taskCatalogues.Add(asset.Id, catalogue);
        }

        var authoritativeTasks = taskCatalogues.Values.SelectMany(catalogue => catalogue).ToArray();
        // AI03: candidates the model saves during interpretation are bound to this request, not to model arguments
        modelCandidates?.Begin(new ModelCandidateContext(
            request.AssetIds,
            window.Id,
            request.CompletedTaskIds,
            request.ReleasableReservedQuantities));
        var interpretation = resumedInterpretation ?? await interpreter.InterpretAsync(
            request,
            assets,
            authoritativeTasks,
            cancellationToken);
        if (interpretation is null)
        {
            return Invalid(
                UnresolvedInterpretation(request, []),
                "INTERPRETATION_UNAVAILABLE",
                "The request interpreter returned no interpretation.",
                null);
        }

        if (interpretation.MissingInformation.Count > 0)
        {
            return Clarification(interpretation, interpretation.MissingInformation);
        }

        if (interpretation.ScopeResolution != PlanningScopeResolution.Resolved)
        {
            var question = interpretation.ScopeResolution switch
            {
                PlanningScopeResolution.Ambiguous =>
                    "Clarify which maintenance task or tasks are intended.",
                PlanningScopeResolution.Unsupported =>
                    "Clarify the requested work; the proposed task scope is not supported by the available information.",
                _ => "Clarify the requested task scope before scheduling."
            };
            return Clarification(interpretation, question);
        }

        if (string.IsNullOrWhiteSpace(interpretation.MaintenanceIntent) ||
            string.IsNullOrWhiteSpace(interpretation.Scope) ||
            interpretation.ProposedTaskIds is null ||
            interpretation.ProposedTaskIds.Count == 0)
        {
            return Clarification(
                interpretation,
                ["Confirm the maintenance task scope before scheduling."]);
        }

        if (interpretation.ProposedAssetIds is null ||
            interpretation.ProposedAssetIds.Count == 0)
        {
            return Clarification(
                interpretation,
                ["Confirm which selected assets are included in the requested maintenance scope."]);
        }

        if (interpretation.ProposedTaskIds.Any(string.IsNullOrWhiteSpace) ||
            HasDuplicates(interpretation.ProposedTaskIds))
        {
            return Invalid(
                interpretation,
                "INVALID_TASK_SCOPE",
                "Proposed task IDs must be nonblank and unique.",
                null);
        }

        if (interpretation.ProposedAssetIds is { Count: > 0 } proposedAssetIds &&
            (proposedAssetIds.Any(string.IsNullOrWhiteSpace) ||
             HasDuplicates(proposedAssetIds) ||
             !SameIdentifiers(proposedAssetIds, request.AssetIds)))
        {
            return Clarification(
                interpretation,
                ["Clarify which of the selected assets are included in the requested work."]);
        }

        if (interpretation.ScopeMappings is null ||
            interpretation.ScopeMappings.Count == 0)
        {
            return Clarification(
                interpretation,
                ["Identify the requested work that each required task represents."]);
        }

        if (interpretation.ScopeMappings.Any(mapping =>
                mapping is null ||
                string.IsNullOrWhiteSpace(mapping.TaskId) ||
                string.IsNullOrWhiteSpace(mapping.AssetId)))
        {
            return Clarification(
                interpretation,
                ["Clarify the asset and task scope for the requested work."]);
        }

        var mappedTaskIds = interpretation.ScopeMappings
            .Select(mapping => mapping.TaskId)
            .ToArray();
        if (HasDuplicates(mappedTaskIds) ||
            mappedTaskIds.Any(taskId =>
                !interpretation.ProposedTaskIds.Contains(taskId, StringComparer.OrdinalIgnoreCase)))
        {
            return Invalid(
                interpretation,
                "INVALID_SCOPE_MAPPING",
                "Request-to-task mappings must uniquely correspond to proposed task IDs.",
                null,
                mappedTaskIds.FirstOrDefault());
        }

        var unmappedTaskIds = interpretation.ProposedTaskIds
            .Where(taskId => !mappedTaskIds.Contains(taskId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (unmappedTaskIds.Length > 0)
        {
            return Clarification(
                interpretation,
                unmappedTaskIds.Select(taskId =>
                    $"Identify which part of the request requires task {taskId}.").ToArray());
        }

        var tasksById = taskCatalogues.Values
            .SelectMany(catalogue => catalogue)
            .GroupBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var requiredTasks = new Dictionary<string, MaintenanceTaskReadModel>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var taskId in interpretation.ProposedTaskIds)
        {
            if (!tasksById.TryGetValue(taskId, out var matches))
            {
                return Invalid(
                    interpretation,
                    "TASK_NOT_FOUND",
                    $"Proposed task {taskId} does not exist in the selected assets' authoritative task catalogues.",
                    taskId,
                    taskId);
            }

            if (matches.Length != 1)
            {
                return Invalid(
                    interpretation,
                    "AMBIGUOUS_TASK_ID",
                    $"Proposed task ID {taskId} is ambiguous across the selected assets.",
                    taskId);
            }

            if (!request.AssetIds.Contains(matches[0].AssetId, StringComparer.OrdinalIgnoreCase))
            {
                return Clarification(
                    interpretation,
                    $"Task {taskId} belongs to unselected asset {matches[0].AssetId}. Clarify the intended asset scope.");
            }

            var mapping = interpretation.ScopeMappings.Single(scopeMapping =>
                string.Equals(scopeMapping.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(mapping.AssetId, matches[0].AssetId, StringComparison.OrdinalIgnoreCase))
            {
                return Clarification(
                    interpretation,
                    $"Task {taskId} belongs to {matches[0].AssetId}, but its proposed mapping names {mapping.AssetId}. Clarify the intended scope.");
            }

            if (!IncludePredecessors(
                    matches[0],
                    tasksById,
                    request.AssetIds,
                    requiredTasks,
                    out var predecessorViolation))
            {
                return Invalid(interpretation, predecessorViolation!);
            }

            if (!string.IsNullOrWhiteSpace(matches[0].PredecessorTaskId) &&
                !IncludeRequiredFollowUps(
                    matches[0],
                    tasksById,
                    request.AssetIds,
                    requiredTasks,
                    out var followUpQuestion))
            {
                return Clarification(interpretation, followUpQuestion!);
            }
        }

        // The interpretation has now passed every interpretation-stage check. A new interpretation is checkpointed
        // here so an interrupted job can resume without interpreting the request again; a resumed one already is.
        if (resumedInterpretation is null && checkpointSink is not null)
            await checkpointSink.SaveValidatedInterpretationAsync(interpretation, cancellationToken);

        // FR08: work in progress on the approved plan being replaced stays in the replacement (at its approved
        // placement), even when the revised request does not name it
        foreach (var inProgress in request.InProgressPlacements)
        {
            if (!tasksById.TryGetValue(inProgress.TaskId, out var matches) ||
                matches.Length != 1 ||
                !request.AssetIds.Contains(matches[0].AssetId, StringComparer.OrdinalIgnoreCase))
            {
                return Invalid(
                    interpretation,
                    "IN_PROGRESS_TASK_OUT_OF_SCOPE",
                    $"Task {inProgress.TaskId} is in progress on the approved plan being replaced but is not in the selected assets' task catalogues.",
                    inProgress.TaskId,
                    inProgress.TaskId);
            }

            if (!IncludePredecessors(matches[0], tasksById, request.AssetIds, requiredTasks, out var predecessorViolation))
                return Invalid(interpretation, predecessorViolation!);
        }

        // FR08: work completed on the approved plan being replaced is not scheduled again
        foreach (var completedTaskId in request.CompletedTaskIds)
            requiredTasks.Remove(completedTaskId);

        if (requiredTasks.Count == 0)
        {
            return Invalid(
                interpretation,
                "NO_REMAINING_WORK",
                "Every requested task is already completed on the approved plan being replaced.",
                resourceId: null);
        }

        var evidenceResult = await RetrieveEvidenceAsync(
            request,
            interpretation,
            request.AssetIds.Select(assetId => assetsById[assetId]),
            assetsById.Keys,
            window,
            requiredTasks.Values,
            cancellationToken);
        if (evidenceResult.Violations.Count > 0)
        {
            return new PlanningDataUnavailableResult(
                evidenceResult.Violations,
                interpretation.Assumptions,
                evidenceResult.References)
            {
                Interpretation = interpretation
            };
        }

        var schedule = await scheduler.ScheduleAsync(
            new PlanningScheduleRequest(
                request.AssetIds,
                window.Id,
                requiredTasks.Keys.ToArray())
            {
                CompletedTaskIds = request.CompletedTaskIds,
                ReleasableReservedQuantities = request.ReleasableReservedQuantities,
                InProgressPlacements = request.InProgressPlacements
            },
            cancellationToken,
            lockedTaskAssignments);
        var result = PlanningWorkflowResultMapper.FromScheduleResult(
            schedule,
            interpretation,
            interpretation.Assumptions,
            evidenceResult.References);

        if (result is PlanningCandidatesGeneratedResult generated &&
            generated.Candidates.Any(candidate =>
                !HasExactTaskScope(candidate, requiredTasks.Keys)))
        {
            return Invalid(
                interpretation,
                "SCHEDULE_SCOPE_MISMATCH",
                "The scheduler candidate task placements do not match the interpreted task scope and its required predecessors.",
                window.Id);
        }

        return AddScopeAndEvidenceToCandidates(
            IncludeModelCandidates(result, requiredTasks.Keys),
            evidenceResult.References,
            interpretation.ProposedTaskIds
                .Where(taskId => !request.CompletedTaskIds.Contains(taskId, StringComparer.OrdinalIgnoreCase))
                .ToArray());
    }

    private async Task<PlanningEvidenceRetrievalResult> RetrieveEvidenceAsync(
        PlanningRequest request,
        PlanningInterpretation interpretation,
        IEnumerable<AssetReadModel> assets,
        IReadOnlyCollection<string> knownAssetIds,
        MaintenanceWindowReadModel window,
        IEnumerable<MaintenanceTaskReadModel> tasks,
        CancellationToken cancellationToken)
    {
        var evidence = new List<PlanningEvidenceReference>();
        var violations = new List<PlanningValidationViolation>();
        evidence.AddRange(assets.Select(asset => new PlanningEvidenceReference(
            PlanningEvidenceSourceKind.Asset,
            asset.Id,
            null,
            null,
            null,
            null,
            $"{asset.Name} ({asset.Model})")));
        evidence.Add(new PlanningEvidenceReference(
            PlanningEvidenceSourceKind.MaintenanceWindow,
            window.Id,
            null,
            null,
            null,
            window.Name,
            $"{window.Start:O} to {window.End:O} ({window.TimeZoneId})"));

        foreach (var task in tasks.OrderBy(task => task.Id, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            evidence.Add(new PlanningEvidenceReference(
                PlanningEvidenceSourceKind.MaintenanceTask,
                task.Id,
                task.SourceDocumentId,
                null,
                null,
                task.SourceSection,
                null));

            var query = string.Join(
                " ",
                request.RequestText,
                interpretation.MaintenanceIntent,
                interpretation.Scope,
                task.Name,
                task.SourceSection);
            IReadOnlyList<PlanningEvidenceReference> taskEvidence;
            IReadOnlyList<PlanningEvidenceReference> allDocumentsEvidence;
            try
            {
                taskEvidence = await evidenceRetriever.SearchAsync(
                    query,
                    task.SourceDocumentId,
                    cancellationToken);
            }
            catch (FileNotFoundException)
            {
                taskEvidence = [];
            }
            catch (InvalidDataException)
            {
                taskEvidence = [];
            }

            try
            {
                allDocumentsEvidence = await evidenceRetriever.SearchAsync(
                    query,
                    null,
                    cancellationToken);
            }
            catch (FileNotFoundException)
            {
                allDocumentsEvidence = [];
            }
            catch (InvalidDataException)
            {
                allDocumentsEvidence = [];
            }

            var manualEvidence = taskEvidence
                .Where(reference =>
                    reference.SourceKind == PlanningEvidenceSourceKind.MaintenanceManual &&
                    string.Equals(
                        reference.DocumentId,
                        task.SourceDocumentId,
                        StringComparison.OrdinalIgnoreCase) &&
                    (!string.IsNullOrWhiteSpace(reference.Excerpt) || reference.Page is not null))
                .ToArray();
            if (manualEvidence.Length == 0)
            {
                violations.Add(new PlanningValidationViolation(
                    "MANUAL_EVIDENCE_UNAVAILABLE",
                    $"No usable manual evidence was retrieved for task {task.Id} from document {task.SourceDocumentId}.",
                    task.Id,
                    task.SourceDocumentId));
            }
            else if (manualEvidence
                     .Select(reference => reference.Version ?? string.Empty)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Skip(1)
                     .Any())
            {
                violations.Add(new PlanningValidationViolation(
                    "MANUAL_EVIDENCE_CONFLICT",
                    $"Multiple manual versions were retrieved for task {task.Id} without an authoritative version selection.",
                    task.Id,
                    task.SourceDocumentId));
            }

            // Detect cross-document conflicts: evidence from other documents that contradicts the task's evidence
            var otherDocumentEvidence = allDocumentsEvidence
                .Where(reference =>
                    reference.SourceKind == PlanningEvidenceSourceKind.MaintenanceManual &&
                    !string.Equals(reference.DocumentId, task.SourceDocumentId, StringComparison.OrdinalIgnoreCase) &&
                    (!string.IsNullOrWhiteSpace(reference.Excerpt) || reference.Page is not null))
                .ToArray();

            if (otherDocumentEvidence.Length > 0 && manualEvidence.Length > 0)
            {
                // Check for contradictory statements (e.g., one doc says "do replacement", another says "don't substitute")
                var conflictIndicators = new[] { "do not", "don't", "not substitute", "inspection only", "scope limitation" };
                var conflictingDocs = otherDocumentEvidence
                    .Where(other =>
                    {
                        var excerpt = other.Excerpt ?? "";

                        // Check if excerpt contains conflict indicators
                        var hasConflictIndicator = conflictIndicators.Any(indicator =>
                            excerpt.Contains(indicator, StringComparison.OrdinalIgnoreCase));

                        if (!hasConflictIndicator)
                            return false;

                        // A statement that names other assets but not this task's asset is about those assets and
                        // cannot contradict this task, e.g. the P-102 inspection-only scope when planning P-103
                        if (!MentionsAsset(excerpt, task.AssetId) &&
                            knownAssetIds.Any(assetId =>
                                !string.Equals(assetId, task.AssetId, StringComparison.OrdinalIgnoreCase) &&
                                MentionsAsset(excerpt, assetId)))
                        {
                            return false;
                        }

                        // Filter out scope-boundary statements that explicitly reference this asset
                        // e.g., "Do not substitute this scope for the P-101 bearing-replacement scope"
                        // These are clarifications, not contradictions
                        if ((excerpt.Contains("not substitute", StringComparison.OrdinalIgnoreCase) ||
                             excerpt.Contains("not confuse", StringComparison.OrdinalIgnoreCase) ||
                             excerpt.Contains("must not substitute", StringComparison.OrdinalIgnoreCase)) &&
                            excerpt.Contains(task.AssetId, StringComparison.OrdinalIgnoreCase))
                        {
                            // This is a scope boundary statement about the current asset, not a conflict
                            return false;
                        }

                        return true;
                    })
                    .ToArray();

                if (conflictingDocs.Length > 0)
                {
                    var conflictingDocNames = string.Join(", ", conflictingDocs
                        .Select(d => d.DocumentId)
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                    var conflictExcerpt = conflictingDocs.FirstOrDefault()?.Excerpt ?? "";
                    violations.Add(new PlanningValidationViolation(
                        "MANUAL_EVIDENCE_CONFLICT_CROSS_DOCUMENT",
                        $"Conflicting evidence for task {task.Id}: evidence from {task.SourceDocumentId} conflicts with evidence in {conflictingDocNames}. Conflicting statement: \"{conflictExcerpt}\"",
                        task.Id,
                        task.SourceDocumentId));
                }
            }

            evidence.AddRange(taskEvidence);
        }

        var history = await queries.GetMaintenanceHistoryAsync(request.AssetIds, cancellationToken);
        evidence.AddRange(history.Select(record => new PlanningEvidenceReference(
            PlanningEvidenceSourceKind.MaintenanceHistory,
            record.Id,
            record.SourceDocumentId,
            null,
            null,
            record.SourceReference,
            $"{record.Summary} {record.Outcome}")));

        var distinctEvidence = evidence
            .DistinctBy(reference => (
                reference.SourceKind,
                reference.SourceId,
                reference.DocumentId,
                reference.Version,
                reference.Page,
                reference.Section,
                reference.Excerpt))
            .ToArray();
        return new PlanningEvidenceRetrievalResult(distinctEvidence, violations);
    }


    // Whole identifier only, so P-101 is not found inside P-1011
    private static bool MentionsAsset(string text, string assetId) =>
        Regex.IsMatch(
            text,
            $@"(?<![\p{{L}}\p{{N}}-]){Regex.Escape(assetId)}(?![\p{{L}}\p{{N}}-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record PlanningEvidenceRetrievalResult(
        IReadOnlyList<PlanningEvidenceReference> References,
        IReadOnlyList<PlanningValidationViolation> Violations);

    private static bool IncludePredecessors(
        MaintenanceTaskReadModel task,
        IReadOnlyDictionary<string, MaintenanceTaskReadModel[]> tasksById,
        IReadOnlyCollection<string> selectedAssetIds,
        IDictionary<string, MaintenanceTaskReadModel> requiredTasks,
        out PlanningValidationViolation? violation)
    {
        violation = null;
        if (requiredTasks.ContainsKey(task.Id))
            return true;

        requiredTasks.Add(task.Id, task);
        if (string.IsNullOrWhiteSpace(task.PredecessorTaskId))
            return true;

        if (!tasksById.TryGetValue(task.PredecessorTaskId, out var predecessors))
        {
            violation = new PlanningValidationViolation(
                "PREDECESSOR_NOT_FOUND",
                $"Task {task.Id} references missing predecessor {task.PredecessorTaskId}.",
                task.Id,
                task.PredecessorTaskId);
            return false;
        }

        if (predecessors.Length != 1)
        {
            violation = new PlanningValidationViolation(
                "AMBIGUOUS_PREDECESSOR",
                $"Predecessor task ID {task.PredecessorTaskId} is ambiguous.",
                task.Id,
                task.PredecessorTaskId);
            return false;
        }

        if (!selectedAssetIds.Contains(predecessors[0].AssetId, StringComparer.OrdinalIgnoreCase))
        {
            violation = new PlanningValidationViolation(
                "PREDECESSOR_OUT_OF_SCOPE",
                $"Predecessor {predecessors[0].Id} belongs to unselected asset {predecessors[0].AssetId}.",
                task.Id,
                predecessors[0].AssetId);
            return false;
        }

        return IncludePredecessors(
            predecessors[0],
            tasksById,
            selectedAssetIds,
            requiredTasks,
            out violation);
    }

    private static bool IncludeRequiredFollowUps(
        MaintenanceTaskReadModel task,
        IReadOnlyDictionary<string, MaintenanceTaskReadModel[]> tasksById,
        IReadOnlyCollection<string> selectedAssetIds,
        IDictionary<string, MaintenanceTaskReadModel> requiredTasks,
        out string? clarificationQuestion)
    {
        clarificationQuestion = null;
        var currentTask = task;
        var visitedTaskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { task.Id };

        while (true)
        {
            var followUps = tasksById.Values
                .SelectMany(matches => matches)
                .Where(candidate =>
                    string.Equals(
                        candidate.PredecessorTaskId,
                        currentTask.Id,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        candidate.AssetId,
                        currentTask.AssetId,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (followUps.Length == 0)
                return true;

            if (followUps.Length > 1)
            {
                clarificationQuestion =
                    $"Task {currentTask.Id} has multiple follow-up tasks. Clarify the intended maintenance scope.";
                return false;
            }

            var followUp = followUps[0];
            if (!selectedAssetIds.Contains(followUp.AssetId, StringComparer.OrdinalIgnoreCase))
            {
                clarificationQuestion =
                    $"Follow-up task {followUp.Id} belongs to unselected asset {followUp.AssetId}. Clarify the intended asset scope.";
                return false;
            }

            if (!visitedTaskIds.Add(followUp.Id))
            {
                clarificationQuestion =
                    $"A task dependency cycle was detected after task {currentTask.Id}. Clarify the intended task scope.";
                return false;
            }

            if (tasksById.TryGetValue(followUp.Id, out var matches) && matches.Length != 1)
            {
                clarificationQuestion =
                    $"Follow-up task ID {followUp.Id} is ambiguous. Clarify the intended task scope.";
                return false;
            }

            requiredTasks[followUp.Id] = followUp;
            currentTask = followUp;
        }
    }

    /// <summary>
    /// AI03 draft saving: adds the candidates the model saved (already validated against this request) to the
    /// generated, unapproved candidates. A saved candidate is added only when it covers exactly the required task scope,
    /// like every scheduler candidate, and is not a copy of one; every saved candidate gets a note saying what happened.
    /// </summary>
    private PlanningWorkflowResult IncludeModelCandidates(
        PlanningWorkflowResult result,
        IReadOnlyCollection<string> requiredTaskIds)
    {
        var proposals = modelCandidates?.Proposals ?? [];
        if (proposals.Count == 0)
            return result;

        if (result is not PlanningCandidatesGeneratedResult generated)
        {
            return result with
            {
                ModelCandidateNotes = proposals
                    .Select(proposal => $"Model-proposed candidate {proposal.Number} was not included: the planning outcome is {result.GetType().Name.Replace("Planning", string.Empty).Replace("Result", string.Empty)}.")
                    .ToArray()
            };
        }

        var candidates = generated.Candidates.ToList();
        var notes = new List<string>();
        foreach (var proposal in proposals)
        {
            var copyOf = candidates.FindIndex(candidate => ModelCandidateProposals.SamePlacements(candidate, proposal.Candidate));
            if (!HasExactTaskScope(proposal.Candidate, requiredTaskIds))
            {
                notes.Add($"Model-proposed candidate {proposal.Number} was not included: it does not cover exactly the required tasks ({string.Join(", ", requiredTaskIds)}).");
            }
            else if (copyOf >= 0)
            {
                notes.Add($"Model-proposed candidate {proposal.Number} was not included: it is identical to candidate {copyOf + 1}.");
            }
            else
            {
                candidates.Add(proposal.Candidate);
                notes.Add($"Model-proposed candidate {proposal.Number} was included as candidate {candidates.Count}.");
            }
        }

        CandidateCostCalculator.ApplyRankingMetadata(candidates);
        return generated with { Candidates = candidates, ModelCandidateNotes = notes };
    }

    private static PlanningWorkflowResult AddScopeAndEvidenceToCandidates(
        PlanningWorkflowResult result,
        IReadOnlyList<PlanningEvidenceReference> evidenceReferences,
        IReadOnlyList<string> requestedTaskIds)
    {
        if (result is not PlanningCandidatesGeneratedResult candidatesResult)
            return result;

        var candidates = candidatesResult.Candidates
            .Select(candidate => candidate with
            {
                RequestedTaskIds = requestedTaskIds.ToArray(),
                DependencyTaskIds = candidate.TaskPlacements
                    .Select(placement => placement.TaskId)
                    .Where(taskId => !requestedTaskIds.Contains(
                        taskId,
                        StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                EvidenceReferences = candidate.EvidenceReferences
                    .Concat(evidenceReferences)
                    .DistinctBy(reference => (
                        reference.SourceKind,
                        reference.SourceId,
                        reference.DocumentId,
                        reference.Version,
                        reference.Page,
                        reference.Section,
                        reference.Excerpt))
                    .ToArray()
            })
            .ToArray();
        return candidatesResult with { Candidates = candidates };
    }

    private static PlanningWorkflowResult Clarification(
        PlanningInterpretation interpretation,
        string question) =>
        Clarification(interpretation, [question]);

    private static PlanningWorkflowResult Clarification(
        PlanningInterpretation interpretation,
        IReadOnlyList<string> questions) =>
        new PlanningClarificationRequiredResult(
            questions,
            interpretation.Assumptions,
            [])
        {
            Interpretation = interpretation
        };

    private static PlanningWorkflowResult Invalid(
        PlanningInterpretation interpretation,
        string code,
        string message,
        string? resourceId,
        string? taskId = null) =>
        Invalid(
            interpretation,
            new PlanningValidationViolation(code, message, taskId, resourceId));

    private static PlanningWorkflowResult Invalid(
        PlanningInterpretation interpretation,
        PlanningValidationViolation violation) =>
        new PlanningInvalidInputResult(
            [violation],
            interpretation.Assumptions,
            [])
        {
            Interpretation = interpretation
        };

    private static PlanningWorkflowResult DataUnavailable(
        PlanningInterpretation interpretation,
        PlanningValidationViolation violation) =>
        new PlanningDataUnavailableResult(
            [violation],
            interpretation.Assumptions,
            [])
        {
            Interpretation = interpretation
        };

    private static PlanningInterpretation UnresolvedInterpretation(
        PlanningRequest? request,
        IReadOnlyList<string> missingInformation) =>
        new(
            string.Empty,
            string.Empty,
            request?.AssetIds ?? [],
            [],
            missingInformation,
            []);

    private static bool HasDuplicates(IEnumerable<string> identifiers) =>
        identifiers.GroupBy(identifier => identifier, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1);

    private static bool SameIdentifiers(
        IReadOnlyCollection<string> left,
        IReadOnlyCollection<string> right) =>
        left.Count == right.Count &&
        left.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(right);

    private static bool HasExactTaskScope(
        PlanningCandidate candidate,
        IEnumerable<string> requiredTaskIds)
    {
        if (candidate.TaskPlacements is null ||
            candidate.TaskPlacements.Any(placement => placement is null))
        {
            return false;
        }

        var placedTaskIds = candidate.TaskPlacements
            .Select(placement => placement.TaskId)
            .ToArray();
        return !HasDuplicates(placedTaskIds) &&
               placedTaskIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .SetEquals(requiredTaskIds);
    }
}
