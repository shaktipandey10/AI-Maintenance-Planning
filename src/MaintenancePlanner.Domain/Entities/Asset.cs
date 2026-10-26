namespace MaintenancePlanner.Domain.Entities
{
    public sealed class Asset
    {
        public string Id { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public int Priority { get; set; }
        public ICollection<MaintenanceHistoryRecord> MaintenanceHistoryRecords { get; set; } =
            new List<MaintenanceHistoryRecord>();
    }
}   