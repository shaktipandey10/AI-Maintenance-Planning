using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;

namespace MaintenancePlanner.Application.Planning;

public sealed class DeterministicPlanningScheduler(
    ISparePartReadModelProvider sparePartProvider,
    IMaintenancePlannerQueries queries,
    PlanningCandidateValidator validator,
    CandidateCostCalculator costCalculator)
{
    // The documented time granularity: placements start on this grid, and imports require durations and
    // availability bounds that fit it.
    public static readonly TimeSpan SchedulingIncrement = TimeSpan.FromMinutes(30);
    private const int MaximumCandidates = 2;

    public async Task<PlanningScheduleResult> ScheduleAsync(
        PlanningScheduleRequest request,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? lockedTaskAssignments = null)
    {
        if (request is null)
        {
            return Invalid(
                "INVALID_REQUEST",
                "A planning schedule request is required.",
                taskId: null,
                resourceId: null);
        }

        if (request.AssetIds is null || request.AssetIds.Count == 0)
        {
            return Clarification("Select at least one asset before scheduling.");
        }

        if (request.RequiredTaskIds is null || request.RequiredTaskIds.Count == 0)
        {
            return Clarification("Confirm the required maintenance task scope before scheduling.");
        }

        if (string.IsNullOrWhiteSpace(request.MaintenanceWindowId))
        {
            return Clarification("Select a maintenance window before scheduling.");
        }

        if (request.AssetIds.Any(string.IsNullOrWhiteSpace) ||
            request.RequiredTaskIds.Any(string.IsNullOrWhiteSpace))
        {
            return Invalid(
                "IDENTIFIER_REQUIRED",
                "Asset IDs and required task IDs must not be blank.",
                taskId: null,
                resourceId: null);
        }

        if (HasDuplicates(request.AssetIds) || HasDuplicates(request.RequiredTaskIds))
        {
            return Invalid(
                "DUPLICATE_SCOPE_ID",
                "Selected asset IDs and required task IDs must be unique.",
                taskId: null,
                resourceId: null);
        }

        var assets = await queries.GetAssetsAsync(cancellationToken);
        var assetsById = assets.ToDictionary(asset => asset.Id, StringComparer.OrdinalIgnoreCase);
        var selectedAssets = new List<AssetReadModel>(request.AssetIds.Count);
        foreach (var assetId in request.AssetIds)
        {
            if (!assetsById.TryGetValue(assetId, out var asset))
            {
                return Invalid(
                    "ASSET_NOT_FOUND",
                    $"Selected asset {assetId} does not exist.",
                    taskId: null,
                    resourceId: assetId);
            }

            selectedAssets.Add(asset);
        }

        var windows = await queries.GetMaintenanceWindowsAsync(cancellationToken);
        var window = windows.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, request.MaintenanceWindowId, StringComparison.OrdinalIgnoreCase));
        if (window is null)
        {
            return Invalid(
                "WINDOW_NOT_FOUND",
                $"Maintenance window {request.MaintenanceWindowId} does not exist.",
                taskId: null,
                resourceId: request.MaintenanceWindowId);
        }

        if (window.Start >= window.End)
        {
            return Invalid(
                "INVALID_WINDOW_INTERVAL",
                $"Maintenance window {window.Id} must end after it starts.",
                taskId: null,
                resourceId: window.Id);
        }

        var cataloguedTasks = new List<MaintenanceTaskReadModel>();
        foreach (var asset in assets.OrderBy(asset => asset.Id, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var catalogue = await queries.GetTaskCatalogueAsync(asset.Id, cancellationToken);
            if (catalogue is null)
            {
                if (request.AssetIds.Contains(asset.Id, StringComparer.OrdinalIgnoreCase))
                {
                    return Invalid(
                        "TASK_CATALOGUE_UNAVAILABLE",
                        $"The task catalogue for selected asset {asset.Id} is unavailable.",
                        taskId: null,
                        resourceId: asset.Id);
                }

                continue;
            }

            cataloguedTasks.AddRange(catalogue);
        }

        var taskGroups = cataloguedTasks
            .GroupBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var selectedAssetIds = request.AssetIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredTasks = new Dictionary<string, MaintenanceTaskReadModel>(StringComparer.OrdinalIgnoreCase);
        var requestedTaskIds = new HashSet<string>(request.RequiredTaskIds, StringComparer.OrdinalIgnoreCase);
        var visitStates = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var scopeViolations = new List<PlanningValidationViolation>();

        // Build successor map: taskId -> list of successors (tasks with this as predecessor)
        var successorsByTaskId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in taskGroups.Values.SelectMany(g => g))
        {
            if (!string.IsNullOrWhiteSpace(task.PredecessorTaskId))
            {
                if (!successorsByTaskId.TryGetValue(task.PredecessorTaskId, out var successorList))
                {
                    successorList = [];
                    successorsByTaskId[task.PredecessorTaskId] = successorList;
                }
                successorList.Add(task.Id);
            }
        }

        foreach (var taskId in request.RequiredTaskIds)
        {
            IncludeTaskAndPredecessors(taskId, isRequested: true);
            IncludeLinearSuccessors(taskId);
        }

        if (scopeViolations.Count > 0)
        {
            return Result(PlanningScheduleOutcome.InvalidInput, scopeViolations);
        }

        // Work completed on the approved plan being replaced is not scheduled again; the remaining tasks treat a
        // completed predecessor as satisfied.
        var completedTaskIds = new HashSet<string>(request.CompletedTaskIds, StringComparer.OrdinalIgnoreCase);
        foreach (var completedTaskId in completedTaskIds)
        {
            requiredTasks.Remove(completedTaskId);
            requestedTaskIds.Remove(completedTaskId);
        }

        if (requiredTasks.Count == 0)
        {
            return Invalid(
                "NO_REMAINING_WORK",
                "Every required task is already completed; there is no remaining work to schedule.",
                taskId: null,
                resourceId: null);
        }

        // Work in progress on the approved plan being replaced keeps its approved placement (FR08)
        var inProgressPlacements = new Dictionary<string, PlanningTaskPlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var inProgress in request.InProgressPlacements)
        {
            if (!requiredTasks.TryGetValue(inProgress.TaskId, out var inProgressTask) ||
                !inProgressPlacements.TryAdd(inProgressTask.Id, inProgress with { TaskId = inProgressTask.Id }))
            {
                return Invalid(
                    "IN_PROGRESS_TASK_OUT_OF_SCOPE",
                    $"Task {inProgress.TaskId} is in progress on the approved plan being replaced but is not in the replacement's task scope.",
                    inProgress.TaskId,
                    inProgress.TaskId);
            }
        }

        var orderedTasks = OrderTasks(requiredTasks, assetsById, out var dependencyViolation);
        if (dependencyViolation is not null)
        {
            return Result(PlanningScheduleOutcome.InvalidInput, [dependencyViolation]);
        }

        if (orderedTasks.Any(task =>
                task.DurationMinutes <= 0 ||
                task.RequiredCrewSize <= 0 ||
                !TryParseRequiredSkills(task.RequiredSkills, out _) ||
                task.RequiredPartQuantity < 0))
        {
            var invalidTask = orderedTasks.First(task =>
                task.DurationMinutes <= 0 ||
                task.RequiredCrewSize <= 0 ||
                !TryParseRequiredSkills(task.RequiredSkills, out _) ||
                task.RequiredPartQuantity < 0);
            return Invalid(
                "INVALID_TASK_REQUIREMENTS",
                $"Task {invalidTask.Id} has invalid authoritative duration, crew, required skills, or part requirements.",
                invalidTask.Id,
                invalidTask.Id);
        }

        var parts = await sparePartProvider.GetSparePartsAsync(window.Id, cancellationToken);
        var partsById = parts.ToDictionary(part => part.Id, StringComparer.OrdinalIgnoreCase);
        var compatiblePartIdsByAsset = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var assetId in selectedAssetIds)
        {
            var compatibleParts = await sparePartProvider.GetCompatibleSparePartsAsync(assetId, window.Id, cancellationToken);
            compatiblePartIdsByAsset[assetId] = compatibleParts
                .Select(part => part.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var hardBlockers = new List<PlanningValidationViolation>();
        var dataIssues = new List<PlanningValidationViolation>();
        var requiredParts = orderedTasks
            .Where(task => task.RequiredPartQuantity > 0)
            .GroupBy(task => task.RequiredPartId ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in requiredParts)
        {
            var task = requirement.First();
            if (string.IsNullOrWhiteSpace(requirement.Key))
            {
                hardBlockers.Add(new PlanningValidationViolation(
                    "TASK_PART_ID_MISSING",
                    $"Task {task.Id} requires a spare part but has no authoritative part ID.",
                    task.Id,
                    null));
                continue;
            }

            if (!partsById.TryGetValue(requirement.Key, out var part))
            {
                dataIssues.Add(new PlanningValidationViolation(
                    "PART_DATA_UNAVAILABLE",
                    $"Inventory data does not include required part {requirement.Key} for task {task.Id}; availability cannot be established.",
                    task.Id,
                    requirement.Key));
                continue;
            }

            var requiredQuantity = requirement.Sum(requiredTask => requiredTask.RequiredPartQuantity);
            // A replacement may also use what the approved plan it replaces holds, since its approval releases that
            var availableQuantity = part.AvailableQuantity + ReleasableQuantity(request.ReleasableReservedQuantities, part.Id);
            if (availableQuantity < requiredQuantity)
            {
                hardBlockers.Add(new PlanningValidationViolation(
                    "PART_UNAVAILABLE",
                    $"Spare part {part.Id} has {availableQuantity} available but {requiredQuantity} is required across the requested scope.",
                    task.Id,
                    part.Id));
            }

            foreach (var partTask in requirement)
            {
                if (!compatiblePartIdsByAsset.TryGetValue(partTask.AssetId, out var compatibleIds) ||
                    !compatibleIds.Contains(part.Id))
                {
                    hardBlockers.Add(new PlanningValidationViolation(
                        "PART_INCOMPATIBLE",
                        $"Spare part {part.Id} required by task {partTask.Id} is not compatible with asset {partTask.AssetId}.",
                        partTask.Id,
                        part.Id));
                }
            }
        }

        var criticalPathMinutes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in orderedTasks)
        {
            var predecessorMinutes = string.IsNullOrWhiteSpace(task.PredecessorTaskId) ||
                                     completedTaskIds.Contains(task.PredecessorTaskId)
                ? 0
                : criticalPathMinutes[task.PredecessorTaskId];
            criticalPathMinutes[task.Id] = predecessorMinutes + task.DurationMinutes;
            if (criticalPathMinutes[task.Id] > (window.End - window.Start).TotalMinutes)
            {
                hardBlockers.Add(new PlanningValidationViolation(
                    "TASKS_EXCEED_WINDOW",
                    $"The predecessor chain ending at task {task.Id} requires {criticalPathMinutes[task.Id]} minutes, exceeding window {window.Id}.",
                    task.Id,
                    window.Id));
                break;
            }
        }

        if (hardBlockers.Count > 0)
        {
            return Result(PlanningScheduleOutcome.Infeasible, hardBlockers);
        }

        if (dataIssues.Count > 0)
        {
            return Result(PlanningScheduleOutcome.DataUnavailable, dataIssues);
        }

        var technicians = await queries.GetTechniciansAsync(cancellationToken);
        if (technicians.Count == 0)
        {
            return Result(
                PlanningScheduleOutcome.DataUnavailable,
                [
                    new PlanningValidationViolation(
                        "TECHNICIAN_DATA_UNAVAILABLE",
                        "No technician records were returned; technician availability and qualified crew capacity cannot be established.",
                        null,
                        "Technicians")
                ]);
        }

        // Validate locked task assignments
        if (lockedTaskAssignments != null && lockedTaskAssignments.Count > 0)
        {
            var techniciansById = technicians.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var (taskId, lockedTechnicianId) in lockedTaskAssignments)
            {
                var task = orderedTasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
                if (task is null)
                    continue;

                if (!techniciansById.TryGetValue(lockedTechnicianId, out var lockedTechnician))
                {
                    hardBlockers.Add(new PlanningValidationViolation(
                        "LOCKED_TECHNICIAN_UNAVAILABLE",
                        $"Task {taskId} is locked to technician {lockedTechnicianId}, who is not available.",
                        taskId,
                        lockedTechnicianId));
                    continue;
                }

                var requiredSkills = TryParseRequiredSkills(task.RequiredSkills, out var skills) ? skills : [];
                var hasAllSkills = requiredSkills.All(skill =>
                    lockedTechnician.Skills.Any(s =>
                        string.Equals(s, skill, StringComparison.OrdinalIgnoreCase)));

                if (!hasAllSkills)
                {
                    hardBlockers.Add(new PlanningValidationViolation(
                        "LOCKED_TECHNICIAN_UNAVAILABLE",
                        $"Task {taskId} is locked to technician {lockedTechnicianId}, who lacks required skills.",
                        taskId,
                        lockedTechnicianId));
                    continue;
                }

                var technicianAvailable = lockedTechnician.Availability.Any(a =>
                    a.AvailableFrom <= window.Start && a.AvailableTo >= window.End);

                if (!technicianAvailable)
                {
                    hardBlockers.Add(new PlanningValidationViolation(
                        "LOCKED_TECHNICIAN_UNAVAILABLE",
                        $"Task {taskId} is locked to technician {lockedTechnicianId}, who is unavailable during the maintenance window.",
                        taskId,
                        lockedTechnicianId));
                }
            }

            if (hardBlockers.Count > 0)
            {
                return Result(PlanningScheduleOutcome.Infeasible, hardBlockers);
            }
        }

        foreach (var task in orderedTasks)
        {
            var qualifiedTechnicians = technicians
                .Where(technician => HasRequiredSkills(technician, task))
                .ToArray();
            var availableForAnySlot = qualifiedTechnicians.Count(technician =>
                HasAvailableGridSlot(technician, task.DurationMinutes, window));
            if (availableForAnySlot < task.RequiredCrewSize)
            {
                hardBlockers.Add(new PlanningValidationViolation(
                    "INSUFFICIENT_QUALIFIED_TECHNICIANS",
                    $"Task {task.Id} requires {task.RequiredCrewSize} qualified technicians, but only {availableForAnySlot} are available for any full task interval in window {window.Id}.",
                    task.Id,
                    window.Id));
            }
        }

        if (hardBlockers.Count > 0)
        {
            return Result(PlanningScheduleOutcome.Infeasible, hardBlockers);
        }

        var candidates = new List<PlanningCandidate>();
        var searchViolations = new List<PlanningValidationViolation>();
        var techniciansByIdLookup = technicians.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var policy in SchedulingPolicies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = TryBuildCandidate(
                orderedTasks,
                window,
                technicians,
                policy,
                lockedTaskAssignments,
                completedTaskIds,
                inProgressPlacements,
                cancellationToken,
                out var schedulingViolation);
            if (candidate is null)
            {
                if (schedulingViolation is not null)
                    searchViolations.Add(schedulingViolation);
                continue;
            }

            var validation = await validator.ValidateAsync(
                new PlanningCandidateValidationRequest(request.AssetIds, window.Id, candidate)
                {
                    CompletedTaskIds = request.CompletedTaskIds,
                    ReleasableReservedQuantities = request.ReleasableReservedQuantities
                },
                cancellationToken);
            if (!validation.IsFeasible)
            {
                searchViolations.AddRange(validation.Violations);
                continue;
            }

            var scheduledTaskIds = new HashSet<string>(
                candidate.TaskPlacements.Select(p => p.TaskId),
                StringComparer.OrdinalIgnoreCase);

            var dependencyTaskIds = new HashSet<string>(
                scheduledTaskIds
                    .Where(taskId => !requestedTaskIds.Contains(taskId)),
                StringComparer.OrdinalIgnoreCase);

            var deferredTaskIds = new HashSet<string>(
                requestedTaskIds.Where(taskId => !scheduledTaskIds.Contains(taskId)),
                StringComparer.OrdinalIgnoreCase);

            var deferralReasons = new List<PlanningDeferralReason>();
            foreach (var deferredTaskId in deferredTaskIds)
            {
                var reason = DetermineDeferralReason(deferredTaskId, orderedTasks, taskGroups);
                deferralReasons.Add(new PlanningDeferralReason(deferredTaskId, reason));
            }

            var metrics = costCalculator.CalculateMetrics(candidate, techniciansByIdLookup);

            var candidateWithMetadata = candidate with
            {
                RequestedTaskIds = requestedTaskIds.ToList(),
                DependencyTaskIds = dependencyTaskIds.ToList(),
                DeferralReasons = deferralReasons,
                TotalLabourCostAmount = metrics.TotalLabourCostAmount,
                TotalLabourCostCurrency = metrics.TotalLabourCostCurrency,
                CompletionTimeMinutes = metrics.CompletionTimeMinutes,
                CostComparabilityWarning = metrics.CostWarning
            };

            if (!candidates.Any(existing => HaveSamePlacements(existing, candidateWithMetadata)))
            {
                candidates.Add(candidateWithMetadata);
            }

            if (candidates.Count == MaximumCandidates)
                break;
        }

        CandidateCostCalculator.ApplyRankingMetadata(candidates);

        if (candidates.Count > 0)
        {
            return new PlanningScheduleResult(
                PlanningScheduleOutcome.CandidatesGenerated,
                candidates,
                [],
                []);
        }

        if (searchViolations.Count == 0)
        {
            searchViolations.Add(new PlanningValidationViolation(
                "NO_CANDIDATE_FOUND",
                "The bounded deterministic scheduling policies did not produce a candidate; this does not prove that no feasible schedule exists.",
                null,
                window.Id));
        }

        return Result(PlanningScheduleOutcome.SearchLimitReached, searchViolations);

        void IncludeLinearSuccessors(string taskId)
        {
            var currentTaskId = taskId;
            while (successorsByTaskId.TryGetValue(currentTaskId, out var successors))
            {
                if (successors.Count != 1)
                {
                    break;
                }

                var successorId = successors[0];
                if (requiredTasks.ContainsKey(successorId))
                {
                    break;
                }

                if (!taskGroups.TryGetValue(successorId, out var matches) || matches.Length != 1)
                {
                    scopeViolations.Add(new PlanningValidationViolation(
                        "SUCCESSOR_TASK_INVALID",
                        $"Successor task {successorId} of {currentTaskId} is invalid or ambiguous.",
                        successorId,
                        successorId));
                    break;
                }

                var successor = matches[0];
                if (!selectedAssetIds.Contains(successor.AssetId))
                {
                    scopeViolations.Add(new PlanningValidationViolation(
                        "SUCCESSOR_OUT_OF_SCOPE",
                        $"Successor task {successorId} belongs to unselected asset {successor.AssetId}.",
                        successorId,
                        successor.AssetId));
                    break;
                }

                requiredTasks[successorId] = successor;
                currentTaskId = successorId;
            }
        }

        void IncludeTaskAndPredecessors(string taskId, bool isRequested)
        {
            if (visitStates.TryGetValue(taskId, out var state))
            {
                if (state == VisitState.Visiting)
                {
                    scopeViolations.Add(new PlanningValidationViolation(
                        "TASK_DEPENDENCY_CYCLE",
                        $"Task dependency cycle detected at task {taskId}.",
                        taskId,
                        taskId));
                }

                return;
            }

            if (!taskGroups.TryGetValue(taskId, out var matches))
            {
                scopeViolations.Add(new PlanningValidationViolation(
                    "TASK_NOT_FOUND",
                    $"Required task {taskId} does not exist in the authoritative task catalogues.",
                    taskId,
                    taskId));
                return;
            }

            if (matches.Length != 1)
            {
                scopeViolations.Add(new PlanningValidationViolation(
                    "AMBIGUOUS_TASK_ID",
                    $"Task ID {taskId} is not unique across task catalogues.",
                    taskId,
                    taskId));
                return;
            }

            var task = matches[0];
            if (!selectedAssetIds.Contains(task.AssetId))
            {
                scopeViolations.Add(new PlanningValidationViolation(
                    isRequested ? "TASK_OUT_OF_SCOPE" : "PREDECESSOR_OUT_OF_SCOPE",
                    isRequested
                        ? $"Required task {task.Id} belongs to asset {task.AssetId}, which was not selected."
                        : $"Predecessor task {task.Id} belongs to unselected asset {task.AssetId}.",
                    task.Id,
                    task.AssetId));
                return;
            }

            visitStates[taskId] = VisitState.Visiting;
            requiredTasks[task.Id] = task;
            if (!string.IsNullOrWhiteSpace(task.PredecessorTaskId))
            {
                IncludeTaskAndPredecessors(task.PredecessorTaskId, isRequested: false);
            }

            visitStates[taskId] = VisitState.Visited;
        }
    }

    private static IReadOnlyList<MaintenanceTaskReadModel> OrderTasks(
        IReadOnlyDictionary<string, MaintenanceTaskReadModel> tasks,
        IReadOnlyDictionary<string, AssetReadModel> assetsById,
        out PlanningValidationViolation? violation)
    {
        var ordered = new List<MaintenanceTaskReadModel>(tasks.Count);
        var remaining = new HashSet<string>(tasks.Keys, StringComparer.OrdinalIgnoreCase);
        while (remaining.Count > 0)
        {
            var next = remaining
                .Select(taskId => tasks[taskId])
                .Where(task => string.IsNullOrWhiteSpace(task.PredecessorTaskId) ||
                               !remaining.Contains(task.PredecessorTaskId))
                .OrderBy(task => assetsById[task.AssetId].Priority)
                .ThenBy(task => task.AssetId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (next is null)
            {
                violation = new PlanningValidationViolation(
                    "TASK_DEPENDENCY_CYCLE",
                    "The requested task dependencies contain a cycle.",
                    null,
                    null);
                return [];
            }

            ordered.Add(next);
            remaining.Remove(next.Id);
        }

        violation = null;
        return ordered;
    }

    private static PlanningCandidate? TryBuildCandidate(
        IReadOnlyList<MaintenanceTaskReadModel> orderedTasks,
        MaintenanceWindowReadModel window,
        IReadOnlyList<TechnicianReadModel> technicians,
        SchedulingPolicy policy,
        IReadOnlyDictionary<string, string>? lockedTaskAssignments,
        IReadOnlySet<string> completedTaskIds,
        IReadOnlyDictionary<string, PlanningTaskPlacement> inProgressPlacements,
        CancellationToken cancellationToken,
        out PlanningValidationViolation? violation)
    {
        var placements = new List<PlanningTaskPlacement>(orderedTasks.Count);
        var placementsByTaskId = new Dictionary<string, PlanningTaskPlacement>(StringComparer.OrdinalIgnoreCase);

        // Work in progress keeps its approved placement; it is placed first so the other tasks are fitted around its
        // technicians' time. The candidate validator still checks it against current data.
        foreach (var task in orderedTasks)
        {
            if (inProgressPlacements.TryGetValue(task.Id, out var inProgress))
            {
                placements.Add(inProgress);
                placementsByTaskId.Add(task.Id, inProgress);
            }
        }

        foreach (var task in orderedTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inProgressPlacements.ContainsKey(task.Id))
            {
                if (!string.IsNullOrWhiteSpace(task.PredecessorTaskId) &&
                    !completedTaskIds.Contains(task.PredecessorTaskId) &&
                    (!placementsByTaskId.TryGetValue(task.PredecessorTaskId, out var inProgressPredecessor) ||
                     inProgressPredecessor.End > placementsByTaskId[task.Id].Start))
                {
                    violation = new PlanningValidationViolation(
                        "IN_PROGRESS_TASK_NOT_PRESERVABLE",
                        $"Task {task.Id} is in progress at its approved time, but its unfinished predecessor {task.PredecessorTaskId} cannot be scheduled before it.",
                        task.Id,
                        task.PredecessorTaskId);
                    return null;
                }

                continue;
            }

            var earliestStart = window.Start;
            if (!string.IsNullOrWhiteSpace(task.PredecessorTaskId) &&
                !completedTaskIds.Contains(task.PredecessorTaskId))
            {
                if (!placementsByTaskId.TryGetValue(task.PredecessorTaskId, out var predecessor))
                {
                    violation = new PlanningValidationViolation(
                        "PREDECESSOR_NOT_SCHEDULED",
                        $"The scheduler could not place predecessor {task.PredecessorTaskId} before task {task.Id}.",
                        task.Id,
                        task.PredecessorTaskId);
                    return null;
                }

                earliestStart = predecessor.End;
            }

            var start = AlignToGrid(earliestStart, window.Start);
            PlanningTaskPlacement? placement = null;
            while (start.AddMinutes(task.DurationMinutes) <= window.End)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = start.AddMinutes(task.DurationMinutes);
                var availableTechnicians = technicians
                    .Where(technician =>
                        HasRequiredSkills(technician, task) &&
                        HasAvailability(technician, start, end) &&
                        IsTechnicianFree(technician.Id, start, end, placements))
                    .OrderBy(technician => policy.TechnicianRank(technician))
                    .ThenBy(technician => technician.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // Check if task has a lock
                if (lockedTaskAssignments?.TryGetValue(task.Id, out var lockedTechnicianId) == true)
                {
                    // Enforce lock: locked technician MUST be in the placement
                    var lockedTechnician = availableTechnicians.FirstOrDefault(t =>
                        string.Equals(t.Id, lockedTechnicianId, StringComparison.OrdinalIgnoreCase));

                    if (lockedTechnician is null)
                    {
                        // Locked technician not available at this slot - try next slot
                        start = start.Add(SchedulingIncrement);
                        continue;
                    }

                    // Build crew with locked technician first, then other available technicians
                    var crew = new List<string> { lockedTechnician.Id };
                    var otherAvailable = availableTechnicians
                        .Where(t => !string.Equals(t.Id, lockedTechnicianId, StringComparison.OrdinalIgnoreCase))
                        .Take(task.RequiredCrewSize - 1)
                        .Select(t => t.Id)
                        .ToList();

                    crew.AddRange(otherAvailable);

                    if (crew.Count == task.RequiredCrewSize)
                    {
                        placement = new PlanningTaskPlacement(task.Id, start, end, crew);
                        break;
                    }

                    // Locked technician is available but cannot form complete crew - try next slot
                    start = start.Add(SchedulingIncrement);
                    continue;
                }

                // No lock - use existing logic
                var selectedTechnicians = availableTechnicians.Take(task.RequiredCrewSize).ToArray();
                if (selectedTechnicians.Length == task.RequiredCrewSize)
                {
                    placement = new PlanningTaskPlacement(
                        task.Id,
                        start,
                        end,
                        selectedTechnicians.Select(technician => technician.Id).ToArray());
                    break;
                }

                start = start.Add(SchedulingIncrement);
            }

            if (placement is null)
            {
                // FR05: a lock is never dropped to find a slot; the conflict names the lock so the user can resolve it
                violation = lockedTaskAssignments?.TryGetValue(task.Id, out var blockingTechnicianId) == true
                    ? new PlanningValidationViolation(
                        "LOCKED_ASSIGNMENT_NOT_SCHEDULABLE",
                        $"Task {task.Id} is locked to technician {blockingTechnicianId}, but the bounded {policy.Name} policy found no 30-minute-grid slot in window {window.Id} where {blockingTechnicianId} and a full qualified crew are free. Unlock the task or lock it to another technician.",
                        task.Id,
                        blockingTechnicianId)
                    : new PlanningValidationViolation(
                        "NO_HEURISTIC_SLOT",
                        $"The bounded {policy.Name} policy could not find a 30-minute-grid slot and qualified crew for task {task.Id} in window {window.Id}.",
                        task.Id,
                        window.Id);
                return null;
            }

            placements.Add(placement);
            placementsByTaskId.Add(task.Id, placement);
        }

        violation = null;
        var evidenceReferences = orderedTasks
            .Select(task => new PlanningEvidenceReference(
                PlanningEvidenceSourceKind.MaintenanceTask,
                task.Id,
                task.SourceDocumentId,
                Version: null,
                Page: null,
                task.SourceSection,
                Excerpt: null))
            .ToArray();
        // Placements are reported in plan (dependency) order, wherever the in-progress ones were placed
        var orderedPlacements = orderedTasks.Select(task => placementsByTaskId[task.Id]).ToList();
        return new PlanningCandidate(orderedPlacements, policy.Rationale, evidenceReferences);
    }

    private static bool HasAvailableGridSlot(
        TechnicianReadModel technician,
        int durationMinutes,
        MaintenanceWindowReadModel window)
    {
        for (var start = window.Start;
             start.AddMinutes(durationMinutes) <= window.End;
             start = start.Add(SchedulingIncrement))
        {
            if (HasAvailability(technician, start, start.AddMinutes(durationMinutes)))
                return true;
        }

        return false;
    }

    private static bool HasRequiredSkills(TechnicianReadModel technician, MaintenanceTaskReadModel task)
    {
        if (!TryParseRequiredSkills(task.RequiredSkills, out var requiredSkills))
            return false;

        return requiredSkills.All(requiredSkill =>
            technician.Skills.Contains(requiredSkill, StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryParseRequiredSkills(string? value, out string[] skills)
    {
        skills = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        skills = value.Split(',', StringSplitOptions.TrimEntries);
        return skills.Length > 0 && skills.All(skill => !string.IsNullOrWhiteSpace(skill));
    }

    private static bool HasAvailability(
        TechnicianReadModel technician,
        DateTimeOffset start,
        DateTimeOffset end) =>
        technician.Availability.Any(availability =>
            availability.AvailableFrom <= start && availability.AvailableTo >= end);

    private static bool IsTechnicianFree(
        string technicianId,
        DateTimeOffset start,
        DateTimeOffset end,
        IEnumerable<PlanningTaskPlacement> placements) =>
        placements.All(placement =>
            !placement.TechnicianIds.Contains(technicianId, StringComparer.OrdinalIgnoreCase) ||
            start >= placement.End ||
            placement.Start >= end);

    private static decimal ReleasableQuantity(IReadOnlyDictionary<string, decimal> releasable, string partId) =>
        releasable.FirstOrDefault(entry => string.Equals(entry.Key, partId, StringComparison.OrdinalIgnoreCase)).Value;

    private static string DetermineDeferralReason(
        string taskId,
        IReadOnlyList<MaintenanceTaskReadModel> orderedTasks,
        IReadOnlyDictionary<string, MaintenanceTaskReadModel[]> taskGroups)
    {
        // Check if task exists in catalogue
        if (!taskGroups.TryGetValue(taskId, out _))
            return "Task not found in asset catalogue; cannot schedule unknown task.";

        // Check if task would have unsatisfied predecessor
        var task = taskGroups[taskId][0];
        if (!string.IsNullOrWhiteSpace(task.PredecessorTaskId) &&
            !orderedTasks.Any(t => string.Equals(t.Id, task.PredecessorTaskId, StringComparison.OrdinalIgnoreCase)))
            return $"Predecessor task {task.PredecessorTaskId} is deferred; cannot schedule dependent task.";

        // If we can't determine the specific reason, report it as unknown
        return "Task scheduling deferred; specific reason requires inspection of candidate violations.";
    }

    private static DateTimeOffset AlignToGrid(DateTimeOffset requested, DateTimeOffset gridOrigin)
    {
        if (requested <= gridOrigin)
            return gridOrigin;

        var elapsedTicks = requested.Ticks - gridOrigin.Ticks;
        var incrementTicks = SchedulingIncrement.Ticks;
        var gridSteps = (elapsedTicks + incrementTicks - 1) / incrementTicks;
        return gridOrigin.AddTicks(gridSteps * incrementTicks);
    }

    private static bool HaveSamePlacements(PlanningCandidate left, PlanningCandidate right) =>
        left.TaskPlacements.Count == right.TaskPlacements.Count &&
        left.TaskPlacements.OrderBy(placement => placement.TaskId, StringComparer.OrdinalIgnoreCase)
            .Zip(right.TaskPlacements.OrderBy(placement => placement.TaskId, StringComparer.OrdinalIgnoreCase))
            .All(pair =>
                string.Equals(pair.First.TaskId, pair.Second.TaskId, StringComparison.OrdinalIgnoreCase) &&
                pair.First.Start == pair.Second.Start &&
                pair.First.End == pair.Second.End &&
                pair.First.TechnicianIds.Count == pair.Second.TechnicianIds.Count &&
                pair.First.TechnicianIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(pair.Second.TechnicianIds));

    private static bool HasDuplicates(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);

    private static PlanningScheduleResult Clarification(string question) =>
        new(PlanningScheduleOutcome.ClarificationRequired, [], [], [question]);

    private static PlanningScheduleResult Invalid(
        string code,
        string message,
        string? taskId,
        string? resourceId) =>
        Result(
            PlanningScheduleOutcome.InvalidInput,
            [new PlanningValidationViolation(code, message, taskId, resourceId)]);

    private static PlanningScheduleResult Result(
        PlanningScheduleOutcome outcome,
        IReadOnlyList<PlanningValidationViolation> violations) =>
        new(outcome, [], violations, []);

    private static readonly SchedulingPolicy[] SchedulingPolicies =
    [
        new(
            "stable technician ID",
            technician => 0,
            "Earliest feasible schedule using stable technician-ID tie-breaking."),
        new(
            "lowest labour cost",
            technician => technician.HourlyRate,
            "Earliest feasible schedule using the lowest hourly-cost qualified crew.")
    ];

    private sealed record SchedulingPolicy(
        string Name,
        Func<TechnicianReadModel, decimal> TechnicianRank,
        string Rationale);

    private enum VisitState
    {
        Visiting,
        Visited
    }
}
