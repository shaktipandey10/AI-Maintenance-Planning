using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// Regression tests for candidate inventory aggregation bug fix.
///
/// Bug: ExtractRequiredPartsAsync iterated ALL candidates and summed their requirements.
/// With 2 candidates each requiring BR-101 x 1, the method calculated BR-101 x 2.
/// Fix: Process only the candidate selected for approval (by explicit candidateIndex),
/// reserving parts from that candidate's task placements.
/// </summary>
public sealed class CandidateAggregationRegressionTests
{
    // Approval revalidates the selected candidate, so candidates carry schedulable P-101 placements.
    private static readonly string StableIdPlacementsJson = JsonSerializer.Serialize(
        ApprovalTestSupport.StableIdPlacements(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static readonly string LowestCostPlacementsJson = JsonSerializer.Serialize(
        ApprovalTestSupport.LowestCostPlacements(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task TwoCandidatesEachRequiringBR101_OnlySelectedCandidateCountsForInventory()
    {
        // Arrange: Planning job with 2 candidates, each requiring BR-101 x 1
        // Only 1 BR-101 is in stock
        // Act: Approve the second candidate by explicit selection
        // Assert: Approval succeeds (reserves 1 for the selected candidate, not 2 across candidates)

        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        // Create planning job with 2 identical candidates
        await using (var dbContext = fixture.CreateDbContext())
        {
            var planningJob = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = ApprovalTestSupport.RequestPayload(),
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = $$"""
                    {
                      "outcome": "CandidatesGenerated",
                      "result": {
                        "candidates": [
                          {
                            "taskPlacements": {{StableIdPlacementsJson}},
                            "rationale": "Candidate 1: Earliest schedule",
                            "evidenceReferences": [],
                            "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                            "dependencyTaskIds": [],
                            "isLowestCostCandidate": false
                          },
                          {
                            "taskPlacements": {{LowestCostPlacementsJson}},
                            "rationale": "Candidate 2: Lowest cost",
                            "evidenceReferences": [],
                            "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                            "dependencyTaskIds": [],
                            "isLowestCostCandidate": true
                          }
                        ],
                        "assumptions": [],
                        "evidenceReferences": [],
                        "interpretation": {
                          "maintenanceIntent": "Replace bearing",
                          "scope": "P-101",
                          "proposedAssetIds": ["P-101"],
                          "proposedTaskIds": ["P101-BEARING-REPLACEMENT"],
                          "missingInformation": [],
                          "assumptions": [],
                          "scopeResolution": "Resolved",
                          "scopeMappings": []
                        }
                      }
                    }
                    """
            };
            await dbContext.PlanningJobs.AddAsync(planningJob);
            await dbContext.SaveChangesAsync();
        }

        // Act: Approve the second candidate by explicit selection
        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result = await approvalService.ApproveDraftAsync(jobId, candidateIndex: 1, CancellationToken.None);

        // Assert: Approval succeeds (not fails due to double-counting)
        Assert.True(result.Succeeded, "Approval should succeed with only the selected candidate's requirements");
        Assert.NotNull(result.DraftId);

        // Verify inventory was reserved for only 1 bearing, not 2
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.ReservedQuantity);
            Assert.Equal(0m, br101.AvailableQuantity);

            var br101Reservations = await dbContext.Reservations
                .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Single(br101Reservations);
            Assert.Equal(1m, br101Reservations[0].QuantityReserved);
        }
    }

    [Fact]
    public async Task MultipleTasksInSameCandidateRequiringSamePart_QuantitiesSummedCorrectly()
    {
        // Arrange: Single candidate with 2 tasks, each requiring BR-101 x 1 (total 2)
        // Stock has 2 BR-101 available
        // Act: Approve the job
        // Assert: Both bearings are reserved (quantities summed within candidate)

        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        // Create a second task definition that also requires BR-101
        await using (var dbContext = fixture.CreateDbContext())
        {
            // Ensure second task exists
            var task2 = await dbContext.MaintenanceTaskDefinitions.FirstOrDefaultAsync(t => t.Id == "P101-VERIFICATION");
            if (task2 != null && task2.RequiredPartId == null)
            {
                task2.RequiredPartId = "BR-101";
                task2.RequiredPartQuantity = 1m;
                dbContext.MaintenanceTaskDefinitions.Update(task2);
            }

            // Increase BR-101 stock to 2 for this test
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            br101.OnHandQuantity = 2m;
            dbContext.SpareParts.Update(br101);

            var planningJob = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = ApprovalTestSupport.RequestPayload(),
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = $$"""
                    {
                      "outcome": "CandidatesGenerated",
                      "result": {
                        "candidates": [
                          {
                            "taskPlacements": {{StableIdPlacementsJson}},
                            "rationale": "Single candidate with multiple BR-101 requirements",
                            "evidenceReferences": [],
                            "requestedTaskIds": ["P101-BEARING-REPLACEMENT", "P101-VERIFICATION"],
                            "dependencyTaskIds": [],
                            "isLowestCostCandidate": false
                          }
                        ],
                        "assumptions": [],
                        "evidenceReferences": [],
                        "interpretation": {
                          "maintenanceIntent": "Replace bearing and verify",
                          "scope": "P-101",
                          "proposedAssetIds": ["P-101"],
                          "proposedTaskIds": ["P101-BEARING-REPLACEMENT", "P101-VERIFICATION"],
                          "missingInformation": [],
                          "assumptions": [],
                          "scopeResolution": "Resolved",
                          "scopeMappings": []
                        }
                      }
                    }
                    """
            };
            await dbContext.PlanningJobs.AddAsync(planningJob);
            await dbContext.SaveChangesAsync();
        }

        // Act: Approve the planning job
        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        // Assert: Approval succeeds and both bearings are reserved
        Assert.True(result.Succeeded, "Approval should succeed with 2 BR-101 available");
        Assert.NotNull(result.DraftId);

        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            // Both bearings should be reserved (1 + 1)
            Assert.Equal(2m, br101.ReservedQuantity);
            Assert.Equal(0m, br101.AvailableQuantity);

            var br101Reservations = await dbContext.Reservations
                .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                .ToListAsync();
            Assert.Single(br101Reservations);
            Assert.Equal(2m, br101Reservations[0].QuantityReserved);
        }
    }

    [Fact]
    public async Task InsufficientStockForSelectedCandidate_StillReturns409ConflictAfterFix()
    {
        // Arrange: Planning job with 2 candidates, each requiring BR-101 x 1
        // Only 0 BR-101 in stock (none available)
        // Act: Attempt approval of the explicitly selected first candidate
        // Assert: Fails with 409 (because even 1 is needed and we have 0)

        await using var fixture = await ApprovalTestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        // Set BR-101 stock to 0
        await using (var dbContext = fixture.CreateDbContext())
        {
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            br101.OnHandQuantity = 0m;
            dbContext.SpareParts.Update(br101);

            var planningJob = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = ApprovalTestSupport.RequestPayload(),
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ResultPayload = $$"""
                    {
                      "outcome": "CandidatesGenerated",
                      "result": {
                        "candidates": [
                          {
                            "taskPlacements": {{StableIdPlacementsJson}},
                            "rationale": "Candidate 1",
                            "evidenceReferences": [],
                            "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                            "dependencyTaskIds": [],
                            "isLowestCostCandidate": false
                          },
                          {
                            "taskPlacements": {{LowestCostPlacementsJson}},
                            "rationale": "Candidate 2",
                            "evidenceReferences": [],
                            "requestedTaskIds": ["P101-BEARING-REPLACEMENT"],
                            "dependencyTaskIds": [],
                            "isLowestCostCandidate": true
                          }
                        ],
                        "assumptions": [],
                        "evidenceReferences": [],
                        "interpretation": {
                          "maintenanceIntent": "Replace bearing",
                          "scope": "P-101",
                          "proposedAssetIds": ["P-101"],
                          "proposedTaskIds": ["P101-BEARING-REPLACEMENT"],
                          "missingInformation": [],
                          "assumptions": [],
                          "scopeResolution": "Resolved",
                          "scopeMappings": []
                        }
                      }
                    }
                    """
            };
            await dbContext.PlanningJobs.AddAsync(planningJob);
            await dbContext.SaveChangesAsync();
        }

        // Act: Attempt approval of the explicitly selected first candidate
        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();
        var result = await approvalService.ApproveDraftAsync(jobId, candidateIndex: 0, CancellationToken.None);

        // Assert: Fails with insufficient inventory
        Assert.False(result.Succeeded);
        Assert.Contains("Insufficient inventory", result.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
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
            var connectionString =
                $"Data Source=CandidateAgg-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();
            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(keeperConnection)
                .Options;

            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);

                var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
                Assert.Equal(1m, br101.OnHandQuantity);
                Assert.Equal(0m, br101.ReservedQuantity);
            }

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
            builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            builder.Services.AddApprovalServices();

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
