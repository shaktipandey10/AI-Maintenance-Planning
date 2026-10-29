using System.Text.Json;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// Shared approval fixtures: schedulable P-101 candidates for the seeded baseline window and the
/// service registrations approval needs (approval revalidates the selected candidate).
/// </summary>
internal static class ApprovalTestSupport
{
    public const string BaselineWindowId = "MW-P101-BASELINE-20261024";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddApprovalServices(this IServiceCollection services)
    {
        services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
        services.AddScoped<ISparePartReadModelProvider>(sp =>
            new ScenarioAwareSparePartReadModelProvider(sp.GetRequiredService<IMaintenancePlannerQueries>()));
        services.AddScoped<PlanningCandidateValidator>();
        services.AddScoped<IDraftApprovalService, DraftApprovalService>();
        return services;
    }

    /// <summary>Stable-technician-ID crew: TECH-001 prepares, TECH-001/TECH-002 replace, TECH-003 verifies.</summary>
    public static object StableIdCandidate() => P101Candidate(
        "Earliest feasible schedule using stable technician-ID tie-breaking.",
        StableIdPlacements());

    /// <summary>Lowest-cost crew: TECH-003 prepares, TECH-002/TECH-001 replace, TECH-003 verifies.</summary>
    public static object LowestCostCandidate() => P101Candidate(
        "Earliest feasible schedule using the lowest hourly-cost qualified crew.",
        LowestCostPlacements());

    /// <summary>
    /// Adds a two-person crew able to run the whole P-101 chain during the baseline window, so plans staffed by
    /// different crews compete only for parts, not for technician time: the lead prepares and joins the bearing
    /// replacement; the second joins the bearing replacement and verifies.
    /// </summary>
    public static async Task AddTwoPersonCrewAsync(MaintenancePlannerDbContext dbContext, string lead, string second)
    {
        await dbContext.Technicians.AddRangeAsync(
            CrewTechnician(lead, "MECHANICAL", "ROTATING-EQUIPMENT"),
            CrewTechnician(second, "ROTATING-EQUIPMENT", "INSTRUMENTATION"));
        await dbContext.SaveChangesAsync();
    }

    /// <summary>The stable-ID plan's timings (requires one BR-101), staffed by a two-person crew.</summary>
    public static object TwoPersonCrewCandidate(string lead, string second) => P101Candidate(
        $"Plan staffed by {lead} and {second}",
        [
            Placement("P101-PREP", 8, 9, lead),
            Placement("P101-BEARING-REPLACEMENT", 9, 12, lead, second),
            Placement("P101-VERIFICATION", 12, 14, second)
        ]);

    /// <summary>
    /// Adds one job per ID, all requiring the same single BR-101: the first uses the seeded crew and every other
    /// job its own two-person crew, so the jobs compete only for the part.
    /// </summary>
    public static async Task AddJobsCompetingOnlyForPartsAsync(
        MaintenancePlannerDbContext dbContext,
        IReadOnlyList<Guid> jobIds)
    {
        for (var index = 0; index < jobIds.Count; index++)
        {
            if (index == 0)
            {
                await AddSucceededJobAsync(dbContext, jobIds[index]);
                continue;
            }

            var lead = $"TECH-CREW{index:00}-A";
            var second = $"TECH-CREW{index:00}-B";
            await AddTwoPersonCrewAsync(dbContext, lead, second);
            await AddSucceededJobAsync(dbContext, jobIds[index], null, TwoPersonCrewCandidate(lead, second));
        }
    }

    private static Technician CrewTechnician(string id, params string[] skills) => new()
    {
        Id = id,
        Name = $"Test technician {id}",
        HourlyRate = 40m,
        TechnicalSkills = skills.Select(skill => new TechnicalSkill { TechnicianId = id, SkillCode = skill }).ToList(),
        TechnicianAvailabilities =
        [
            new TechnicianAvailability { TechnicianId = id, AvailableFrom = At(8), AvailableTo = At(16) }
        ]
    };

