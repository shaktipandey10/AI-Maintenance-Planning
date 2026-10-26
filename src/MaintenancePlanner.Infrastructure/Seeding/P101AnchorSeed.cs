using MaintenancePlanner.Domain.Entities;

namespace MaintenancePlanner.Infrastructure.Seeding;

public sealed record P101SeedData(
    Asset Asset,
    SparePart SparePart,
    IReadOnlyList<MaintenanceTaskDefinition> Tasks,
    IReadOnlyList<Technician> Technicians,
    IReadOnlyList<MaintenanceWindow> MaintenanceWindows);

public static class P101AnchorSeed
{
    public static P101SeedData Create()
    {
        const string assetId = "P-101";
        const string manualId = "MAN-P101-001";

        var asset = new Asset
        {
            Id = assetId,
            PlantId = "PLANT-01",
            Name = "Cooling Water Pump P-101",
            Model = "CP-100",
            Priority = 1
        };

        var sparePart = new SparePart
        {
            Id = "BR-101",
            Name = "P-101 replacement bearing",
            Unit = "each",
            OnHandQuantity = 1,
            ReservedQuantity = 0
        };

        var tasks = new List<MaintenanceTaskDefinition>
        {
            new()
            {
                Id = "P101-PREP",
                AssetId = assetId,
                Name = "Preparation",
                DurationMinutes = 60,
                RequiredSkills = "MECHANICAL",
                RequiredCrewSize = 1,
                RequiredPartQuantity = 0,
                SourceDocumentId = manualId,
                SourceSection = "Planning assumptions: Preparation"
            },
            new()
            {
                Id = "P101-BEARING-REPLACEMENT",
                AssetId = assetId,
                Name = "Bearing replacement",
                DurationMinutes = 180,
                RequiredSkills = "ROTATING-EQUIPMENT",
                RequiredCrewSize = 2,
                RequiredPartId = sparePart.Id,
                RequiredPartQuantity = 1,
                PredecessorTaskId = "P101-PREP",
                SourceDocumentId = manualId,
                SourceSection = "Planning assumptions: Bearing replacement"
            },
            new()
            {
                Id = "P101-VERIFICATION",
                AssetId = assetId,
                Name = "Verification",
                DurationMinutes = 120,
                RequiredSkills = "INSTRUMENTATION",
                RequiredCrewSize = 1,
                RequiredPartQuantity = 0,
                PredecessorTaskId = "P101-BEARING-REPLACEMENT",
                SourceDocumentId = manualId,
                SourceSection = "Planning assumptions: Verification"
            }
        };

        var technicians = new List<Technician>
        {
            CreateTechnician("TECH-001", "Asha Rao", 42.00m, ["MECHANICAL", "ROTATING-EQUIPMENT"]),
            CreateTechnician("TECH-002", "Kabir Shah", 39.50m, ["MECHANICAL", "ROTATING-EQUIPMENT"]),
            CreateTechnician("TECH-003", "Meera Iyer", 37.00m, ["MECHANICAL", "INSTRUMENTATION"])
        };

        var maintenanceWindows = new List<MaintenanceWindow>
        {
            new()
            {
                Id = "MW-P101-BASELINE-20261024",
                Name = "P-101 Saturday baseline window",
                Start = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
                End = new DateTimeOffset(2026, 10, 24, 16, 0, 0, TimeSpan.FromHours(5.5)),
                TimeZoneId = "Asia/Kolkata"
            },
            new()
            {
                Id = "MW-P101-REDUCED-20261024",
                Name = "P-101 Saturday reduced window",
                Start = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
                End = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(5.5)),
                TimeZoneId = "Asia/Kolkata"
            }
        };

        return new P101SeedData(asset, sparePart, tasks, technicians, maintenanceWindows);
    }

    private static Technician CreateTechnician(
        string id,
        string name,
        decimal hourlyRate,
        IReadOnlyList<string> skills)
    {
        var availableFrom = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5));
        var availableTo = new DateTimeOffset(2026, 10, 24, 16, 0, 0, TimeSpan.FromHours(5.5));

        return new Technician
        {
            Id = id,
            Name = name,
            HourlyRate = hourlyRate,
            TechnicalSkills = skills.Select(skill => new TechnicalSkill
            {
                TechnicianId = id,
                SkillCode = skill
            }).ToList(),
            TechnicianAvailabilities =
            [
                new TechnicianAvailability
                {
                    TechnicianId = id,
                    AvailableFrom = availableFrom,
                    AvailableTo = availableTo
                }
            ]
        };
    }
}