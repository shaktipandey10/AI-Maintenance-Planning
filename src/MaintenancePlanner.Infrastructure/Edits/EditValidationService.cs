using System.Text.Json;
using MaintenancePlanner.Application.Edits;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Edits;

public sealed class EditValidationService(
    MaintenancePlannerDbContext dbContext,
    IMaintenancePlannerQueries queries,
    EditedPlanValidator? planValidator = null) : IEditValidationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EditValidationResult> ValidateEditsAsync(
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts
            .Include(d => d.PlanningJob)
            .FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);

        if (draft is null)
            throw new InvalidOperationException($"Draft {draftId} not found");

        if (draft.Status != DraftStatus.Approved)
            throw new InvalidOperationException($"Draft {draftId} is not in approved state");

        var planningJob = draft.PlanningJob;
        if (planningJob is null)
            throw new InvalidOperationException($"Planning job for draft {draftId} not found");

        // Fetch all edits for this draft
        var edits = await dbContext.EditedTaskPlacements
            .Where(e => e.DraftId == draftId)
            .ToListAsync(cancellationToken);

        if (edits.Count == 0)
            return new EditValidationResult(draftId, true, [], 0, 0);

        // Extract maintenance window from planning job request
        MaintenanceWindowReadModel? window = null;
        try
        {
            using var requestDoc = JsonDocument.Parse(planningJob.RequestPayload);
            if (requestDoc.RootElement.TryGetProperty("windowId", out var windowIdElem))
            {
                var windowId = windowIdElem.GetString();
                if (!string.IsNullOrEmpty(windowId))
                {
                    var windows = await queries.GetMaintenanceWindowsAsync(cancellationToken);
                    window = windows.FirstOrDefault(w => string.Equals(w.Id, windowId, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        catch
        {
            // If window extraction fails, proceed without window validation
        }

        // Fetch all technicians and spare parts
        var technicians = await queries.GetTechniciansAsync(cancellationToken);
        var spareParts = await queries.GetSparePartsAsync(cancellationToken);

        // Extract asset IDs from request payload
        var assetIdsFromRequest = ExtractAssetIdsFromRequest(planningJob.RequestPayload);

        // Fetch task definitions for tasks being edited
        var allTaskDefs = new Dictionary<string, MaintenanceTaskReadModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var assetId in assetIdsFromRequest)
        {
            var taskDefs = await queries.GetTaskCatalogueAsync(assetId, cancellationToken);
            if (taskDefs != null)
            {
                foreach (var td in taskDefs)
                {
                    if (!allTaskDefs.ContainsKey(td.Id))
                        allTaskDefs[td.Id] = td;
                }
            }
        }

        // The draft's own unconsumed reservations already hold its parts, so they count as available to its edits
        var heldByDraft = (await dbContext.Reservations
                .AsNoTracking()
                .Where(r => r.DraftId == draftId && r.Status == ReservationStatus.Confirmed && r.CompletedAtUtc == null)
                .Select(r => new { r.SparePartId, r.QuantityReserved })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.SparePartId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.QuantityReserved), StringComparer.OrdinalIgnoreCase);

        var violations = new List<ValidationViolation>();
        var violationsByEdit = new Dictionary<EditedTaskPlacement, List<ValidationViolation>>();
        var validCount = 0;

        // Validate each edit
        foreach (var edit in edits)
        {
            var editViolations = new List<ValidationViolation>();

            // Check if task exists
            if (!allTaskDefs.TryGetValue(edit.TaskId, out var taskDef))
            {
                editViolations.Add(new ValidationViolation(
                    edit.TaskId,
                    "TASK_NOT_FOUND",
                    $"Task definition not found for {edit.TaskId}"));
            }
            else
            {
                // Validate window
                if (window != null)
                {
                    if (edit.NewStart < window.Start || edit.NewEnd > window.End)
                    {
                        editViolations.Add(new ValidationViolation(
                            edit.TaskId,
                            "WINDOW_EXCEEDED",
                            $"Placement [{edit.NewStart:O}, {edit.NewEnd:O}] falls outside maintenance window [{window.Start:O}, {window.End:O}]"));
                    }

                    var duration = (edit.NewEnd - edit.NewStart).TotalMinutes;
                    if (duration < taskDef.DurationMinutes)
                    {
                        editViolations.Add(new ValidationViolation(
                            edit.TaskId,
                            "INSUFFICIENT_TIME",
                            $"Allocated time {duration:0} minutes is less than required {taskDef.DurationMinutes} minutes"));
                    }
                }

                // Validate spare parts
                if (!string.IsNullOrEmpty(taskDef.RequiredPartId) && taskDef.RequiredPartQuantity > 0)
                {
                    var part = spareParts.FirstOrDefault(p => string.Equals(p.Id, taskDef.RequiredPartId, StringComparison.OrdinalIgnoreCase));
                    if (part is null)
                    {
                        editViolations.Add(new ValidationViolation(
                            edit.TaskId,
                            "PART_NOT_FOUND",
                            $"Spare part {taskDef.RequiredPartId} not found"));
                    }
                    else if (part.AvailableQuantity + heldByDraft.GetValueOrDefault(part.Id) < taskDef.RequiredPartQuantity)
                    {
                        editViolations.Add(new ValidationViolation(
                            edit.TaskId,
                            "PART_UNAVAILABLE",
                            $"Spare part {taskDef.RequiredPartId} has {part.AvailableQuantity + heldByDraft.GetValueOrDefault(part.Id)} available to this plan but {taskDef.RequiredPartQuantity} required"));
                    }
                }

                // Validate technician availability and skills
                var requiredSkills = taskDef.RequiredSkills
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var assignedTechnicianIds = DeserializeStringArray(edit.NewTechnicianIds);
                var availableCount = 0;

                foreach (var techId in assignedTechnicianIds)
                {
                    var tech = technicians.FirstOrDefault(t => string.Equals(t.Id, techId, StringComparison.OrdinalIgnoreCase));

                    if (tech is null)
                    {
                        editViolations.Add(new ValidationViolation(
                            edit.TaskId,
                            "TECHNICIAN_NOT_FOUND",
                            $"Technician {techId} not found"));
                        continue;
                    }

                    // Check skills
                    var hasAllSkills = requiredSkills.All(requiredSkill =>
                        tech.Skills.Any(s => string.Equals(s, requiredSkill, StringComparison.OrdinalIgnoreCase)));

                    if (!hasAllSkills)
                    {
                        editViolations.Add(new ValidationViolation(
                            edit.TaskId,
                            "TECHNICIAN_SKILL_MISMATCH",
                            $"Technician {techId} lacks required skills: {string.Join(", ", requiredSkills)}"));
                        continue;
                    }

                    // Check availability
                    var isAvailable = tech.Availability.Any(av =>
                        av.AvailableFrom <= edit.NewStart && av.AvailableTo >= edit.NewEnd);

                    if (!isAvailable)
                    {
                        editViolations.Add(new ValidationViolation(
                            edit.TaskId,
                            "TECHNICIAN_UNAVAILABLE",
                            $"Technician {techId} is not available during [{edit.NewStart:O}, {edit.NewEnd:O}]"));
                        continue;
                    }

                    availableCount++;
                }

                // Check crew size
                if (availableCount < taskDef.RequiredCrewSize)
                {
                    editViolations.Add(new ValidationViolation(
                        edit.TaskId,
                        "INSUFFICIENT_CREW",
                        $"Task requires {taskDef.RequiredCrewSize} qualified technicians, but only {availableCount} are available"));
                }
            }

            violationsByEdit[edit] = editViolations;
        }

        // FR05: the edits are also checked together, as the plan they would produce, exactly as revision creation
        // checks them (order, overlaps, locks, task progress). A plan violation on an edited task invalidates that edit;
        // one on a task nobody edited (e.g. a successor the edits now overlap) invalidates every edit. Drafts without a
        // stored approved plan keep the edit-by-edit checks only.
        if (planValidator is not null &&
            await planValidator.CheckAsync(draft, planningJob.RequestPayload, edits, cancellationToken) is { } plan)
        {
            foreach (var planViolation in plan.Violations)
            {
                var affected = edits.Where(e => string.Equals(e.TaskId, planViolation.TaskId, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var edit in affected.Count > 0 ? affected : edits)
                    violationsByEdit[edit].Add(planViolation);
            }
        }

        foreach (var edit in edits)
        {
            var editViolations = violationsByEdit[edit]
                .DistinctBy(v => (v.TaskId, v.Code, v.Message))
                .ToList();

            // Update edit with validation results
            if (editViolations.Count == 0)
            {
                edit.ValidationStatus = "valid";
                edit.ValidationViolations = null;
                validCount++;
            }
            else
            {
                edit.ValidationStatus = "invalid";
                edit.ValidationViolations = JsonSerializer.Serialize(
                    editViolations.Select(v => new { v.TaskId, v.Code, v.Message }).ToList(),
                    JsonOptions);
                violations.AddRange(editViolations);
            }

            edit.UpdatedAtUtc = DateTimeOffset.UtcNow;
            dbContext.EditedTaskPlacements.Update(edit);
        }

        violations = violations.DistinctBy(v => (v.TaskId, v.Code, v.Message)).ToList();
        await dbContext.SaveChangesAsync(cancellationToken);

        return new EditValidationResult(
            draftId,
            violations.Count == 0,
            violations,
            validCount,
            edits.Count - validCount);
    }

    private static List<string> ExtractAssetIdsFromRequest(string requestPayload)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestPayload);
            if (doc.RootElement.TryGetProperty("assetIds", out var assetsElem))
            {
                return assetsElem.EnumerateArray()
                    .Select(a => a.GetString())
                    .Where(a => !string.IsNullOrEmpty(a))
                    .Cast<string>()
                    .ToList();
            }
        }
        catch
        {
            // Silently fail and return empty list
        }

        return [];
    }

    private static IReadOnlyList<string> DeserializeStringArray(string json)
    {
        try
        {
            var array = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
            return array ?? [];
        }
        catch
        {
            return [];
        }
    }
}