    /// <summary>A candidate whose bearing placement is shorter than the authoritative 180-minute duration.</summary>
    public static object InvalidDurationCandidate() => new
    {
        taskPlacements = new object[]
        {
            Placement("P101-PREP", 8, 9, "TECH-001"),
            Placement("P101-BEARING-REPLACEMENT", 9, 10, "TECH-001", "TECH-002"),
            Placement("P101-VERIFICATION", 10, 12, "TECH-003")
        },
        rationale = "Hand-edited candidate with an impossible duration",
        evidenceReferences = Array.Empty<object>(),
        requestedTaskIds = new[] { "P101-BEARING-REPLACEMENT" },
        dependencyTaskIds = new[] { "P101-PREP", "P101-VERIFICATION" }
    };

    public static PlanningJob CreateSucceededJob(
        Guid jobId,
        Guid? originalJobId = null,
        params object[] candidates) => new()
    {
        Id = jobId,
        OperationId = Guid.NewGuid(),
        OriginalJobId = originalJobId,
        RequestPayload = RequestPayload(),
        Status = PlanningJobStatus.Succeeded,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        ResultPayload = ResultPayload(candidates)
    };

    /// <summary>A P-101 planning request for the seeded baseline window.</summary>
    public static string RequestPayload(string requestText = "Plan bearing replacement for P-101.") =>
        JsonSerializer.Serialize(new
        {
            requestText,
            assetIds = new[] { "P-101" },
            windowId = BaselineWindowId
        }, JsonOptions);

    /// <summary>A CandidatesGenerated result envelope; defaults to the stable-ID candidate.</summary>
    public static string ResultPayload(params object[] candidates) =>
        JsonSerializer.Serialize(new
        {
            outcome = "CandidatesGenerated",
            result = new
            {
                candidates = candidates.Length == 0 ? new[] { StableIdCandidate() } : candidates,
                assumptions = Array.Empty<string>(),
                evidenceReferences = Array.Empty<object>(),
                interpretation = new
                {
                    maintenanceIntent = "Replace bearing",
                    scope = "P-101",
                    proposedAssetIds = new[] { "P-101" },
                    proposedTaskIds = new[] { "P101-BEARING-REPLACEMENT" },
                    missingInformation = Array.Empty<string>(),
                    assumptions = Array.Empty<string>(),
                    scopeResolution = "Resolved",
                    scopeMappings = Array.Empty<object>()
                }
            }
        }, JsonOptions);

    /// <summary>The stable-ID crew's schedulable placements, for tests that build their own candidate shape.</summary>
    public static object[] StableIdPlacements() =>
    [
        Placement("P101-PREP", 8, 9, "TECH-001"),
        Placement("P101-BEARING-REPLACEMENT", 9, 12, "TECH-001", "TECH-002"),
        Placement("P101-VERIFICATION", 12, 14, "TECH-003")
    ];

    /// <summary>The lowest-cost crew's schedulable placements, for tests that build their own candidate shape.</summary>
    public static object[] LowestCostPlacements() =>
    [
        Placement("P101-PREP", 8, 9, "TECH-003"),
        Placement("P101-BEARING-REPLACEMENT", 9, 12, "TECH-002", "TECH-001"),
        Placement("P101-VERIFICATION", 12, 14, "TECH-003")
    ];

    public static async Task AddSucceededJobAsync(
        MaintenancePlannerDbContext dbContext,
        Guid jobId,
        Guid? originalJobId = null,
        params object[] candidates)
    {
        await dbContext.PlanningJobs.AddAsync(CreateSucceededJob(jobId, originalJobId, candidates));
        await dbContext.SaveChangesAsync();
    }

    /// <summary>The raw JSON of a candidate exactly as it is stored inside a job's result payload.</summary>
    public static string CandidateJson(PlanningJob job, int index)
    {
        using var document = JsonDocument.Parse(job.ResultPayload!);
        return document.RootElement.GetProperty("result").GetProperty("candidates")[index].GetRawText();
    }

    private static object P101Candidate(string rationale, object[] taskPlacements) => new
    {
        taskPlacements,
        rationale,
        evidenceReferences = Array.Empty<object>(),
        requestedTaskIds = new[] { "P101-BEARING-REPLACEMENT" },
        dependencyTaskIds = new[] { "P101-PREP", "P101-VERIFICATION" }
    };

    public static object Placement(string taskId, int startHour, int endHour, params string[] technicianIds) => new
    {
        taskId,
        start = At(startHour),
        end = At(endHour),
        technicianIds
    };

    private static DateTimeOffset At(int hour) =>
        new(2026, 10, 24, hour, 0, 0, TimeSpan.FromHours(5.5));
}
