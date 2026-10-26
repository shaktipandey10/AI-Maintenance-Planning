namespace MaintenancePlanner.Domain.Entities;

public enum PlanningJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}
