namespace MaintenancePlanner.Domain.Entities
{
    public sealed class TechnicalSkill
    {
        public int Id { get; set; } 
        public string TechnicianId { get; set; } = string.Empty;
        public string SkillCode { get; set; } = string.Empty;
        public Technician Technician { get; set; } = new Technician();
        
    }
}   
