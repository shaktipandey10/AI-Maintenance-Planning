using MaintenancePlanner.Application.History;
using MaintenancePlanner.Domain.Entities;

namespace MaintenancePlanner.Infrastructure.Persistence;

public static class BusinessActionHistory
{
    /// <summary>
    /// Adds a history record to the context, to be saved by the same SaveChanges (and transaction) as the action, so
    /// an action is never recorded without being applied or applied without being recorded.
    /// </summary>
    public static BusinessActionRecord RecordBusinessAction(
        this MaintenancePlannerDbContext dbContext,
        BusinessActor? actor,
        string action,
        Guid? planningJobId,
        Guid? draftId,
        string? taskId = null,
        string? details = null)
    {
        var record = new BusinessActionRecord
        {
            Id = Guid.NewGuid(),
            Action = action,
            PerformedBy = actor?.Role ?? BusinessActor.Unspecified,
            PerformedAtUtc = DateTimeOffset.UtcNow,
            PlanningJobId = planningJobId,
            DraftId = draftId,
            TaskId = taskId,
            Details = details
        };
        dbContext.BusinessActionRecords.Add(record);
        return record;
    }
}
