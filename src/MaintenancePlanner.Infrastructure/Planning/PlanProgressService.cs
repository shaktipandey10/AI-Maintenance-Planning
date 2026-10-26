using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Planning;

public sealed class PlanProgressService(MaintenancePlannerDbContext dbContext) : IPlanProgressService
{
    public async Task<PlanProgressResult> MarkTaskCompleteAsync(
        Guid draftId,
        string sparePartId,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);

        if (draft is null)
            return new PlanProgressResult(false, "Draft not found");

        if (draft.Status != DraftStatus.Approved)
            return new PlanProgressResult(false, "Only approved drafts can have tasks marked complete");

        var reservations = await dbContext.Reservations
            .Where(r => r.DraftId == draftId && r.SparePartId == sparePartId)
            .ToListAsync(cancellationToken);

        if (reservations.Count == 0)
            return new PlanProgressResult(false, $"No reservation found for spare part {sparePartId} in this draft");

        // Mark one reservation as completed
        var incompleteReservation = reservations.FirstOrDefault(r => !r.CompletedAtUtc.HasValue);
        if (incompleteReservation is null)
            return new PlanProgressResult(false, "All tasks for this spare part are already completed");

        incompleteReservation.CompletedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new PlanProgressResult(true);
    }

    public async Task<PlanProgress> GetPlanProgressAsync(
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts.FirstOrDefaultAsync(d => d.Id == draftId, cancellationToken);

        if (draft is null)
            throw new InvalidOperationException("Draft not found");

        var reservations = await dbContext.Reservations
            .Where(r => r.DraftId == draftId && r.Status == ReservationStatus.Confirmed)
            .GroupBy(r => r.SparePartId)
            .Select(g => new
            {
                SparePartId = g.Key,
                Total = g.Sum(r => r.QuantityReserved),
                Completed = g.Sum(r => r.CompletedAtUtc.HasValue ? r.QuantityReserved : 0)
            })
            .ToListAsync(cancellationToken);

        var totalRequired = reservations.Sum(r => r.Total);
        var completedQuantity = reservations.Sum(r => r.Completed);
        var remainingQuantity = totalRequired - completedQuantity;

        var tasks = reservations.Select(r => new TaskProgress(
            r.SparePartId,
            r.Total,
            r.Completed,
            r.Total - r.Completed
        )).ToList();

        return new PlanProgress(draftId, totalRequired, completedQuantity, remainingQuantity, tasks);
    }
}
