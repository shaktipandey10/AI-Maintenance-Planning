namespace MaintenancePlanner.Api.Workers;

public sealed class PlanningJobWorkerOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
}
