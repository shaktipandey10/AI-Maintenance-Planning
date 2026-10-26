namespace MaintenancePlanner.Domain.Entities
{
    public sealed class TechnicianAvailability
    {
        public int Id { get; set; } 
        public string TechnicianId { get; set; } = string.Empty;
        public DateTimeOffset AvailableFrom { get; set; }
        public DateTimeOffset AvailableTo { get; set; }
        public Technician Technician { get; set; } = new Technician();
    }
}   
