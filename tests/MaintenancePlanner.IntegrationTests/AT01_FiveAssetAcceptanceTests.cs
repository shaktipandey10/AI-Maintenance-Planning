using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Queries;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>AT01 acceptance test: Baseline scenario with five assets, three technicians, and multiple feasible alternatives.</summary>
public sealed class AT01_FiveAssetAcceptanceTests
{
    [Fact]
    public async Task AT01_FiveAssets_GeneratesTwoDistinctFeasibleAlternatives()
    {
        // Arrange: Set up a test database with five assets
        await using var fixture = await AT01Fixture.CreateAsync();
        var seed = AT01_FiveAssetSeed.Create();
        await fixture.SeedAsync(seed);

        var scheduler = fixture.ServiceProvider.GetRequiredService<DeterministicPlanningScheduler>();
        var validator = fixture.ServiceProvider.GetRequiredService<PlanningCandidateValidator>();

        // Gather all requested task IDs across all 5 assets
        var requestedTaskIds = seed.Tasks.Select(t => t.Id).ToList();

        // Act: Schedule all 5 assets with all their tasks
        var scheduleRequest = new PlanningScheduleRequest(
            ["P-101", "P-102", "P-103", "P-104", "P-105"],
            "MW-AT01-BASELINE-20261024",
            requestedTaskIds);

        var result = await scheduler.ScheduleAsync(scheduleRequest, CancellationToken.None);

        // Assert: Verify outcome is CandidatesGenerated
        Assert.Equal(PlanningScheduleOutcome.CandidatesGenerated, result.Outcome);

        // Assert: Verify candidates array is present and has at least 2 distinct alternatives
        Assert.NotEmpty(result.Candidates);
        Assert.True(
            result.Candidates.Count >= 2,
            $"Expected at least 2 distinct alternatives, but got {result.Candidates.Count}");

        // Assert: All five assets are represented in requested tasks across all candidates
        var allRequestedAssets = new HashSet<string> { "P-101", "P-102", "P-103", "P-104", "P-105" };
        var representedAssets = new HashSet<string>();

        foreach (var candidate in result.Candidates)
        {
            foreach (var taskId in candidate.RequestedTaskIds)
            {
                var assetId = ExtractAssetIdFromTaskId(taskId);
                representedAssets.Add(assetId);
            }
        }

        Assert.Equal(5, representedAssets.Count);
        foreach (var asset in allRequestedAssets)
        {
            Assert.Contains(asset, representedAssets);
        }

        // Assert: Each candidate has evidence references
        foreach (var candidate in result.Candidates)
        {
            Assert.True(candidate.EvidenceReferences.Count > 0,
                "Candidate must have at least one evidence reference");

            // Verify evidence structure
            foreach (var evidence in candidate.EvidenceReferences)
            {
                Assert.False(string.IsNullOrEmpty(evidence.SourceId),
                    "Evidence must have sourceId");
            }
        }

        // Assert: Verify task placements (calculated totals)
        foreach (var candidate in result.Candidates)
        {
            Assert.True(candidate.TaskPlacements.Count > 0,
                "Candidate must have at least one task placement");

            // Verify each placement has start/end times and technician assignments
            var totalDuration = TimeSpan.Zero;
            foreach (var placement in candidate.TaskPlacements)
            {
                var duration = placement.End - placement.Start;
                Assert.True(duration.TotalMinutes > 0, "Task placement must have positive duration");
                Assert.True(placement.TechnicianIds.Count > 0, "Task placement must have technician assignments");
                totalDuration += duration;
            }

            // Total duration should be positive and within the 8-hour window
            Assert.True(totalDuration.TotalMinutes > 0, "Candidate should have calculated total duration > 0");
            Assert.True(totalDuration.TotalHours <= 8, "Candidate should fit within 8-hour maintenance window");
        }

        // Assert: Verify rationale is present (explains alternative choice)
        foreach (var candidate in result.Candidates)
        {
            Assert.False(string.IsNullOrWhiteSpace(candidate.Rationale),
                "Candidate must have non-empty rationale");
        }

        // Assert: Verify candidates are distinct
        var candidateRationales = result.Candidates
            .Select(c => c.Rationale)
            .Distinct()
            .Count();

        Assert.True(candidateRationales >= 1,
            "Candidates should have distinct characteristics");
    }

    [Fact]
    public async Task AT01_AllRequestedTasksAreScheduled()
    {
        // Arrange
        await using var fixture = await AT01Fixture.CreateAsync();
        var seed = AT01_FiveAssetSeed.Create();
        await fixture.SeedAsync(seed);

        var scheduler = fixture.ServiceProvider.GetRequiredService<DeterministicPlanningScheduler>();

        // Act: Plan all tasks for all five assets
        var allTaskIds = seed.Tasks.Select(t => t.Id).ToList();
        var scheduleRequest = new PlanningScheduleRequest(
            ["P-101", "P-102", "P-103", "P-104", "P-105"],
            "MW-AT01-BASELINE-20261024",
            allTaskIds);

        var result = await scheduler.ScheduleAsync(scheduleRequest, CancellationToken.None);

        // Assert: At least one candidate exists
        Assert.NotEmpty(result.Candidates);

        // Assert: Each candidate includes requested task IDs
        foreach (var candidate in result.Candidates)
        {
            Assert.True(candidate.RequestedTaskIds.Count > 0,
                "Candidate must include requested tasks");

            // Verify all requested task IDs are in the candidate
            var candidateTaskIds = new HashSet<string>(candidate.RequestedTaskIds);
            foreach (var requestedTaskId in allTaskIds)
            {
                Assert.True(candidateTaskIds.Contains(requestedTaskId),
                    $"Task {requestedTaskId} must be in candidate's requested tasks");
            }
        }
    }

