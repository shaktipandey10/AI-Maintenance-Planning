namespace MaintenancePlanner.Domain.Entities
{
    public sealed class MaintenanceHistoryRecord
    {
        public string Id { get; set; } = string.Empty;
        public string AssetId { get; set; } = string.Empty;
        public Asset Asset { get; set; } = null!;
        public DateTimeOffset PerformedAt { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string Outcome { get; set; } = string.Empty;
        public string SourceDocumentId { get; set; } = string.Empty;
        public string SourceReference { get; set; } = string.Empty;
    }
}