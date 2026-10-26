namespace MaintenancePlanner.Domain.Entities
{
    public sealed class MaintenanceWindow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }
        public string TimeZoneId { get; set; } = "Asia/Kolkata";
    }
}   