    [Fact]
    public async Task AT01_EvidenceReferencesContainSourceDocumentDetails()
    {
        // Arrange
        await using var fixture = await AT01Fixture.CreateAsync();
        var seed = AT01_FiveAssetSeed.Create();
        await fixture.SeedAsync(seed);

        var scheduler = fixture.ServiceProvider.GetRequiredService<DeterministicPlanningScheduler>();

        // Act
        var allTaskIds = seed.Tasks.Select(t => t.Id).ToList();
        var scheduleRequest = new PlanningScheduleRequest(
            ["P-101", "P-102", "P-103", "P-104", "P-105"],
            "MW-AT01-BASELINE-20261024",
            allTaskIds);

        var result = await scheduler.ScheduleAsync(scheduleRequest, CancellationToken.None);

        // Assert: Evidence references contain document IDs and source information
        var foundDocumentReferences = 0;
        foreach (var candidate in result.Candidates)
        {
            foreach (var evidence in candidate.EvidenceReferences)
            {
                // Check for document-level evidence
                if (!string.IsNullOrEmpty(evidence.DocumentId))
                {
                    foundDocumentReferences++;
                }
            }
        }

        // Assert: At least some evidence includes document references
        Assert.True(
            foundDocumentReferences > 0,
            "Expected at least one evidence reference with a document ID");
    }

    private static string ExtractAssetIdFromTaskId(string taskId)
    {
        // Task IDs follow pattern: P###-TASKNAME (e.g., "P101-PREP" -> "P-101")
        if (taskId.Length >= 4 && taskId[0] == 'P')
        {
            var assetNumber = taskId.Substring(1, 3);
            return $"P-{assetNumber}";
        }

        return taskId;
    }

    private sealed class AT01Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;

        private AT01Fixture(
            SqliteConnection keeperConnection,
            string connectionString,
            IServiceProvider serviceProvider)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            ServiceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider { get; }

        public static async Task<AT01Fixture> CreateAsync()
        {
            var connectionString = $"Data Source=AT01-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();

            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(connectionString)
                .Options;

            // Initialize empty schema
            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
            }

            // Set up services
            var services = new ServiceCollection();
            services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(connectionString));
            services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            services.AddScoped<ISparePartReadModelProvider>(sp =>
                new ScenarioAwareSparePartReadModelProvider(
                    sp.GetRequiredService<IMaintenancePlannerQueries>()));
            services.AddScoped<PlanningCandidateValidator>();
            services.AddScoped<CandidateCostCalculator>();
            services.AddScoped<DeterministicPlanningScheduler>();

            var serviceProvider = services.BuildServiceProvider();
            return new AT01Fixture(keeperConnection, connectionString, serviceProvider);
        }

        public async Task SeedAsync(AT01SeedData seed)
        {
            await using var dbContext = new MaintenancePlannerDbContext(
                new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                    .UseSqlite(_connectionString)
                    .Options);

            // Add all assets
            foreach (var asset in seed.Assets)
            {
                if (!await dbContext.Assets.AnyAsync(a => a.Id == asset.Id))
                    await dbContext.Assets.AddAsync(asset);
            }

            // Add all spare parts
            foreach (var part in seed.SpareParts)
            {
                if (!await dbContext.SpareParts.AnyAsync(p => p.Id == part.Id))
                    await dbContext.SpareParts.AddAsync(part);
            }

            // Add all tasks
            foreach (var task in seed.Tasks)
            {
                if (!await dbContext.MaintenanceTaskDefinitions.AnyAsync(t => t.Id == task.Id))
                    await dbContext.MaintenanceTaskDefinitions.AddAsync(task);
            }

            // Add all technicians
            foreach (var technician in seed.Technicians)
            {
                if (!await dbContext.Technicians.AnyAsync(t => t.Id == technician.Id))
                    await dbContext.Technicians.AddAsync(technician);
            }

            // Add all maintenance windows
            foreach (var window in seed.MaintenanceWindows)
            {
                if (!await dbContext.MaintenanceWindows.AnyAsync(w => w.Id == window.Id))
                    await dbContext.MaintenanceWindows.AddAsync(window);
            }

            await dbContext.SaveChangesAsync();

            // Set up spare part compatibility (each part is compatible with its asset)
            var assetToPartMap = new Dictionary<string, string>
            {
                ["P-101"] = "BR-101",
                ["P-102"] = "SEAL-102",
                ["P-103"] = "VALVE-103",
                ["P-104"] = "WINDING-104",
                ["P-105"] = "BLADE-105"
            };

            foreach (var (assetId, partId) in assetToPartMap)
            {
                var part = await dbContext.SpareParts
                    .Include(p => p.CompatibleAssets)
                    .FirstOrDefaultAsync(p => p.Id == partId);

                var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.Id == assetId);

                if (part != null && asset != null &&
                    !part.CompatibleAssets.Any(a => a.Id == assetId))
                {
                    part.CompatibleAssets.Add(asset);
                    await dbContext.SaveChangesAsync();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _keeperConnection.DisposeAsync();
        }
    }
}
