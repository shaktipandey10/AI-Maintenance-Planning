using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;

namespace MaintenancePlanner.Application.Validation;

public sealed class PlanningCandidateValidator(
    ISparePartReadModelProvider sparePartProvider,
    IMaintenancePlannerQueries queries)
{
    public async Task<PlanningCandidateValidationResult> ValidateAsync(
        PlanningCandidateValidationRequest request,
        CancellationToken cancellationToken)
    {
        var violations = new List<PlanningValidationViolation>();
        if (request is null)
        {
            return Invalid(
                "INVALID_REQUEST",
                "A candidate validation request is required.",
                taskId: null,
                resourceId: null);
        }

        var requestedAssetIds = request.AssetIds;
        var candidate = request.Candidate;
        if (requestedAssetIds is null || requestedAssetIds.Count == 0)
        {
            violations.Add(new PlanningValidationViolation(
                "ASSETS_REQUIRED",
                "At least one asset must be selected for candidate validation.",
                TaskId: null,
                ResourceId: null));
        }

        if (candidate is null)
        {
            violations.Add(new PlanningValidationViolation(
                "CANDIDATE_REQUIRED",
                "A planning candidate is required.",
                TaskId: null,
                ResourceId: null));
        }

        if (requestedAssetIds is null || candidate is null)
            return ToResult(violations);

        var assetIds = requestedAssetIds
            .Where(assetId => !string.IsNullOrWhiteSpace(assetId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedAssetIds.Any(string.IsNullOrWhiteSpace))
        {
            violations.Add(new PlanningValidationViolation(
                "INVALID_ASSET_ID",
                "Selected asset IDs cannot be blank.",
                TaskId: null,
                ResourceId: null));
        }

        foreach (var duplicateAssetId in requestedAssetIds
                     .Where(assetId => !string.IsNullOrWhiteSpace(assetId))
                     .GroupBy(assetId => assetId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            violations.Add(new PlanningValidationViolation(
                "DUPLICATE_ASSET_ID",
                $"Asset {duplicateAssetId} is selected more than once.",
                TaskId: null,
                ResourceId: duplicateAssetId));
        }

        if (string.IsNullOrWhiteSpace(request.MaintenanceWindowId))
        {
            violations.Add(new PlanningValidationViolation(
                "WINDOW_REQUIRED",
                "A maintenance window ID is required.",
                TaskId: null,
                ResourceId: null));
        }

        var taskPlacements = candidate.TaskPlacements;
        if (taskPlacements is null)
        {
            violations.Add(new PlanningValidationViolation(
                "TASK_PLACEMENTS_REQUIRED",
                "The candidate task placements collection is required.",
                TaskId: null,
                ResourceId: null));
            return ToResult(violations);
        }

        if (violations.Count > 0)
            return ToResult(violations);

        var assets = await queries.GetAssetsAsync(cancellationToken);
        var knownAssetIds = assets.Select(asset => asset.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var assetId in assetIds.Where(assetId => !knownAssetIds.Contains(assetId)))
        {
            violations.Add(new PlanningValidationViolation(
                "ASSET_NOT_FOUND",
                $"Selected asset {assetId} does not exist.",
                TaskId: null,
                ResourceId: assetId));
        }

        var windows = await queries.GetMaintenanceWindowsAsync(cancellationToken);
        var window = windows.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, request.MaintenanceWindowId, StringComparison.OrdinalIgnoreCase));
        if (window is null)
        {
            violations.Add(new PlanningValidationViolation(
                "WINDOW_NOT_FOUND",
                $"Maintenance window {request.MaintenanceWindowId} does not exist.",
                TaskId: null,
                ResourceId: request.MaintenanceWindowId));
        }
        else if (window.Start >= window.End)
        {
            violations.Add(new PlanningValidationViolation(
                "INVALID_WINDOW_INTERVAL",
                $"Maintenance window {window.Id} must end after it starts.",
                TaskId: null,
                ResourceId: window.Id));
        }

        var tasksById = new Dictionary<string, MaintenanceTaskReadModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            var taskCatalogue = await queries.GetTaskCatalogueAsync(asset.Id, cancellationToken);
            if (taskCatalogue is null)
            {
                if (assetIds.Contains(asset.Id, StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add(new PlanningValidationViolation(
                        "TASK_CATALOGUE_UNAVAILABLE",
                        $"The task catalogue for asset {asset.Id} could not be retrieved.",
                        TaskId: null,
                        ResourceId: asset.Id));
                }

                continue;
            }

            foreach (var task in taskCatalogue)
            {
                if (!tasksById.TryAdd(task.Id, task))
                {
                    violations.Add(new PlanningValidationViolation(
                        "AMBIGUOUS_TASK_ID",
                        $"Task ID {task.Id} is not unique across the selected assets.",
                        task.Id,
                        task.Id));
                }
            }
        }

        var technicians = await queries.GetTechniciansAsync(cancellationToken);
        var techniciansById = technicians
            .GroupBy(technician => technician.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var spareParts = await sparePartProvider.GetSparePartsAsync(request.MaintenanceWindowId, cancellationToken);
        var sparePartsById = spareParts
            .GroupBy(part => part.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var compatiblePartIdsByAsset = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var assetId in assetIds.Where(knownAssetIds.Contains))
        {
            var compatibleParts = await sparePartProvider.GetCompatibleSparePartsAsync(assetId, request.MaintenanceWindowId, cancellationToken);
            compatiblePartIdsByAsset[assetId] = compatibleParts
                .Select(part => part.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var placements = taskPlacements;
        var placementsByTaskId = new Dictionary<string, PlanningTaskPlacement>(StringComparer.OrdinalIgnoreCase);
        var partRequirements = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var assignedTechniciansByPlacement = new List<(PlanningTaskPlacement Placement, string TechnicianId)>();

        foreach (var placement in placements)
        {
            if (placement is null)
            {
                violations.Add(new PlanningValidationViolation(
                    "INVALID_TASK_PLACEMENT",
                    "A task placement cannot be null.",
                    TaskId: null,
                    ResourceId: null));
                continue;
            }

            if (string.IsNullOrWhiteSpace(placement.TaskId))
            {
                violations.Add(new PlanningValidationViolation(
                    "TASK_ID_REQUIRED",
                    "Every task placement must identify a task.",
                    TaskId: null,
                    ResourceId: null));
                continue;
            }

            if (!placementsByTaskId.TryAdd(placement.TaskId, placement))
            {
                violations.Add(new PlanningValidationViolation(
                    "DUPLICATE_TASK_PLACEMENT",
                    $"Task {placement.TaskId} has more than one placement.",
                    placement.TaskId,
                    placement.TaskId));
                continue;
            }

            if (!tasksById.TryGetValue(placement.TaskId, out var task))
            {
                violations.Add(new PlanningValidationViolation(
                    "TASK_NOT_FOUND",
                    $"Task {placement.TaskId} is not in the authoritative task catalogues for the selected assets.",
                    placement.TaskId,
                    placement.TaskId));
                continue;
            }

            if (!assetIds.Contains(task.AssetId, StringComparer.OrdinalIgnoreCase))
            {
                violations.Add(new PlanningValidationViolation(
                    "TASK_ASSET_MISMATCH",
                    $"Task {task.Id} belongs to asset {task.AssetId}, which is not selected.",
                    task.Id,
                    task.AssetId));
                continue;
            }

            var intervalIsValid = placement.Start < placement.End;
            if (!intervalIsValid)
            {
                violations.Add(new PlanningValidationViolation(
                    "INVALID_TASK_INTERVAL",
                    $"Task {task.Id} must end after it starts.",
                    task.Id,
                    null));
            }
            else
            {
                if (task.DurationMinutes <= 0 ||
                    placement.End - placement.Start != TimeSpan.FromMinutes(task.DurationMinutes))
                {
                    violations.Add(new PlanningValidationViolation(
                        "TASK_DURATION_MISMATCH",
                        $"Task {task.Id} must have its authoritative duration of {task.DurationMinutes} minutes.",
                        task.Id,
                        null));
                }

                if (window is not null &&
                    window.Start < window.End &&
                    (placement.Start < window.Start || placement.End > window.End))
                {
                    violations.Add(new PlanningValidationViolation(
                        "TASK_OUTSIDE_WINDOW",
                        $"Task {task.Id} must be scheduled within maintenance window {window.Id}.",
                        task.Id,
                        window.Id));
                }
            }

            if (!TryParseRequiredSkills(task.RequiredSkills, out var requiredSkills))
            {
                violations.Add(new PlanningValidationViolation(
                    "INVALID_REQUIRED_SKILLS",
                    $"Task {task.Id} has malformed or empty authoritative required-skill data.",
                    task.Id,
                    task.Id));
            }
            else
            {
                ValidateTechnicianAssignments(
                    placement,
                    task,
                    requiredSkills,
                    techniciansById,
                    assignedTechniciansByPlacement,
                    violations);
            }

            if (task.RequiredPartQuantity > 0)
            {
                if (string.IsNullOrWhiteSpace(task.RequiredPartId))
                {
                    violations.Add(new PlanningValidationViolation(
                        "TASK_PART_ID_MISSING",
                        $"Task {task.Id} requires a spare part but has no authoritative part ID.",
                        task.Id,
                        null));
                }
                else if (!sparePartsById.TryGetValue(task.RequiredPartId, out var part))
                {
                    violations.Add(new PlanningValidationViolation(
                        "PART_DATA_UNAVAILABLE",
                        $"Inventory data does not include required part {task.RequiredPartId} for task {task.Id}; availability cannot be established.",
                        task.Id,
                        task.RequiredPartId));
                }
                else
                {
                    partRequirements[part.Id] = partRequirements.GetValueOrDefault(part.Id) +
                                                task.RequiredPartQuantity;
                    if (!compatiblePartIdsByAsset.TryGetValue(task.AssetId, out var compatiblePartIds) ||
                        !compatiblePartIds.Contains(part.Id))
                    {
                        violations.Add(new PlanningValidationViolation(
                            "PART_INCOMPATIBLE",
                            $"Spare part {part.Id} required by task {task.Id} is not compatible with asset {task.AssetId}.",
                            task.Id,
                            part.Id));
                    }
                }
            }
        }

        ValidatePredecessors(tasksById, placementsByTaskId, violations);
        ValidateTechnicianOverlaps(assignedTechniciansByPlacement, violations);

        foreach (var requirement in partRequirements)
        {
            var part = sparePartsById[requirement.Key];
            if (part.AvailableQuantity < requirement.Value)
            {
                violations.Add(new PlanningValidationViolation(
                    "PART_UNAVAILABLE",
                    $"Spare part {part.Id} has {part.AvailableQuantity} available but {requirement.Value} is required across the candidate.",
                    TaskId: null,
                    ResourceId: part.Id));
            }
        }

        return ToResult(violations);
    }

    private static void ValidateTechnicianAssignments(
        PlanningTaskPlacement placement,
        MaintenanceTaskReadModel task,
        IReadOnlyList<string> requiredSkills,
        IReadOnlyDictionary<string, TechnicianReadModel> techniciansById,
        ICollection<(PlanningTaskPlacement Placement, string TechnicianId)> assignedTechnicians,
        ICollection<PlanningValidationViolation> violations)
    {
        if (placement.TechnicianIds is null)
        {
            violations.Add(new PlanningValidationViolation(
                "TECHNICIANS_REQUIRED",
                $"Task {task.Id} must provide its assigned technicians.",
                task.Id,
                null));
            return;
        }

        var distinctTechnicianIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var technicianId in placement.TechnicianIds)
        {
            if (string.IsNullOrWhiteSpace(technicianId))
            {
                violations.Add(new PlanningValidationViolation(
                    "TECHNICIAN_ID_REQUIRED",
                    $"Task {task.Id} contains a blank technician ID.",
                    task.Id,
                    null));
                continue;
            }

            if (!distinctTechnicianIds.Add(technicianId))
            {
                violations.Add(new PlanningValidationViolation(
                    "DUPLICATE_TECHNICIAN_ASSIGNMENT",
                    $"Technician {technicianId} is assigned more than once to task {task.Id}.",
                    task.Id,
                    technicianId));
                continue;
            }

            if (!techniciansById.TryGetValue(technicianId, out var technician))
            {
                violations.Add(new PlanningValidationViolation(
                    "TECHNICIAN_NOT_FOUND",
                    $"Assigned technician {technicianId} for task {task.Id} does not exist.",
                    task.Id,
                    technicianId));
                continue;
            }

            assignedTechnicians.Add((placement, technician.Id));
            if (!requiredSkills.All(requiredSkill =>
                    technician.Skills.Contains(requiredSkill, StringComparer.OrdinalIgnoreCase)))
            {
                violations.Add(new PlanningValidationViolation(
                    "TECHNICIAN_UNQUALIFIED",
                    $"Technician {technician.Id} does not have every required skill for task {task.Id}.",
                    task.Id,
                    technician.Id));
            }

            if (placement.Start < placement.End &&
                !technician.Availability.Any(availability =>
                    availability.AvailableFrom <= placement.Start &&
                    availability.AvailableTo >= placement.End))
            {
                violations.Add(new PlanningValidationViolation(
                    "TECHNICIAN_UNAVAILABLE",
                    $"Technician {technician.Id} is not available for the full interval of task {task.Id}.",
                    task.Id,
                    technician.Id));
            }
        }

        if (distinctTechnicianIds.Count < task.RequiredCrewSize)
        {
            violations.Add(new PlanningValidationViolation(
                "CREW_SIZE_UNMET",
                $"Task {task.Id} requires {task.RequiredCrewSize} distinct technicians but has {distinctTechnicianIds.Count} assigned.",
                task.Id,
                null));
        }
    }

    private static void ValidatePredecessors(
        IReadOnlyDictionary<string, MaintenanceTaskReadModel> tasksById,
        IReadOnlyDictionary<string, PlanningTaskPlacement> placementsByTaskId,
        ICollection<PlanningValidationViolation> violations)
    {
        foreach (var (taskId, placement) in placementsByTaskId)
        {
            if (!tasksById.TryGetValue(taskId, out var task) ||
                string.IsNullOrWhiteSpace(task.PredecessorTaskId))
            {
                continue;
            }

            if (!tasksById.ContainsKey(task.PredecessorTaskId))
            {
                violations.Add(new PlanningValidationViolation(
                    "PREDECESSOR_NOT_FOUND",
                    $"Task {task.Id} references predecessor {task.PredecessorTaskId}, which is not in the selected assets' task catalogues.",
                    task.Id,
                    task.PredecessorTaskId));
                continue;
            }

            if (!placementsByTaskId.TryGetValue(task.PredecessorTaskId, out var predecessorPlacement))
            {
                violations.Add(new PlanningValidationViolation(
                    "PREDECESSOR_NOT_SCHEDULED",
                    $"Task {task.Id} cannot be scheduled because predecessor {task.PredecessorTaskId} has no candidate placement.",
                    task.Id,
                    task.PredecessorTaskId));
                continue;
            }

            if (predecessorPlacement.End > placement.Start)
            {
                violations.Add(new PlanningValidationViolation(
                    "PREDECESSOR_ORDER_VIOLATION",
                    $"Predecessor {task.PredecessorTaskId} must finish before task {task.Id} starts.",
                    task.Id,
                    task.PredecessorTaskId));
            }
        }
    }

    private static void ValidateTechnicianOverlaps(
        IReadOnlyCollection<(PlanningTaskPlacement Placement, string TechnicianId)> assignments,
        ICollection<PlanningValidationViolation> violations)
    {
        var orderedAssignments = assignments
            .Where(assignment => assignment.Placement.Start < assignment.Placement.End)
            .OrderBy(assignment => assignment.Placement.Start)
            .ThenBy(assignment => assignment.Placement.TaskId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var index = 0; index < orderedAssignments.Length; index++)
        {
            var current = orderedAssignments[index];
            for (var otherIndex = index + 1; otherIndex < orderedAssignments.Length; otherIndex++)
            {
                var other = orderedAssignments[otherIndex];
                if (other.Placement.Start >= current.Placement.End)
                    break;

                if (!string.Equals(current.TechnicianId, other.TechnicianId, StringComparison.OrdinalIgnoreCase) ||
                    current.Placement.Start >= other.Placement.End)
                {
                    continue;
                }

                violations.Add(new PlanningValidationViolation(
                    "TECHNICIAN_SLOT_CONFLICT",
                    $"Technician {current.TechnicianId} is assigned to overlapping tasks {current.Placement.TaskId} and {other.Placement.TaskId}.",
                    other.Placement.TaskId,
                    current.TechnicianId));
            }
        }
    }

    private static PlanningCandidateValidationResult Invalid(
        string code,
        string message,
        string? taskId,
        string? resourceId) =>
        new(false, [new PlanningValidationViolation(code, message, taskId, resourceId)]);

    private static bool TryParseRequiredSkills(string? value, out string[] skills)
    {
        skills = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        skills = value.Split(',', StringSplitOptions.TrimEntries);
        return skills.Length > 0 && skills.All(skill => !string.IsNullOrWhiteSpace(skill));
    }

    private static PlanningCandidateValidationResult ToResult(
        IReadOnlyList<PlanningValidationViolation> violations) =>
        new(violations.Count == 0, violations);
}
