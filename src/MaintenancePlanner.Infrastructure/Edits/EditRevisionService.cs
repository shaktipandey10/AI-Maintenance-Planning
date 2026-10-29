using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MaintenancePlanner.Application.Edits;
using MaintenancePlanner.Application.History;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Edits;

/// <summary>
/// FR05: builds a reviewable revision from an approved draft's task edits. Completed tasks are left out (they are not
/// scheduled again) and tasks in progress keep their approved placement, as in replanning (FR08); a locked task must
/// keep its locked technician. The revision is a succeeded replacement planning job, so it is reviewed and approved
/// through the existing approval, which revalidates it, supersedes the draft and swaps the reservations.
/// </summary>
public sealed class EditRevisionService(
    MaintenancePlannerDbContext dbContext,
    IEditValidationService editValidation,
    EditedPlanValidator planValidator,
    CandidateCostCalculator costCalculator,
    IMaintenancePlannerQueries queries,
    BusinessActor? actor = null) : IEditRevisionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EditRevisionResult> CreateRevisionAsync(
        Guid draftId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts.AsNoTracking()
            .Include(d => d.PlanningJob)
            .FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);
        if (draft?.PlanningJob is null)
            return Failure(EditRevisionResult.DraftNotFound, "Draft not found.");

        // A repeated request returns the revision it already created, even once that revision has been approved
        var fingerprint = Fingerprint(draftId);
        var existing = await ExistingRevisionAsync(operationId, draft, fingerprint, cancellationToken);
        if (existing is not null)
            return existing;

        if (draft.Status != DraftStatus.Approved)
            return Failure(EditRevisionResult.DraftNotApproved, $"Draft is {draft.Status}; only an approved draft can be revised.");

        var edits = await dbContext.EditedTaskPlacements.AsNoTracking()
            .Where(e => e.DraftId == draftId)
            .ToListAsync(cancellationToken);
        if (edits.Count == 0)
            return Failure(EditRevisionResult.NoEdits, "The draft has no task edits to revise.");

        var approvedPlacements = ApprovedCandidateSnapshot.ReadPlacements(draft.ApprovedCandidatePayload);
        if (approvedPlacements is null || JsonNode.Parse(draft.ApprovedCandidatePayload!) is not JsonObject snapshot)
            return Failure(EditRevisionResult.SnapshotUnavailable, "The approved plan of this draft cannot be read.");

        // Edit-by-edit checks first (their result is also saved on each edit), then the edits' effect on the plan; edit
        // validation reports the same plan violations, so duplicates are removed
        var violations = new List<ValidationViolation>((await editValidation.ValidateEditsAsync(draftId, cancellationToken)).Violations);
        var plan = (await planValidator.CheckAsync(draft, draft.PlanningJob.RequestPayload, edits, cancellationToken))!;
        violations.AddRange(plan.Violations);
        var revised = plan.RevisedPlacements;

        violations = violations.DistinctBy(v => (v.TaskId, v.Code, v.Message)).ToList();
        if (violations.Count > 0)
        {
            return new EditRevisionResult(false, null, EditRevisionResult.EditsInvalid,
                "The edited plan violates current constraints; nothing was created.", violations);
        }

        var technicians = (await queries.GetTechniciansAsync(cancellationToken))
            .ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var metrics = costCalculator.CalculateMetrics(new PlanningCandidate(revised, string.Empty, []), technicians);
        var editedTaskIds = edits.Select(edit => edit.TaskId).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidate = ReviseSnapshot(snapshot, revised, metrics, draftId, editedTaskIds);
        var job = new PlanningJob
        {
            Id = Guid.NewGuid(),
            OperationId = operationId,
            OriginalJobId = draft.PlanningJobId,
            RequestPayload = draft.PlanningJob.RequestPayload,
            RequestFingerprint = fingerprint,
            Status = PlanningJobStatus.Succeeded,
            CurrentStep = "Completed",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ResultPayload = RevisedResult(draft.PlanningJob.ResultPayload, candidate, draftId)
        };
        await dbContext.PlanningJobs.AddAsync(job, cancellationToken);
        var history = dbContext.RecordBusinessAction(
            actor,
            BusinessActions.RevisionCreatedFromEdits,
            job.Id,
            draftId,
            details: $"Revision job {job.Id} of approved job {draft.PlanningJobId} created from edits to {string.Join(", ", editedTaskIds)}.");
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            // A concurrent request with the same operation ID created the revision first
            dbContext.Entry(job).State = EntityState.Detached;
            dbContext.Entry(history).State = EntityState.Detached;
            return await ExistingRevisionAsync(operationId, draft, fingerprint, cancellationToken)
                   ?? throw new InvalidOperationException("The revision could not be saved.", exception);
        }

        return new EditRevisionResult(true, job.Id);
    }

    private async Task<EditRevisionResult?> ExistingRevisionAsync(
        Guid operationId,
        Draft draft,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.PlanningJobs.AsNoTracking()
            .FirstOrDefaultAsync(job => job.OperationId == operationId, cancellationToken);
        if (existing is null)
            return null;

        return existing.OriginalJobId == draft.PlanningJobId && existing.RequestFingerprint == fingerprint
            ? new EditRevisionResult(true, existing.Id)
            : Failure(EditRevisionResult.OperationIdReused, "This Idempotency-Key has already been used for a different request.");
    }

    // The approved candidate with the revised placements and recalculated totals; every other field (evidence,
    // deferrals, requested tasks) is kept from the approved plan
    private static string ReviseSnapshot(
        JsonObject snapshot,
        IReadOnlyList<PlanningTaskPlacement> placements,
        CandidateCostMetrics metrics,
        Guid draftId,
        IReadOnlyList<string> editedTaskIds)
    {
        var placedTaskIds = placements.Select(p => p.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = (JsonObject)snapshot.DeepClone();
        candidate["taskPlacements"] = JsonSerializer.SerializeToNode(
            placements.Select(p => new { taskId = p.TaskId, start = p.Start, end = p.End, technicianIds = p.TechnicianIds }),
            JsonOptions);
        foreach (var name in new[] { "requestedTaskIds", "dependencyTaskIds" })
        {
            if (candidate[name] is JsonArray ids)
            {
                candidate[name] = new JsonArray(ids
                    .Where(id => id?.GetValueKind() == JsonValueKind.String && placedTaskIds.Contains(id.GetValue<string>()))
                    .Select(id => (JsonNode)JsonValue.Create(id!.GetValue<string>())!)
                    .ToArray());
            }
        }

        candidate["rationale"] = $"Revision of approved draft {draftId} with user edits to {string.Join(", ", editedTaskIds)}.";
        candidate["totalLabourCostAmount"] = metrics.TotalLabourCostAmount;
        candidate["totalLabourCostCurrency"] = metrics.TotalLabourCostCurrency;
        candidate["completionTimeMinutes"] = metrics.CompletionTimeMinutes;
        candidate["isLowestCostCandidate"] = metrics.CostWarning is null;
        candidate["costComparabilityWarning"] = metrics.CostWarning;
        return candidate.ToJsonString();
    }

    // The original job's result with the revised plan as its only candidate, so interpretation and evidence are kept
    private static string RevisedResult(string? originalResult, string candidate, Guid draftId)
    {
        var assumption = $"Revision of approved draft {draftId} built from user task edits; validated against current data.";
        JsonObject? envelope = null;
        try
        {
            envelope = string.IsNullOrWhiteSpace(originalResult) ? null : JsonNode.Parse(originalResult) as JsonObject;
        }
        catch (JsonException)
        {
            // A new envelope is built below
        }

        if (envelope?["result"] is not JsonObject result)
        {
            result = new JsonObject { ["assumptions"] = new JsonArray(), ["evidenceReferences"] = new JsonArray() };
            envelope = new JsonObject { ["result"] = result };
        }

        envelope["outcome"] = "CandidatesGenerated";
        result["candidates"] = new JsonArray(JsonNode.Parse(candidate));
        if (result["assumptions"] is JsonArray assumptions)
            assumptions.Add(assumption);
        else
            result["assumptions"] = new JsonArray(assumption);
        return envelope.ToJsonString();
    }

    private static string Fingerprint(Guid draftId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"edit-revision:{draftId:N}")));

    private static EditRevisionResult Failure(string code, string reason) => new(false, null, code, reason);
}
