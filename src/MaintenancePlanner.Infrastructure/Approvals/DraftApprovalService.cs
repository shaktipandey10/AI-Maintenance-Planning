using System.Text.Json;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Approvals;

public sealed class DraftApprovalService(
    MaintenancePlannerDbContext dbContext) : IDraftApprovalService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<DraftApprovalResult> ApproveDraftAsync(
        Guid planningJobId,
        CancellationToken cancellationToken)
    {
        var planningJob = await dbContext.PlanningJobs.FirstOrDefaultAsync(
            j => j.Id == planningJobId,
            cancellationToken);

        if (planningJob is null)
            return new DraftApprovalResult(false,
                FailureReason: "Planning job not found",
                ConflictData: new ApprovalConflictData("PlanningJob", "PLANNING_JOB_NOT_FOUND", planningJobId.ToString()));

        if (string.IsNullOrEmpty(planningJob.ResultPayload))
            return new DraftApprovalResult(false,
                FailureReason: "Planning job has no result",
                ConflictData: new ApprovalConflictData("PlanningJob", "PLANNING_JOB_NO_RESULT", planningJobId.ToString()));

        Guid? v1DraftIdToCleanup = null;
        if (planningJob.OriginalJobId.HasValue)
        {
            var v1Draft = await GetApprovedDraftForJobAsync(planningJob.OriginalJobId.Value, cancellationToken);
            if (v1Draft is not null)
            {
                v1DraftIdToCleanup = v1Draft.Id;
            }
        }

        var existingDraft = await dbContext.Drafts.FirstOrDefaultAsync(
            d => d.PlanningJobId == planningJobId,
            cancellationToken);

        if (existingDraft is not null)
        {
            if (existingDraft.Status == DraftStatus.Approved)
                return new DraftApprovalResult(true, existingDraft.Id);
            if (existingDraft.Status == DraftStatus.Cancelled)
                return new DraftApprovalResult(false,
                    FailureReason: "Draft was cancelled and cannot be re-approved",
                    ConflictData: new ApprovalConflictData("Draft", "DRAFT_CANCELLED", existingDraft.Id.ToString()));
        }

        var requiredParts = await ExtractRequiredPartsAsync(planningJob, cancellationToken);
        if (requiredParts.Count == 0)
            return new DraftApprovalResult(false,
                FailureReason: "Planning job has no required parts",
                ConflictData: new ApprovalConflictData("PlanningJob", "PLANNING_JOB_NO_PARTS", planningJobId.ToString()));

        using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var draft = new Draft
            {
                Id = Guid.NewGuid(),
                PlanningJobId = planningJobId,
                Status = DraftStatus.Pending
            };
            await dbContext.Drafts.AddAsync(draft, cancellationToken);

            var reservations = new List<Reservation>();
            foreach (var (partId, quantity) in requiredParts)
            {
                var reservation = new Reservation
                {
                    Id = Guid.NewGuid(),
                    DraftId = draft.Id,
                    SparePartId = partId,
                    QuantityReserved = quantity,
                    Status = ReservationStatus.Confirmed,
                    ConfirmedAtUtc = DateTimeOffset.UtcNow
                };
                reservations.Add(reservation);
                await dbContext.Reservations.AddAsync(reservation, cancellationToken);
            }

            var allReservedForParts = await dbContext.Reservations
                .Where(r => requiredParts.Keys.Contains(r.SparePartId) && r.Status == ReservationStatus.Confirmed)
                .GroupBy(r => r.SparePartId)
                .Select(g => new { PartId = g.Key, TotalReserved = g.Sum(r => r.QuantityReserved) })
                .ToDictionaryAsync(g => g.PartId, g => g.TotalReserved, cancellationToken);

            foreach (var (partId, _) in requiredParts)
            {
                var part = await dbContext.SpareParts.FirstOrDefaultAsync(
                    p => p.Id == partId,
                    cancellationToken);

                if (part is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new DraftApprovalResult(false,
                        FailureReason: $"Spare part {partId} not found",
                        ConflictData: new ApprovalConflictData("SparePart", "SPARE_PART_NOT_FOUND", partId));
                }

                var totalReserved = allReservedForParts.TryGetValue(partId, out var amount) ? amount : 0;
                var newReservationQuantity = requiredParts[partId];
                var totalWithNewReservation = totalReserved + newReservationQuantity;

                if (totalWithNewReservation > part.OnHandQuantity)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new DraftApprovalResult(false,
                        FailureReason: $"Insufficient inventory for {partId}",
                        ConflictData: new ApprovalConflictData("SparePart", "INSUFFICIENT_INVENTORY", partId));
                }

                part.ReservedQuantity = totalWithNewReservation;
            }

            // Capture approved candidate snapshot
            draft.ApprovedCandidatePayload = ExtractApprovedCandidate(planningJob.ResultPayload);

            draft.Status = DraftStatus.Approved;
            draft.ApprovedAtUtc = DateTimeOffset.UtcNow;

            if (v1DraftIdToCleanup.HasValue)
            {
                var v1Draft = await dbContext.Drafts.FindAsync(new object[] { v1DraftIdToCleanup.Value }, cancellationToken: cancellationToken);
                if (v1Draft is not null && v1Draft.Status == DraftStatus.Approved)
                {
                    v1Draft.Status = DraftStatus.Superseded;
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            await CreateLockedTaskAssignmentsAsync(draft.Id, planningJob, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            if (v1DraftIdToCleanup.HasValue)
            {
                try
                {
                    await CancelDraftAsync(v1DraftIdToCleanup.Value, cancellationToken);
                }
                catch
                {
                    // V2 is already committed, cleanup failure is not critical
                    // Log would go here, but don't fail the approval
                }
            }

            return new DraftApprovalResult(true, draft.Id);
        }
        catch (DbUpdateException dex) when (
            dex.InnerException != null &&
            dex.InnerException.GetType().Name == "SqliteException" &&
            dex.Message.Contains("UNIQUE"))
        {
            await transaction.RollbackAsync(cancellationToken);
            var reloadedDraft = await dbContext.Drafts.FirstOrDefaultAsync(
                d => d.PlanningJobId == planningJobId,
                cancellationToken);

            if (reloadedDraft is not null && reloadedDraft.Status == DraftStatus.Approved)
            {
                return new DraftApprovalResult(true, reloadedDraft.Id);
            }

            return new DraftApprovalResult(false,
                FailureReason: "Concurrent draft creation detected",
                ConflictData: new ApprovalConflictData("Draft", "CONCURRENT_DRAFT_CREATION", planningJobId.ToString()));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            var draftId = existingDraft?.Id.ToString() ?? "unknown";
            return new DraftApprovalResult(false,
                FailureReason: "Concurrent modification detected",
                ConflictData: new ApprovalConflictData("Draft", "CONCURRENT_MODIFICATION", draftId));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<DraftApprovalResult> ApproveDraftReplacementAsync(
        Guid planningJobId,
        CancellationToken cancellationToken)
    {
        var planningJob = await dbContext.PlanningJobs.FirstOrDefaultAsync(
            j => j.Id == planningJobId,
            cancellationToken);

        if (planningJob is null)
            return new DraftApprovalResult(false,
                FailureReason: "Planning job not found",
                ConflictData: new ApprovalConflictData("PlanningJob", "PLANNING_JOB_NOT_FOUND", planningJobId.ToString()));

        if (string.IsNullOrEmpty(planningJob.ResultPayload))
            return new DraftApprovalResult(false,
                FailureReason: "Planning job has no result",
                ConflictData: new ApprovalConflictData("PlanningJob", "PLANNING_JOB_NO_RESULT", planningJobId.ToString()));

        Guid? v1DraftIdToCleanup = null;
        if (planningJob.OriginalJobId.HasValue)
        {
            var v1Draft = await GetApprovedDraftForJobAsync(planningJob.OriginalJobId.Value, cancellationToken);
            if (v1Draft is not null)
            {
                v1DraftIdToCleanup = v1Draft.Id;
            }
        }

        var requiredParts = await ExtractRequiredPartsAsync(planningJob, cancellationToken);
        if (requiredParts.Count == 0)
            return new DraftApprovalResult(false,
                FailureReason: "Planning job has no required parts",
                ConflictData: new ApprovalConflictData("PlanningJob", "PLANNING_JOB_NO_PARTS", planningJobId.ToString()));

        using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var draft = new Draft
            {
                Id = Guid.NewGuid(),
                PlanningJobId = planningJobId,
                Status = DraftStatus.Pending
            };
            await dbContext.Drafts.AddAsync(draft, cancellationToken);

            var reservations = new List<Reservation>();
            foreach (var (partId, quantity) in requiredParts)
            {
                var reservation = new Reservation
                {
                    Id = Guid.NewGuid(),
                    DraftId = draft.Id,
                    SparePartId = partId,
                    QuantityReserved = quantity,
                    Status = ReservationStatus.Confirmed,
                    ConfirmedAtUtc = DateTimeOffset.UtcNow
                };
                reservations.Add(reservation);
                await dbContext.Reservations.AddAsync(reservation, cancellationToken);
            }

            var allReservedForParts = await dbContext.Reservations
                .Where(r => requiredParts.Keys.Contains(r.SparePartId) && r.Status == ReservationStatus.Confirmed)
                .GroupBy(r => r.SparePartId)
                .Select(g => new { PartId = g.Key, TotalReserved = g.Sum(r => r.QuantityReserved) })
                .ToDictionaryAsync(g => g.PartId, g => g.TotalReserved, cancellationToken);

            foreach (var (partId, _) in requiredParts)
            {
                var part = await dbContext.SpareParts.FirstOrDefaultAsync(
                    p => p.Id == partId,
                    cancellationToken);

                if (part is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new DraftApprovalResult(false,
                        FailureReason: $"Spare part {partId} not found",
                        ConflictData: new ApprovalConflictData("SparePart", "SPARE_PART_NOT_FOUND", partId));
                }

                var totalReserved = allReservedForParts.TryGetValue(partId, out var amount) ? amount : 0;
                var newReservationQuantity = requiredParts[partId];
                var totalWithNewReservation = totalReserved + newReservationQuantity;

                // DIAGNOSTIC LOGGING - ApproveDraftReplacementAsync
                var comparisonResult = totalWithNewReservation > part.OnHandQuantity;
                Console.WriteLine($"[INVENTORY_DIAGNOSTIC] JobId={planningJobId}|Method=ApproveDraftReplacementAsync|PartId={partId}|OnHand={part.OnHandQuantity}|Reserved={part.ReservedQuantity}|totalReserved={totalReserved}|newQty={newReservationQuantity}|total={totalWithNewReservation}|Exceeds={comparisonResult}");

                if (comparisonResult)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new DraftApprovalResult(false,
                        FailureReason: $"Insufficient inventory for {partId}",
                        ConflictData: new ApprovalConflictData("SparePart", "INSUFFICIENT_INVENTORY", partId));
                }

                part.ReservedQuantity = totalWithNewReservation;
            }

            // Capture approved candidate snapshot
            draft.ApprovedCandidatePayload = ExtractApprovedCandidate(planningJob.ResultPayload);

            draft.Status = DraftStatus.Approved;
            draft.ApprovedAtUtc = DateTimeOffset.UtcNow;

            if (v1DraftIdToCleanup.HasValue)
            {
                var v1Draft = await dbContext.Drafts.FindAsync(new object[] { v1DraftIdToCleanup.Value }, cancellationToken: cancellationToken);
                if (v1Draft is not null && v1Draft.Status == DraftStatus.Approved)
                {
                    v1Draft.Status = DraftStatus.Superseded;
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            await CreateLockedTaskAssignmentsAsync(draft.Id, planningJob, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            if (v1DraftIdToCleanup.HasValue)
            {
                try
                {
                    await CancelDraftAsync(v1DraftIdToCleanup.Value, cancellationToken);
                }
                catch
                {
                    // V2 is already committed, cleanup failure is not critical
                }
            }

            return new DraftApprovalResult(true, draft.Id);
        }
        catch (DbUpdateException dex) when (
            dex.InnerException != null &&
            dex.InnerException.GetType().Name == "SqliteException" &&
            dex.Message.Contains("UNIQUE"))
        {
            await transaction.RollbackAsync(cancellationToken);
            var reloadedDraft = await dbContext.Drafts.FirstOrDefaultAsync(
                d => d.PlanningJobId == planningJobId,
                cancellationToken);

            if (reloadedDraft is not null && reloadedDraft.Status == DraftStatus.Approved)
            {
                return new DraftApprovalResult(true, reloadedDraft.Id);
            }

            return new DraftApprovalResult(false,
                FailureReason: "Concurrent draft creation detected",
                ConflictData: new ApprovalConflictData("Draft", "CONCURRENT_DRAFT_CREATION", planningJobId.ToString()));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DraftApprovalResult(false,
                FailureReason: "Concurrent modification detected",
                ConflictData: new ApprovalConflictData("Draft", "CONCURRENT_MODIFICATION", "unknown"));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<CancellationResult> CancelDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken)
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

        using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Get all confirmed reservations for this draft
            var confirmedReservations = draft.Reservations
                .Where(r => r.Status == ReservationStatus.Confirmed)
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

    private async Task<Dictionary<string, decimal>> ExtractRequiredPartsAsync(
        PlanningJob job,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(job.ResultPayload))
                return [];

            using var doc = JsonDocument.Parse(job.ResultPayload);
            var root = doc.RootElement;

            if (!root.TryGetProperty("outcome", out var outcomeElement) ||
                outcomeElement.GetString() != "CandidatesGenerated")
                return [];

            if (!root.TryGetProperty("result", out var resultElement))
                return [];

            var requiredParts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            if (resultElement.TryGetProperty("candidates", out var candidatesElement) &&
                candidatesElement.ValueKind == JsonValueKind.Array)
            {
                var firstCandidate = candidatesElement.EnumerateArray().FirstOrDefault();
                if (firstCandidate.ValueKind != JsonValueKind.Undefined &&
                    firstCandidate.TryGetProperty("requestedTaskIds", out var taskIdsElement) &&
                    taskIdsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var taskIdElement in taskIdsElement.EnumerateArray())
                    {
                        var taskId = taskIdElement.GetString();
                        if (!string.IsNullOrEmpty(taskId))
                        {
                            var task = await dbContext.MaintenanceTaskDefinitions
                                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

                            if (task?.RequiredPartId is not null)
                            {
                                if (requiredParts.ContainsKey(task.RequiredPartId))
                                    requiredParts[task.RequiredPartId] += task.RequiredPartQuantity;
                                else
                                    requiredParts[task.RequiredPartId] = task.RequiredPartQuantity;
                            }
                        }
                    }
                }
            }

            return requiredParts;
        }
        catch
        {
            return [];
        }
    }

    private async Task CreateLockedTaskAssignmentsAsync(
        Guid draftId,
        PlanningJob planningJob,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(planningJob.ResultPayload))
            return;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(planningJob.ResultPayload);
        }
        catch (JsonException)
        {
            // Silently ignore malformed JSON; no locks can be created from invalid result
            return;
        }

        using (document)
        {
            var rootElement = document.RootElement;

            if (!rootElement.TryGetProperty("result", out var resultElement))
                return;

            if (!resultElement.TryGetProperty("candidates", out var candidatesElement) ||
                candidatesElement.ValueKind != JsonValueKind.Array ||
                candidatesElement.GetArrayLength() == 0)
                return;

            var firstCandidate = candidatesElement[0];
            if (!firstCandidate.TryGetProperty("taskPlacements", out var placementsElement) ||
                placementsElement.ValueKind != JsonValueKind.Array)
                return;

            var locksToCreate = new List<Domain.Entities.LockedTaskAssignment>();
            foreach (var placementElement in placementsElement.EnumerateArray())
            {
                if (placementElement.TryGetProperty("taskId", out var taskIdElement) &&
                    placementElement.TryGetProperty("technicianIds", out var technicianIdsElement) &&
                    technicianIdsElement.ValueKind == JsonValueKind.Array &&
                    technicianIdsElement.GetArrayLength() > 0)
                {
                    var taskId = taskIdElement.GetString();
                    var firstTechnicianId = technicianIdsElement[0].GetString();

                    if (!string.IsNullOrEmpty(taskId) && !string.IsNullOrEmpty(firstTechnicianId))
                    {
                        locksToCreate.Add(new Domain.Entities.LockedTaskAssignment
                        {
                            Id = Guid.NewGuid(),
                            DraftId = draftId,
                            TaskId = taskId,
                            LockedTechnicianId = firstTechnicianId,
                            CreatedAtUtc = DateTimeOffset.UtcNow
                        });
                    }
                }
            }

            if (locksToCreate.Count > 0)
            {
                await dbContext.LockedTaskAssignments.AddRangeAsync(locksToCreate, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private string? ExtractApprovedCandidate(string? resultPayload)
    {
        if (string.IsNullOrEmpty(resultPayload))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(resultPayload);
            var root = doc.RootElement;

            // Try to extract candidates[0] from result object
            if (root.TryGetProperty("result", out var resultElement) &&
                resultElement.TryGetProperty("candidates", out var candidatesElement) &&
                candidatesElement.ValueKind == JsonValueKind.Array &&
                candidatesElement.GetArrayLength() > 0)
            {
                return candidatesElement[0].GetRawText();
            }

            // Fallback: if no candidates found, return entire resultPayload
            return resultPayload;
        }
        catch
        {
            return null;
        }
    }
}
