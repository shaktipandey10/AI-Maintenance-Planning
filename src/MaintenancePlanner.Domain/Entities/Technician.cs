namespace MaintenancePlanner.Domain.Entities
{
    public sealed class Technician
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal HourlyRate { get; set; } 
        public ICollection<TechnicalSkill> TechnicalSkills { get; set; } = new List<TechnicalSkill>();
        public ICollection<TechnicianAvailability> TechnicianAvailabilities { get; set; } = new List<TechnicianAvailability>();
    }
}   