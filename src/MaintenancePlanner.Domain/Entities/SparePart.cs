namespace MaintenancePlanner.Domain.Entities
{
    public sealed class SparePart
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public decimal OnHandQuantity { get; set; }
        public decimal ReservedQuantity { get; set; }
        public decimal AvailableQuantity => OnHandQuantity - ReservedQuantity;
        public ICollection<Asset> CompatibleAssets { get; set; } = new List<Asset>();
    }
}