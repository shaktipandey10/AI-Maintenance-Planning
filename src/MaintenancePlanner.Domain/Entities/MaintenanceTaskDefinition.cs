namespace MaintenancePlanner.Domain.Entities
{
    public sealed class MaintenanceTaskDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string AssetId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int DurationMinutes { get; set; }
        public string RequiredSkills { get; set; } = string.Empty;
        public int RequiredCrewSize { get; set; }
        public string? RequiredPartId { get; set; }
        public decimal RequiredPartQuantity { get; set; }
        public string? PredecessorTaskId { get; set; }
        
        public string SourceDocumentId { get; set; } = string.Empty;
        public string SourceSection { get; set; } = string.Empty;
    }
}