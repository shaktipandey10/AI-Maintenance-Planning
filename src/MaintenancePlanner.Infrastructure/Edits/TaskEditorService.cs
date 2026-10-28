using System.Text.Json;
using MaintenancePlanner.Application.Edits;
using MaintenancePlanner.Application.History;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Edits;

public sealed class TaskEditorService(
    MaintenancePlannerDbContext dbContext,
    BusinessActor? actor = null) : ITaskEditorService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<TaskEditsResponse> SubmitEditsAsync(
        Guid draftId,
        TaskEditsSubmissionRequest request,
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

        if (string.IsNullOrEmpty(planningJob.ResultPayload))
            throw new InvalidOperationException($"Planning job {planningJob.Id} has no result");

        // The original placements are those of the approved alternative (its snapshot); drafts approved before
        // snapshots were stored were always approved from the first candidate
        var originalPlacements = ApprovedCandidateSnapshot.ReadPlacements(draft.ApprovedCandidatePayload) is { } approved
            ? approved.ToDictionary(
                placement => placement.TaskId,
                placement => (placement.Start, placement.End, placement.TechnicianIds),
                StringComparer.OrdinalIgnoreCase)
            : ExtractTaskPlacementsFromCandidate(planningJob.ResultPayload);

        var editsToCreate = new List<EditedTaskPlacement>();

        foreach (var edit in request.Edits)
        {
            // Find original placement
            if (!originalPlacements.TryGetValue(edit.TaskId, out var originalPlacement))
                throw new InvalidOperationException($"Task {edit.TaskId} not found in approved candidate");

            // Check for duplicate edit (same draft + task)
            var existingEdit = await dbContext.EditedTaskPlacements
                .FirstOrDefaultAsync(
                    e => e.DraftId == draftId && e.TaskId == edit.TaskId,
                    cancellationToken);

            if (existingEdit is not null)
            {
                // Idempotent: update existing edit
                existingEdit.NewStart = edit.NewStart;
                existingEdit.NewEnd = edit.NewEnd;
                existingEdit.NewTechnicianIds = JsonSerializer.Serialize(edit.NewTechnicianIds, JsonOptions);
                existingEdit.ValidationStatus = "pending";
                existingEdit.ValidationViolations = null;
                existingEdit.UpdatedAtUtc = DateTimeOffset.UtcNow;
                dbContext.EditedTaskPlacements.Update(existingEdit);
            }
            else
            {
                // Create new edit
                var newEdit = new EditedTaskPlacement
                {
                    Id = Guid.NewGuid(),
                    DraftId = draftId,
                    TaskId = edit.TaskId,
                    OriginalStart = originalPlacement.Start,
                    OriginalEnd = originalPlacement.End,
                    OriginalTechnicianIds = JsonSerializer.Serialize(originalPlacement.TechnicianIds, JsonOptions),
                    NewStart = edit.NewStart,
                    NewEnd = edit.NewEnd,
                    NewTechnicianIds = JsonSerializer.Serialize(edit.NewTechnicianIds, JsonOptions),
                    ValidationStatus = "pending",
                    ValidationViolations = null,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
                editsToCreate.Add(newEdit);
            }
        }

        if (editsToCreate.Count > 0)
        {
            await dbContext.EditedTaskPlacements.AddRangeAsync(editsToCreate, cancellationToken);
        }

        if (request.Edits.Count > 0)
        {
            dbContext.RecordBusinessAction(
                actor,
                BusinessActions.TaskEditSubmitted,
                draft.PlanningJobId,
                draftId,
                request.Edits.Count == 1 ? request.Edits[0].TaskId : null,
                string.Join("; ", request.Edits.Select(edit =>
                    $"{edit.TaskId}: {edit.NewStart:O} to {edit.NewEnd:O} by {string.Join(", ", edit.NewTechnicianIds)}")));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Return all current edits
        var allEdits = await dbContext.EditedTaskPlacements
            .Where(e => e.DraftId == draftId)
            .ToListAsync(cancellationToken);

        var editResponses = allEdits.Select(e => MapToResponse(e)).ToList();

        return new TaskEditsResponse(draftId, editResponses);
    }

    public async Task<IReadOnlyList<EditedTaskPlacementResponse>> GetEditsAsync(
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var edits = await dbContext.EditedTaskPlacements
            .Where(e => e.DraftId == draftId)
            .ToListAsync(cancellationToken);

        return edits.Select(e => MapToResponse(e)).ToList();
    }

    public async Task<bool> DeleteEditAsync(
        Guid draftId,
        string taskId,
        CancellationToken cancellationToken)
    {
        var edit = await dbContext.EditedTaskPlacements
            .FirstOrDefaultAsync(e => e.DraftId == draftId && e.TaskId == taskId, cancellationToken);

        if (edit is null)
            return false;

        dbContext.EditedTaskPlacements.Remove(edit);
        dbContext.RecordBusinessAction(actor, BusinessActions.TaskEditDeleted, null, draftId, edit.TaskId);
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> ClearEditsAsync(
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var edits = await dbContext.EditedTaskPlacements
            .Where(e => e.DraftId == draftId)
            .ToListAsync(cancellationToken);

        if (edits.Count == 0)
            return false;

        dbContext.EditedTaskPlacements.RemoveRange(edits);
        dbContext.RecordBusinessAction(
            actor,
            BusinessActions.TaskEditsCleared,
            null,
            draftId,
            details: $"Deleted edits for {string.Join(", ", edits.Select(edit => edit.TaskId))}.");
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    private Dictionary<string, (DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<string> TechnicianIds)>
        ExtractTaskPlacementsFromCandidate(string resultPayload)
    {
        var placements = new Dictionary<string, (DateTimeOffset, DateTimeOffset, IReadOnlyList<string>)>(
            StringComparer.OrdinalIgnoreCase);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(resultPayload);
        }
        catch (JsonException)
        {
            return placements;
        }

        using (document)
        {
            var rootElement = document.RootElement;

            if (!rootElement.TryGetProperty("result", out var resultElement))
                return placements;

            if (!resultElement.TryGetProperty("candidates", out var candidatesElement) ||
                candidatesElement.ValueKind != JsonValueKind.Array ||
                candidatesElement.GetArrayLength() == 0)
                return placements;

            var firstCandidate = candidatesElement[0];
            if (!firstCandidate.TryGetProperty("taskPlacements", out var placementsElement) ||
                placementsElement.ValueKind != JsonValueKind.Array)
                return placements;

            foreach (var placementElement in placementsElement.EnumerateArray())
            {
                if (placementElement.TryGetProperty("taskId", out var taskIdElement) &&
                    placementElement.TryGetProperty("start", out var startElement) &&
                    placementElement.TryGetProperty("end", out var endElement) &&
                    placementElement.TryGetProperty("technicianIds", out var technicianIdsElement))
                {
                    var taskId = taskIdElement.GetString();
                    if (string.IsNullOrEmpty(taskId))
                        continue;

                    var startStr = startElement.GetString();
                    var endStr = endElement.GetString();

                    if (!DateTimeOffset.TryParse(startStr, out var start) ||
                        !DateTimeOffset.TryParse(endStr, out var end))
                        continue;

                    var technicianIds = new List<string>();
                    if (technicianIdsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var techElement in technicianIdsElement.EnumerateArray())
                        {
                            var techId = techElement.GetString();
                            if (!string.IsNullOrEmpty(techId))
                                technicianIds.Add(techId);
                        }
                    }

                    placements[taskId] = (start, end, technicianIds);
                }
            }
        }

        return placements;
    }

    private static EditedTaskPlacementResponse MapToResponse(EditedTaskPlacement edit)
    {
        var originalTechs = DeserializeStringArray(edit.OriginalTechnicianIds);
        var newTechs = DeserializeStringArray(edit.NewTechnicianIds);

        return new EditedTaskPlacementResponse(
            TaskId: edit.TaskId,
            OriginalStart: edit.OriginalStart,
            OriginalEnd: edit.OriginalEnd,
            OriginalTechnicianIds: originalTechs,
            NewStart: edit.NewStart,
            NewEnd: edit.NewEnd,
            NewTechnicianIds: newTechs,
            ValidationStatus: edit.ValidationStatus);
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
