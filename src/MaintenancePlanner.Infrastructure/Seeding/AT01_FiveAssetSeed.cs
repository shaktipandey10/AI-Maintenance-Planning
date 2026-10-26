using MaintenancePlanner.Domain.Entities;

namespace MaintenancePlanner.Infrastructure.Seeding;

/// <summary>AT01 baseline scenario: Five assets with three technicians, maintenance tasks, spare parts, and source references.</summary>
public sealed record AT01SeedData(
    IReadOnlyList<Asset> Assets,
    IReadOnlyList<SparePart> SpareParts,
    IReadOnlyList<MaintenanceTaskDefinition> Tasks,
    IReadOnlyList<Technician> Technicians,
    IReadOnlyList<MaintenanceWindow> MaintenanceWindows);

public static class AT01_FiveAssetSeed
{
    public static AT01SeedData Create()
    {
        // Five assets: P-101 through P-105
        var assets = new List<Asset>
        {
            new()
            {
                Id = "P-101",
                PlantId = "PLANT-01",
                Name = "Cooling Water Pump P-101",
                Model = "CP-100",
                Priority = 1
            },
            new()
            {
                Id = "P-102",
                PlantId = "PLANT-01",
                Name = "Heat Exchanger P-102",
                Model = "HX-200",
                Priority = 2
            },
            new()
            {
                Id = "P-103",
                PlantId = "PLANT-01",
                Name = "Compressor P-103",
                Model = "CM-300",
                Priority = 3
            },
            new()
            {
                Id = "P-104",
                PlantId = "PLANT-01",
                Name = "Motor P-104",
                Model = "MT-400",
                Priority = 4
            },
            new()
            {
                Id = "P-105",
                PlantId = "PLANT-01",
                Name = "Turbine P-105",
                Model = "TB-500",
                Priority = 5
            }
        };

        // Spare parts: one per asset
        var spareParts = new List<SparePart>
        {
            new()
            {
                Id = "BR-101",
                Name = "P-101 replacement bearing",
                Unit = "each",
                OnHandQuantity = 1,
                ReservedQuantity = 0
            },
            new()
            {
                Id = "SEAL-102",
                Name = "P-102 seal kit",
                Unit = "kit",
                OnHandQuantity = 1,
                ReservedQuantity = 0
            },
            new()
            {
                Id = "VALVE-103",
                Name = "P-103 discharge valve",
                Unit = "each",
                OnHandQuantity = 1,
                ReservedQuantity = 0
            },
            new()
            {
                Id = "WINDING-104",
                Name = "P-104 stator winding",
                Unit = "each",
                OnHandQuantity = 1,
                ReservedQuantity = 0
            },
            new()
            {
                Id = "BLADE-105",
                Name = "P-105 turbine blade",
                Unit = "each",
                OnHandQuantity = 1,
                ReservedQuantity = 0
            }
        };

        // Maintenance tasks: 1 per asset (simplified for feasibility in 8-hour window with 3 technicians)
        var tasks = new List<MaintenanceTaskDefinition>
        {
            // P-101: Bearing replacement (1 task, 1.5h)
            new()
            {
                Id = "P101-BEARING-REPLACEMENT",
                AssetId = "P-101",
                Name = "Bearing replacement",
                DurationMinutes = 90,
                RequiredSkills = "ROTATING-EQUIPMENT",
                RequiredCrewSize = 2,
                RequiredPartId = "BR-101",
                RequiredPartQuantity = 1,
                SourceDocumentId = "MAN-P101-001",
                SourceSection = "Section 3.2: Bearing replacement steps"
            },
            // P-102: Heat Exchanger seal replacement (1 task, 1h)
            new()
            {
                Id = "P102-SEAL-REPLACEMENT",
                AssetId = "P-102",
                Name = "Seal kit replacement",
                DurationMinutes = 60,
                RequiredSkills = "MECHANICAL",
                RequiredCrewSize = 1,
                RequiredPartId = "SEAL-102",
                RequiredPartQuantity = 1,
                SourceDocumentId = "MAN-P102-001",
                SourceSection = "Section 3.1: Seal replacement procedure"
            },
            // P-103: Compressor valve replacement (1 task, 1.5h)
            new()
            {
                Id = "P103-VALVE-REPLACEMENT",
                AssetId = "P-103",
                Name = "Discharge valve replacement",
                DurationMinutes = 90,
                RequiredSkills = "ROTATING-EQUIPMENT",
                RequiredCrewSize = 1,
                RequiredPartId = "VALVE-103",
                RequiredPartQuantity = 1,
                SourceDocumentId = "MAN-P103-001",
                SourceSection = "Section 3.3: Valve replacement steps"
            },
            // P-104: Motor winding replacement (1 task, 1.5h)
            new()
            {
                Id = "P104-WINDING-REPLACEMENT",
                AssetId = "P-104",
                Name = "Stator winding replacement",
                DurationMinutes = 90,
                RequiredSkills = "ROTATING-EQUIPMENT",
                RequiredCrewSize = 2,
                RequiredPartId = "WINDING-104",
                RequiredPartQuantity = 1,
                SourceDocumentId = "MAN-P104-001",
                SourceSection = "Section 3.4: Winding replacement procedure"
            },
            // P-105: Turbine blade replacement (1 task, 1.5h)
            new()
            {
                Id = "P105-BLADE-REPLACEMENT",
                AssetId = "P-105",
                Name = "Turbine blade replacement",
                DurationMinutes = 90,
                RequiredSkills = "ROTATING-EQUIPMENT",
                RequiredCrewSize = 2,
                RequiredPartId = "BLADE-105",
                RequiredPartQuantity = 1,
                SourceDocumentId = "MAN-P105-001",
                SourceSection = "Section 3.5: Blade replacement sequence"
            }
        };

        // Three technicians (same skills as P101AnchorSeed for consistency)
        var technicians = new List<Technician>
        {
            CreateTechnician("TECH-001", "Asha Rao", 42.00m, ["MECHANICAL", "ROTATING-EQUIPMENT"]),
            CreateTechnician("TECH-002", "Kabir Shah", 39.50m, ["MECHANICAL", "ROTATING-EQUIPMENT"]),
            CreateTechnician("TECH-003", "Meera Iyer", 37.00m, ["MECHANICAL", "INSTRUMENTATION"])
        };

        // Single maintenance window: 8-hour baseline window
        var maintenanceWindows = new List<MaintenanceWindow>
        {
            new()
            {
                Id = "MW-AT01-BASELINE-20261024",
                Name = "AT01 baseline maintenance window",
                Start = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(5.5)),
                End = new DateTimeOffset(2026, 10, 24, 16, 0, 0, TimeSpan.FromHours(5.5)),
                TimeZoneId = "Asia/Kolkata"
            }
        };

        return new AT01SeedData(assets, spareParts, tasks, technicians, maintenanceWindows);
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
