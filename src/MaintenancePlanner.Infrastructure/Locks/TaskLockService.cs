using MaintenancePlanner.Application.Locks;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Locks;

public sealed class TaskLockService(MaintenancePlannerDbContext dbContext) : ITaskLockService
{
    public async Task<IReadOnlyDictionary<string, string>> GetLocksAsync(
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var locks = await dbContext.LockedTaskAssignments
            .Where(l => l.DraftId == draftId)
            .ToDictionaryAsync(
                l => l.TaskId,
                l => l.LockedTechnicianId,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);

        return locks;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetLocksForJobAsync(
        Guid planningJobId,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts
            .Where(d => d.PlanningJobId == planningJobId && d.Status == DraftStatus.Approved)
            .FirstOrDefaultAsync(cancellationToken);

        if (draft is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return await GetLocksAsync(draft.Id, cancellationToken);
    }
}
