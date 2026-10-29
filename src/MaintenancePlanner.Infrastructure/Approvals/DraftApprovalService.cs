using System.Text.Json;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.History;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Approvals;

public sealed class DraftApprovalService(
    MaintenancePlannerDbContext dbContext,
    PlanningCandidateValidator candidateValidator,
    BusinessActor? actor = null) : IDraftApprovalService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    // The validator reads stock from SparePart.ReservedQuantity, which still contains the reservations of the
    // approved revision being replaced. Stock is instead checked authoritatively inside the approval transaction
    // (see ReserveParts), where those releasable reservations are excluded.
    private const string PartUnavailableViolationCode = "PART_UNAVAILABLE";

    public Task<DraftApprovalResult> ApproveDraftAsync(
        Guid planningJobId,
        CancellationToken cancellationToken) =>
        ApproveDraftAsync(planningJobId, candidateIndex: null, cancellationToken);

    public Task<DraftApprovalResult> ApproveDraftAsync(
        Guid planningJobId,
        int? candidateIndex,
        CancellationToken cancellationToken) =>
        ApproveAsync(planningJobId, candidateIndex, requireReplacement: false, cancellationToken);

    public Task<DraftApprovalResult> ApproveDraftReplacementAsync(
        Guid planningJobId,
        CancellationToken cancellationToken) =>
        ApproveDraftReplacementAsync(planningJobId, candidateIndex: null, cancellationToken);

    public Task<DraftApprovalResult> ApproveDraftReplacementAsync(
        Guid planningJobId,
        int? candidateIndex,
        CancellationToken cancellationToken) =>
        ApproveAsync(planningJobId, candidateIndex, requireReplacement: true, cancellationToken);

    private async Task<DraftApprovalResult> ApproveAsync(
        Guid planningJobId,
        int? candidateIndex,
        bool requireReplacement,
        CancellationToken cancellationToken)
    {
        // Approval is its own unit of work; entities tracked by earlier operations in this scope may hold stale
        // stock or draft state, so they are discarded and every decision below reads committed rows.
        if (dbContext.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Approval cannot run while the DbContext has unsaved changes.");
        dbContext.ChangeTracker.Clear();

        // SQLite transactions begin IMMEDIATE, so concurrent approvals are serialized here: every read below
        // (existing drafts, confirmed reservations, stock) sees the committed state of earlier approvals.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await ApproveWithinTransactionAsync(
                planningJobId,
                candidateIndex,
                requireReplacement,
                cancellationToken);

            if (result.Succeeded)
                await transaction.CommitAsync(cancellationToken);
            else
                await transaction.RollbackAsync(cancellationToken);

            return result;
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();

            var reloadedDraft = await dbContext.Drafts
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.PlanningJobId == planningJobId, cancellationToken);
            if (reloadedDraft is not null && reloadedDraft.Status == DraftStatus.Approved)
                return new DraftApprovalResult(true, reloadedDraft.Id);

            return Conflict("Draft", "CONCURRENT_DRAFT_CREATION", planningJobId.ToString(),
                "Concurrent draft creation detected");
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return Conflict("Draft", "CONCURRENT_MODIFICATION", planningJobId.ToString(),
                "Concurrent modification detected");
        }
    }

    private async Task<DraftApprovalResult> ApproveWithinTransactionAsync(
        Guid planningJobId,
        int? candidateIndex,
        bool requireReplacement,
        CancellationToken cancellationToken)
    {
        var planningJob = await dbContext.PlanningJobs.FirstOrDefaultAsync(
            j => j.Id == planningJobId,
            cancellationToken);
        if (planningJob is null)
            return Conflict("PlanningJob", "PLANNING_JOB_NOT_FOUND", planningJobId.ToString(),
                "Planning job not found");

        if (requireReplacement && planningJob.OriginalJobId is null)
            return Conflict("PlanningJob", "NOT_A_REPLACEMENT_JOB", planningJobId.ToString(),
                "Planning job is not a replacement of an approved plan");

        if (planningJob.Status != PlanningJobStatus.Succeeded || string.IsNullOrEmpty(planningJob.ResultPayload))
            return Conflict("PlanningJob", "PLANNING_JOB_NO_RESULT", planningJobId.ToString(),
                "Planning job has no result");

        if (!TryReadCandidates(planningJob.ResultPayload, out var outcome, out var candidates))
            return Conflict("PlanningJob", "PLANNING_RESULT_UNREADABLE", planningJobId.ToString(),
                "Planning job result could not be read");

        if (outcome != "CandidatesGenerated")
            return Conflict("PlanningJob", "PLANNING_JOB_NOT_APPROVABLE", planningJobId.ToString(),
                $"Planning job outcome is '{outcome}'; only generated candidates can be approved");

        var existingDraft = await dbContext.Drafts.FirstOrDefaultAsync(
            d => d.PlanningJobId == planningJobId,
            cancellationToken);
        if (existingDraft is not null)
            return ExistingDraftResult(existingDraft, candidateIndex, candidates);

        if (candidates.Count == 0)
            return Conflict("PlanningJob", "PLANNING_JOB_NO_CANDIDATES", planningJobId.ToString(),
                "Planning job has no candidates");

        if (candidateIndex is null && candidates.Count > 1)
            return Conflict("Candidate", "CANDIDATE_SELECTION_REQUIRED", planningJobId.ToString(),
                $"Planning job has {candidates.Count} candidates; select one by candidateIndex");

        var selectedIndex = candidateIndex ?? 0;
        if (selectedIndex < 0 || selectedIndex >= candidates.Count)
            return Conflict("Candidate", "CANDIDATE_NOT_FOUND", selectedIndex.ToString(),
                $"Candidate index {selectedIndex} does not exist; the job has {candidates.Count} candidates");

        var selectedCandidateJson = candidates[selectedIndex];
        PlanningJobRequestPayload? request;
        IReadOnlyList<CandidatePlacementPayload>? placements;
        try
        {
            request = JsonSerializer.Deserialize<PlanningJobRequestPayload>(planningJob.RequestPayload, JsonOptions);
            placements = JsonSerializer.Deserialize<CandidatePayload>(selectedCandidateJson, JsonOptions)?.TaskPlacements;
        }
        catch (JsonException exception)
        {
            return Conflict("Candidate", "CANDIDATE_UNREADABLE", selectedIndex.ToString(),
                $"The planning request or selected candidate could not be read: {exception.Message}");
        }

        var assetIds = request?.AssetIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToArray();
        if (assetIds is null || assetIds.Length == 0 || string.IsNullOrWhiteSpace(request!.WindowId))
            return Conflict("PlanningJob", "PLANNING_REQUEST_INCOMPLETE", planningJobId.ToString(),
                "The planning request has no asset scope or maintenance window to revalidate against");

        if (placements is null || placements.Count == 0)
            return Conflict("Candidate", "CANDIDATE_HAS_NO_TASKS", selectedIndex.ToString(),
                "The selected candidate has no task placements to approve");

        Draft? replacedDraft = null;
        if (planningJob.OriginalJobId is { } originalJobId)
        {
            replacedDraft = await dbContext.Drafts
                .Include(d => d.Reservations)
                .FirstOrDefaultAsync(
                    d => d.PlanningJobId == originalJobId && d.Status == DraftStatus.Approved,
                    cancellationToken);
            if (replacedDraft is null)
                return Conflict("PlanningJob", "NO_APPROVED_ORIGINAL_DRAFT", originalJobId.ToString(),
                    "Original job has no approved draft; cannot create replacement");
        }

        // FR08: a replacement must not schedule again work completed on the draft it replaces. Read inside this
        // transaction, so a task completed after V2 was planned rejects the now-stale V2.
        IReadOnlyList<string> completedTaskIds = replacedDraft is null
            ? []
            : await dbContext.DraftTaskProgress
                .Where(progress => progress.DraftId == replacedDraft.Id && progress.Status == TaskProgressStatus.Completed)
                .Select(progress => progress.TaskId)
                .ToListAsync(cancellationToken);

        var validation = await candidateValidator.ValidateAsync(
            new PlanningCandidateValidationRequest(
                assetIds,
                request.WindowId,
                new PlanningCandidate(
                    placements.Select(p => new PlanningTaskPlacement(p.TaskId, p.Start, p.End, p.TechnicianIds)).ToArray(),
                    Rationale: string.Empty,
                    EvidenceReferences: []))
            {
                CompletedTaskIds = completedTaskIds
            },
            cancellationToken);
        var violations = validation.Violations
            .Where(v => v.Code != PartUnavailableViolationCode)
            .ToArray();
        if (violations.Length > 0)
            return Conflict("Candidate", "CANDIDATE_NO_LONGER_VALID", violations[0].Code,
                "The selected candidate fails current validation: " +
                string.Join("; ", violations.Select(v => $"{v.Code}: {v.Message}")));

        // FR08: work in progress on the draft being replaced must continue in the replacement at its approved
        // placement. Read inside this transaction, so a task started after V2 was planned rejects the stale V2.
        IReadOnlyList<DraftTaskProgress> inProgressTasks = replacedDraft is null
            ? []
            : await dbContext.DraftTaskProgress
                .AsNoTracking()
                .Where(progress => progress.DraftId == replacedDraft.Id && progress.Status == TaskProgressStatus.InProgress)
                .ToListAsync(cancellationToken);
        var inProgressConflict = FindInProgressTaskNotPreserved(replacedDraft, inProgressTasks, placements);
        if (inProgressConflict is not null)
            return inProgressConflict;

        var timeConflict = await FindTechnicianTimeConflictAsync(placements, replacedDraft?.Id, cancellationToken);
        if (timeConflict is not null)
            return timeConflict;

        var draft = new Draft
        {
            Id = Guid.NewGuid(),
            PlanningJobId = planningJobId,
            Status = DraftStatus.Approved,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
            ApprovedCandidatePayload = selectedCandidateJson
        };

        var stockConflict = await ReservePartsAsync(draft, placements, replacedDraft, cancellationToken);
        if (stockConflict is not null)
            return stockConflict;

        if (replacedDraft is not null)
            replacedDraft.Status = DraftStatus.Superseded;

        await dbContext.Drafts.AddAsync(draft, cancellationToken);
        // The in-progress work continues on the replacement with its status carried over
        await dbContext.DraftTaskProgress.AddRangeAsync(
            inProgressTasks.Select(progress => new DraftTaskProgress
            {
                Id = Guid.NewGuid(),
                DraftId = draft.Id,
                TaskId = progress.TaskId,
                Status = TaskProgressStatus.InProgress,
                UpdatedAtUtc = progress.UpdatedAtUtc
            }),
            cancellationToken);
        await dbContext.LockedTaskAssignments.AddRangeAsync(
            placements
                .Where(p => p.TechnicianIds.Count > 0)
                .Select(p => new LockedTaskAssignment
                {
                    Id = Guid.NewGuid(),
                    DraftId = draft.Id,
                    TaskId = p.TaskId,
                    LockedTechnicianId = p.TechnicianIds[0],
                    CreatedAtUtc = DateTimeOffset.UtcNow
                }),
            cancellationToken);

        // FR09: recorded only when the approval is applied; an idempotent repeat returns earlier and records nothing
        dbContext.RecordBusinessAction(
            actor,
            BusinessActions.Approved,
            planningJobId,
            draft.Id,
            details: $"Approved alternative {selectedIndex + 1} of {candidates.Count}" +
                     (replacedDraft is null
                         ? "."
                         : $"; superseded draft {replacedDraft.Id} of job {replacedDraft.PlanningJobId} and released its unused reservations."));

        await dbContext.SaveChangesAsync(cancellationToken);
        return new DraftApprovalResult(true, draft.Id);
    }

    /// <summary>
    /// Rejects the approval if any technician in the new placements is already committed, in another currently
    /// approved draft, to an overlapping interval (half-open: start &lt; otherEnd and otherStart &lt; end, compared
    /// as instants). The draft being replaced is excluded so a revision can reuse its own time. An approved draft
    /// whose snapshot cannot be read is treated as a conflict, because its commitments are unknown.
    /// </summary>
    private async Task<DraftApprovalResult?> FindTechnicianTimeConflictAsync(
        IReadOnlyList<CandidatePlacementPayload> placements,
        Guid? replacedDraftId,
        CancellationToken cancellationToken)
    {
        var committedDrafts = await dbContext.Drafts
            .AsNoTracking()
            .Where(d => d.Status == DraftStatus.Approved && d.Id != replacedDraftId)
            .Select(d => new { d.Id, d.ApprovedCandidatePayload })
            .ToListAsync(cancellationToken);

        foreach (var committedDraft in committedDrafts.OrderBy(d => d.Id))
        {
            if (!TryReadCommittedPlacements(committedDraft.ApprovedCandidatePayload, out var committedPlacements))
                return Conflict("Draft", "APPROVED_SNAPSHOT_UNAVAILABLE", committedDraft.Id.ToString(),
                    $"Approved draft {committedDraft.Id} has no readable approved-plan snapshot, so its technician " +
                    "commitments cannot be checked; resolve that draft before approving overlapping work");

            foreach (var placement in placements)
            foreach (var technicianId in placement.TechnicianIds)
            {
                var overlapping = committedPlacements.FirstOrDefault(committed =>
                    committed.TechnicianIds.Contains(technicianId, StringComparer.OrdinalIgnoreCase) &&
                    placement.Start < committed.End &&
                    committed.Start < placement.End);
                if (overlapping is not null)
                    return Conflict("Technician", "TECHNICIAN_TIME_CONFLICT", technicianId,
                        $"Technician {technicianId} is assigned to task {placement.TaskId} " +
                        $"[{placement.Start:O}, {placement.End:O}), which overlaps task {overlapping.TaskId} " +
                        $"[{overlapping.Start:O}, {overlapping.End:O}) in approved draft {committedDraft.Id}");
            }
        }

        return null;
    }

    /// <summary>
    /// FR08: each task in progress on the draft being replaced must appear in the replacement exactly once, at its
    /// approved placement (same start, end and crew); otherwise the replacement is stale, or would drop or redo work
    /// that has started.
    /// </summary>
    private static DraftApprovalResult? FindInProgressTaskNotPreserved(
        Draft? replacedDraft,
        IReadOnlyList<DraftTaskProgress> inProgressTasks,
        IReadOnlyList<CandidatePlacementPayload> placements)
    {
        if (replacedDraft is null || inProgressTasks.Count == 0)
            return null;

        TryReadCommittedPlacements(replacedDraft.ApprovedCandidatePayload, out var approvedPlacements);
        foreach (var progress in inProgressTasks)
        {
            var approved = approvedPlacements.FirstOrDefault(placement =>
                string.Equals(placement.TaskId, progress.TaskId, StringComparison.OrdinalIgnoreCase));
            var replacement = placements
                .Where(placement => string.Equals(placement.TaskId, progress.TaskId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (approved is null ||
                replacement.Count != 1 ||
                replacement[0].Start != approved.Start ||
                replacement[0].End != approved.End ||
                !replacement[0].TechnicianIds.SequenceEqual(approved.TechnicianIds, StringComparer.OrdinalIgnoreCase))
            {
                return Conflict("Candidate", "CANDIDATE_NO_LONGER_VALID", "IN_PROGRESS_TASK_NOT_PRESERVED",
                    $"Task {progress.TaskId} is in progress on the approved plan being replaced; the replacement must " +
                    "keep it at its approved placement (same times and crew)");
            }
        }

        return null;
    }

    private static bool TryReadCommittedPlacements(
        string? snapshot,
        out IReadOnlyList<CandidatePlacementPayload> placements)
    {
        placements = [];
        if (string.IsNullOrWhiteSpace(snapshot))
            return false;

        try
        {
            var read = JsonSerializer.Deserialize<CandidatePayload>(snapshot, JsonOptions)?.TaskPlacements;
            if (read is null || read.Any(p => p is null || p.TechnicianIds is null))
                return false;

            placements = read;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds the new draft's reservations, cancels the replaced draft's unused reservations and recomputes
    /// SparePart.ReservedQuantity as the total of confirmed reservations. Completed (consumed) reservations of
    /// the replaced draft are not released. Returns a conflict without changing anything if stock is short.
    /// </summary>
    private async Task<DraftApprovalResult?> ReservePartsAsync(
        Draft draft,
        IReadOnlyList<CandidatePlacementPayload> placements,
        Draft? replacedDraft,
        CancellationToken cancellationToken)
    {
        var placedTaskIds = placements.Select(p => p.TaskId).ToArray();
        var placedTasks = await dbContext.MaintenanceTaskDefinitions
            .AsNoTracking()
            .Where(t => placedTaskIds.Contains(t.Id))
            .ToListAsync(cancellationToken);
        var requiredParts = placedTasks
            .Where(t => t.RequiredPartId is not null && t.RequiredPartQuantity > 0)
            .GroupBy(t => t.RequiredPartId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.RequiredPartQuantity), StringComparer.OrdinalIgnoreCase);

        var releasedReservations = replacedDraft?.Reservations
            .Where(r => r.Status == ReservationStatus.Confirmed && r.CompletedAtUtc is null)
            .ToList() ?? [];
        var releasedReservationIds = releasedReservations.Select(r => r.Id).ToHashSet();

        var affectedPartIds = requiredParts.Keys
            .Concat(releasedReservations.Select(r => r.SparePartId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var retainedConfirmed = (await dbContext.Reservations
                .AsNoTracking()
                .Where(r => affectedPartIds.Contains(r.SparePartId) && r.Status == ReservationStatus.Confirmed)
                .Select(r => new { r.Id, r.SparePartId, r.QuantityReserved })
                .ToListAsync(cancellationToken))
            .Where(r => !releasedReservationIds.Contains(r.Id))
            .GroupBy(r => r.SparePartId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.QuantityReserved), StringComparer.OrdinalIgnoreCase);

        var parts = new List<SparePart>(affectedPartIds.Length);
        foreach (var partId in affectedPartIds)
        {
            var part = await dbContext.SpareParts.FirstOrDefaultAsync(p => p.Id == partId, cancellationToken);
            if (part is null)
                return Conflict("SparePart", "SPARE_PART_NOT_FOUND", partId, $"Spare part {partId} not found");

            var required = requiredParts.GetValueOrDefault(partId);
            if (retainedConfirmed.GetValueOrDefault(partId) + required > part.OnHandQuantity)
                return Conflict("SparePart", "INSUFFICIENT_INVENTORY", partId, $"Insufficient inventory for {partId}");

            parts.Add(part);
        }

        foreach (var reservation in releasedReservations)
            reservation.Status = ReservationStatus.Cancelled;

        foreach (var (partId, quantity) in requiredParts)
        {
            draft.Reservations.Add(new Reservation
            {
                Id = Guid.NewGuid(),
                DraftId = draft.Id,
                SparePartId = partId,
                QuantityReserved = quantity,
                Status = ReservationStatus.Confirmed,
                ConfirmedAtUtc = DateTimeOffset.UtcNow
            });
        }

        foreach (var part in parts)
            part.ReservedQuantity = retainedConfirmed.GetValueOrDefault(part.Id) + requiredParts.GetValueOrDefault(part.Id);

        return null;
    }

    private static DraftApprovalResult ExistingDraftResult(
        Draft existingDraft,
        int? candidateIndex,
        IReadOnlyList<string> candidates)
    {
        switch (existingDraft.Status)
        {
            case DraftStatus.Approved:
                // A repeated request is idempotent only if it asks for the candidate that was approved.
                if (candidateIndex is { } index &&
                    (index < 0 || index >= candidates.Count || candidates[index] != existingDraft.ApprovedCandidatePayload))
                {
                    return Conflict("Candidate", "CANDIDATE_SELECTION_MISMATCH", existingDraft.Id.ToString(),
                        "This planning job was already approved with a different candidate");
                }

                return new DraftApprovalResult(true, existingDraft.Id);
            case DraftStatus.Cancelled:
                return Conflict("Draft", "DRAFT_CANCELLED", existingDraft.Id.ToString(),
                    "Draft was cancelled and cannot be re-approved");
            case DraftStatus.Superseded:
                return Conflict("Draft", "DRAFT_SUPERSEDED", existingDraft.Id.ToString(),
                    "Draft was superseded by a newer approved revision and cannot be re-approved");
            default:
                return Conflict("Draft", "INVALID_DRAFT_STATE", existingDraft.Id.ToString(),
                    $"Draft is in state {existingDraft.Status} and cannot be approved");
        }
    }

    public async Task<CancellationResult> CancelDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken)
    {
        // As in approval, decide from committed rows only: the transaction begins (taking SQLite's write lock)
        // before the draft is read, so an approval that superseded this draft cannot commit between the status
        // check and the writes, and no stale tracked draft, reservation or part state is reused.
        if (dbContext.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Cancellation cannot run while the DbContext has unsaved changes.");
        dbContext.ChangeTracker.Clear();

        using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var draft = await dbContext.Drafts
                .Include(d => d.Reservations)
                .FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);

            if (draft is null)
                return new CancellationResult(false, "Draft not found");

            if (draft.Status == DraftStatus.Cancelled)
                return new CancellationResult(true); // Idempotent: already cancelled

            if (draft.Status != DraftStatus.Approved)
                return new CancellationResult(false,
                    "Draft is not in approved state",
                    ConflictData: new ApprovalConflictData("Draft", "INVALID_DRAFT_STATE", draftId.ToString()));

            // FR08: release only unused reservations. Completed (consumed) reservations stay Confirmed with their
            // CompletedAtUtc and keep counting towards ReservedQuantity, as in replacement (ReservePartsAsync).
            var confirmedReservations = draft.Reservations
                .Where(r => r.Status == ReservationStatus.Confirmed && r.CompletedAtUtc is null)
                .ToList();

            // Release inventory by updating SparePart.ReservedQuantity
            var partsByReservation = confirmedReservations
                .GroupBy(r => r.SparePartId)
                .Select(g => new { PartId = g.Key, TotalReserved = g.Sum(r => r.QuantityReserved) })
                .ToList();

            foreach (var partGroup in partsByReservation)
            {
                var part = await dbContext.SpareParts.FirstOrDefaultAsync(
                    p => p.Id == partGroup.PartId,
                    cancellationToken);

                if (part is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new CancellationResult(false,
                        $"Spare part {partGroup.PartId} not found",
                        ConflictData: new ApprovalConflictData("SparePart", "SPARE_PART_NOT_FOUND", partGroup.PartId));
                }

                // Reduce reserved quantity
                part.ReservedQuantity = Math.Max(0, part.ReservedQuantity - partGroup.TotalReserved);
            }

            // Mark all reservations as cancelled
            foreach (var reservation in confirmedReservations)
            {
                reservation.Status = ReservationStatus.Cancelled;
            }

            // Mark draft as cancelled
            draft.Status = DraftStatus.Cancelled;

            dbContext.RecordBusinessAction(
                actor,
                BusinessActions.Cancelled,
                draft.PlanningJobId,
                draft.Id,
                details: $"Released {confirmedReservations.Count} unused reservation(s).");

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new CancellationResult(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CancellationResult(false,
                "Concurrent modification detected",
                ConflictData: new ApprovalConflictData("Draft", "CONCURRENT_MODIFICATION", draftId.ToString()));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ApprovedDraftInfo?> GetApprovedDraftForJobAsync(
        Guid planningJobId,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts
            .AsNoTracking()
            .Where(d => d.PlanningJobId == planningJobId && d.Status == DraftStatus.Approved)
            .Select(d => new ApprovedDraftInfo(d.Id, d.PlanningJobId, d.Status.ToString()))
            .FirstOrDefaultAsync(cancellationToken);

        return draft;
    }

    /// <summary>Reads the outcome and the raw JSON of each candidate; false when the payload is not readable JSON.</summary>
    private static bool TryReadCandidates(
        string resultPayload,
        out string? outcome,
        out IReadOnlyList<string> candidates)
    {
        outcome = null;
        candidates = [];
        try
        {
            using var document = JsonDocument.Parse(resultPayload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            outcome = root.TryGetProperty("outcome", out var outcomeElement) &&
                      outcomeElement.ValueKind == JsonValueKind.String
                ? outcomeElement.GetString()
                : null;
            if (root.TryGetProperty("result", out var resultElement) &&
                resultElement.ValueKind == JsonValueKind.Object &&
                resultElement.TryGetProperty("candidates", out var candidatesElement) &&
                candidatesElement.ValueKind == JsonValueKind.Array)
            {
                candidates = candidatesElement.EnumerateArray().Select(c => c.GetRawText()).ToArray();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException?.GetType().Name == "SqliteException" &&
        exception.InnerException.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);

    private static DraftApprovalResult Conflict(string resourceType, string reason, string? resourceId, string message) =>
        new(false, FailureReason: message, ConflictData: new ApprovalConflictData(resourceType, reason, resourceId));

    private sealed record CandidatePayload(IReadOnlyList<CandidatePlacementPayload>? TaskPlacements);

    private sealed record CandidatePlacementPayload(
        string TaskId,
        DateTimeOffset Start,
        DateTimeOffset End,
        IReadOnlyList<string> TechnicianIds);
}
