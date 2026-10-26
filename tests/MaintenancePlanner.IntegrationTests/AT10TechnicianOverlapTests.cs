using System.Text.Json;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT10TechnicianOverlapTests
{
    [Fact]
    public async Task AT10_TechnicianOverlapDetected_WhenSameTechnicianScheduledToOverlappingTasks()
    {
        // AT10: Overlapping technician assignments within a single candidate are detected and rejected
        await using var fixture = await PlanningValidationFixture.CreateAsync();
        var validator = fixture.ServiceProvider.GetRequiredService<PlanningCandidateValidator>();

        var windowStart = DateTimeOffset.UtcNow;

        var overlappingCandidate = new PlanningCandidate(
            TaskPlacements:
            [
                new PlanningTaskPlacement(
                    "P101-PREP",
                    windowStart.AddMinutes(0),
                    windowStart.AddMinutes(60),
                    ["TECH-001"]),
                new PlanningTaskPlacement(
                    "P101-BEARING-REPLACEMENT",
                    windowStart.AddMinutes(30), // Overlaps with PREP (10:00-11:00)
                    windowStart.AddMinutes(210), // 10:30-13:30
                    ["TECH-001"]),
                new PlanningTaskPlacement(
                    "P101-VERIFICATION",
                    windowStart.AddMinutes(210),
                    windowStart.AddMinutes(330), // 13:30-15:30, no overlap
                    ["TECH-002"])
            ],
            Rationale: "Candidate with overlapping technician assignments",
            EvidenceReferences: [])
        {
            RequestedTaskIds = ["P101-PREP", "P101-BEARING-REPLACEMENT", "P101-VERIFICATION"],
            DependencyTaskIds = []
        };

        var request = new PlanningCandidateValidationRequest(
            AssetIds: ["P-101"],
            MaintenanceWindowId: "MW-P101",
            Candidate: overlappingCandidate);

        var validation = await validator.ValidateAsync(request, CancellationToken.None);

        // Assert: Validation fails due to overlap
        Assert.False(validation.IsFeasible);
        var overlapViolation = validation.Violations.FirstOrDefault(v => v.Code == "TECHNICIAN_SLOT_CONFLICT");
        Assert.True(overlapViolation != null, $"Expected TECHNICIAN_SLOT_CONFLICT but got: {string.Join("; ", validation.Violations.Select(v => v.Code))}");
        Assert.Contains("TECH-001", overlapViolation!.ResourceId ?? "");
    }

    [Fact]
    public async Task AT10_AdjacentNonOverlappingIntervals_AreValid()
    {
        // AT10: Adjacent tasks (end time equals next start time) should not trigger overlap
        await using var fixture = await PlanningValidationFixture.CreateAsync();
        var validator = fixture.ServiceProvider.GetRequiredService<PlanningCandidateValidator>();

        var windowStart = DateTimeOffset.UtcNow;

        // P101-PREP: 10:00-11:00
        // P101-BEARING-REPLACEMENT: 11:00-14:00 (starts exactly when PREP ends)
        // This is adjacent, NOT overlapping
        var adjacentCandidate = new PlanningCandidate(
            TaskPlacements:
            [
                new PlanningTaskPlacement(
                    "P101-PREP",
                    windowStart.AddMinutes(0),
                    windowStart.AddMinutes(60), // Ends at 11:00
                    ["TECH-001"]),
                new PlanningTaskPlacement(
                    "P101-BEARING-REPLACEMENT",
                    windowStart.AddMinutes(60), // Starts at 11:00 (exactly when PREP ends)
                    windowStart.AddMinutes(240),
                    ["TECH-001"]),
                new PlanningTaskPlacement(
                    "P101-VERIFICATION",
                    windowStart.AddMinutes(240),
                    windowStart.AddMinutes(360),
                    ["TECH-002"])
            ],
            Rationale: "Adjacent non-overlapping technician assignments",
            EvidenceReferences: [])
        {
            RequestedTaskIds = ["P101-PREP", "P101-BEARING-REPLACEMENT", "P101-VERIFICATION"],
            DependencyTaskIds = []
        };

        var request = new PlanningCandidateValidationRequest(
            AssetIds: ["P-101"],
            MaintenanceWindowId: "MW-P101",
            Candidate: adjacentCandidate);

        var validation = await validator.ValidateAsync(request, CancellationToken.None);

        // Assert: No overlap violations (adjacent intervals are allowed)
        var overlapViolations = validation.Violations.Where(v => v.Code == "TECHNICIAN_SLOT_CONFLICT").ToList();
        Assert.Empty(overlapViolations);
    }

    [Fact]
    public async Task AT10_DifferentTechnicians_NoOverlapDetected()
    {
        // AT10: Different technicians assigned to overlapping time intervals should not trigger overlap
        await using var fixture = await PlanningValidationFixture.CreateAsync();
        var validator = fixture.ServiceProvider.GetRequiredService<PlanningCandidateValidator>();

        var windowStart = DateTimeOffset.UtcNow;

        // TECH-001 on P101-PREP: 10:00-11:00
        // TECH-002 on P101-BEARING-REPLACEMENT: 10:30-13:30 (time overlaps but different tech)
        var differentTechsCandidate = new PlanningCandidate(
            TaskPlacements:
            [
                new PlanningTaskPlacement(
                    "P101-PREP",
                    windowStart.AddMinutes(0),
                    windowStart.AddMinutes(60),
                    ["TECH-001"]),
                new PlanningTaskPlacement(
                    "P101-BEARING-REPLACEMENT",
                    windowStart.AddMinutes(30), // Time overlaps
                    windowStart.AddMinutes(210),
                    ["TECH-002"]), // Different technician
                new PlanningTaskPlacement(
                    "P101-VERIFICATION",
                    windowStart.AddMinutes(210),
                    windowStart.AddMinutes(330),
                    ["TECH-001"])
            ],
            Rationale: "Different technicians, no overlap conflict",
            EvidenceReferences: [])
        {
            RequestedTaskIds = ["P101-PREP", "P101-BEARING-REPLACEMENT", "P101-VERIFICATION"],
            DependencyTaskIds = []
        };

        var request = new PlanningCandidateValidationRequest(
            AssetIds: ["P-101"],
            MaintenanceWindowId: "MW-P101",
            Candidate: differentTechsCandidate);

        var validation = await validator.ValidateAsync(request, CancellationToken.None);

        // Assert: No overlap violations (different technicians)
        var overlapViolations = validation.Violations.Where(v => v.Code == "TECHNICIAN_SLOT_CONFLICT").ToList();
        Assert.Empty(overlapViolations);
    }

    [Fact]
    public async Task AT10_TechnicianSkillsEnforced_WithOverlapValidation()
    {
        // AT10: Technician skills are still enforced even when checking for overlaps
        await using var fixture = await PlanningValidationFixture.CreateAsync();
        var validator = fixture.ServiceProvider.GetRequiredService<PlanningCandidateValidator>();

        var windowStart = DateTimeOffset.UtcNow;

        // Assign TECH-005 (no skills) to tasks that require "BearingReplacement"
        var unskilledCandidate = new PlanningCandidate(
            TaskPlacements:
            [
                new PlanningTaskPlacement(
                    "P101-PREP",
                    windowStart.AddMinutes(0),
                    windowStart.AddMinutes(60),
                    ["TECH-005"]), // Unqualified technician
                new PlanningTaskPlacement(
                    "P101-BEARING-REPLACEMENT",
                    windowStart.AddMinutes(60),
                    windowStart.AddMinutes(240),
                    ["TECH-005"]), // Unqualified technician
                new PlanningTaskPlacement(
                    "P101-VERIFICATION",
                    windowStart.AddMinutes(240),
                    windowStart.AddMinutes(360),
                    ["TECH-001"])
            ],
            Rationale: "Candidate with unqualified technician",
            EvidenceReferences: [])
        {
            RequestedTaskIds = ["P101-PREP", "P101-BEARING-REPLACEMENT", "P101-VERIFICATION"],
            DependencyTaskIds = []
        };

        var request = new PlanningCandidateValidationRequest(
            AssetIds: ["P-101"],
            MaintenanceWindowId: "MW-P101",
            Candidate: unskilledCandidate);

        var validation = await validator.ValidateAsync(request, CancellationToken.None);

        // Assert: Detects skill violations (TECH-005 has no skills)
        Assert.False(validation.IsFeasible);
        var skillViolations = validation.Violations.Where(v => v.Code == "TECHNICIAN_UNQUALIFIED").ToList();
        Assert.NotEmpty(skillViolations);
    }

    [Fact]
    public async Task AT10_ApprovalWithoutOverlaps_Succeeds()
    {
        // AT10: Valid candidates without overlaps can be approved successfully
        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        var windowStart = DateTimeOffset.UtcNow;

        // Create planning job with valid (non-overlapping) task placements
        await using (var dbContext = fixture.CreateDbContext())
        {
            var placements = new[]
            {
                new { taskId = "P101-PREP", start = windowStart.AddMinutes(0), end = windowStart.AddMinutes(60), technicianIds = new[] { "TECH-001" } },
                new { taskId = "P101-BEARING-REPLACEMENT", start = windowStart.AddMinutes(60), end = windowStart.AddMinutes(240), technicianIds = new[] { "TECH-001" } },
                new { taskId = "P101-VERIFICATION", start = windowStart.AddMinutes(240), end = windowStart.AddMinutes(360), technicianIds = new[] { "TECH-002" } }
            };

            var result = new
            {
                outcome = "CandidatesGenerated",
                result = new
                {
                    candidates = new[]
                    {
                        new
                        {
                            taskPlacements = placements,
                            rationale = "Valid candidate without overlaps",
                            evidenceReferences = new object[0],
                            requestedTaskIds = new[] { "P101-PREP", "P101-BEARING-REPLACEMENT", "P101-VERIFICATION" },
                            dependencyTaskIds = new object[0]
                        }
                    }
                }
            };

            var planningJob = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Plan P-101 bearing replacement","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };

            await dbContext.PlanningJobs.AddAsync(planningJob);
            await dbContext.SaveChangesAsync();
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var approvalResult = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        // Assert: Approval succeeds (no overlap)
        Assert.True(approvalResult.Succeeded);
        Assert.NotEqual(Guid.Empty, approvalResult.DraftId);
    }

    private sealed class PlanningValidationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private PlanningValidationFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            WebApplication app)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            _app = app;
            ServiceProvider = app.Services;
        }

        public IServiceProvider ServiceProvider { get; }

        public static async Task<PlanningValidationFixture> CreateAsync()
        {
            var connectionString = $"Data Source=AT10-Validation-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();

            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(keeperConnection)
                .Options;

            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
            builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            builder.Services.AddScoped<ISparePartReadModelProvider>(sp =>
                new ScenarioAwareSparePartReadModelProvider(
                    sp.GetRequiredService<IMaintenancePlannerQueries>()));
            builder.Services.AddScoped<PlanningCandidateValidator>();

            var app = builder.Build();

            return new PlanningValidationFixture(keeperConnection, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            await _keeperConnection.DisposeAsync();
        }
    }

    private sealed class ApprovalTestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private ApprovalTestFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            WebApplication app)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            _app = app;
            ServiceProvider = app.Services;
        }

        public IServiceProvider ServiceProvider { get; }

        public static async Task<ApprovalTestFixture> CreateAsync()
        {
            var connectionString = $"Data Source=AT10-Approval-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();

            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(keeperConnection)
                .Options;

            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
            builder.Services.AddScoped<IDraftApprovalService, DraftApprovalService>();

            var app = builder.Build();

            return new ApprovalTestFixture(keeperConnection, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            await _keeperConnection.DisposeAsync();
        }
    }
}
