using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT10ErrorHandlingIntegrationTests
{
    // AT10 Integration: Job state consistency and approval rejection after errors
    // - Verify failed job results prevent approval
    // - Verify job state persists correctly after errors
    // - Verify result outcome types block approval appropriately

    [Fact]
    public async Task AT10_FailedJobResultCannotBeApproved()
    {
        // A job with failed result (InvalidInput) should not be approvable
        await using var fixture = await ErrorHandlingFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        // Create job with invalid result (no task mapping)
        var invalidResultPayload = $$"""
        {
          "outcome": "InvalidInput",
          "result": {
            "violations": [
              {
                "violationCode": "INVALID_TASK_SCOPE",
                "description": "Proposed task IDs must be nonblank and unique.",
                "taskId": null,
                "resourceId": null
              }
            ],
            "assumptions": [],
            "evidenceReferences": [],
            "interpretation": {
              "maintenanceIntent": "Test",
              "scope": "P-101",
              "proposedAssetIds": ["P-101"],
              "proposedTaskIds": [],
              "missingInformation": [],
              "assumptions": [],
              "scopeResolution": "Resolved",
              "scopeMappings": []
            }
          }
        }
        """;

        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Plan work.","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                ResultPayload = invalidResultPayload,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Try to approve the invalid result
        var approvalResult = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        // Should fail because result has no required parts (invalid outcome)
        Assert.False(approvalResult.Succeeded);
        Assert.NotNull(approvalResult.ConflictData);
    }

    [Fact]
    public async Task AT10_InfeasibleResultCannotBeApproved()
    {
        // A job with Infeasible result should not be approvable
        await using var fixture = await ErrorHandlingFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        var infeasibleResultPayload = $$"""
        {
          "outcome": "Infeasible",
          "result": {
            "violations": [
              {
                "violationCode": "PART_UNAVAILABLE",
                "description": "Spare part BR-101 has 0 available but 1 is required.",
                "taskId": "P101-BEARING-REPLACEMENT",
                "resourceId": "BR-101"
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
              "scopeMappings": [{"taskId": "P101-BEARING-REPLACEMENT", "assetId": "P-101"}]
            }
          }
        }
        """;

        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Plan bearing replacement.","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                ResultPayload = infeasibleResultPayload,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Try to approve the infeasible result
        var approvalResult = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        // Should fail because result is Infeasible (not CandidatesGenerated)
        Assert.False(approvalResult.Succeeded);
    }

    [Fact]
    public async Task AT10_DataUnavailableResultCannotBeApproved()
    {
        // A job with DataUnavailable result should not be approvable
        await using var fixture = await ErrorHandlingFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        var dataUnavailablePayload = $$"""
        {
          "outcome": "DataUnavailable",
          "result": {
            "violations": [
              {
                "violationCode": "PART_DATA_UNAVAILABLE",
                "description": "Inventory data does not include required part BR-101.",
                "taskId": "P101-BEARING-REPLACEMENT",
                "resourceId": "BR-101"
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
              "scopeMappings": [{"taskId": "P101-BEARING-REPLACEMENT", "assetId": "P-101"}]
            }
          }
        }
        """;

        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Plan bearing replacement.","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                ResultPayload = dataUnavailablePayload,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Try to approve the data unavailable result
        var approvalResult = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        // Should fail because result is DataUnavailable
        Assert.False(approvalResult.Succeeded);
    }

    [Fact]
    public async Task AT10_ClarificationRequiredResultCannotBeApproved()
    {
        // A job with ClarificationRequired result should not be approvable
        await using var fixture = await ErrorHandlingFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        var clarificationPayload = $$"""
        {
          "outcome": "ClarificationRequired",
          "result": {
            "clarificationQuestions": ["Which bearing model is required?"],
            "assumptions": [],
            "evidenceReferences": [],
            "interpretation": {
              "maintenanceIntent": "Replace bearing",
              "scope": "P-101",
              "proposedAssetIds": ["P-101"],
              "proposedTaskIds": ["P101-BEARING-REPLACEMENT"],
              "missingInformation": ["Which bearing model is required?"],
              "assumptions": [],
              "scopeResolution": "Ambiguous",
              "scopeMappings": [{"taskId": "P101-BEARING-REPLACEMENT", "assetId": "P-101"}]
            }
          }
        }
        """;

        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Plan bearing replacement.","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                ResultPayload = clarificationPayload,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Try to approve the clarification result
        var approvalResult = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        // Should fail because result is ClarificationRequired
        Assert.False(approvalResult.Succeeded);
    }

    [Fact]
    public async Task AT10_JobStateConsistentAfterFailure()
    {
        // Job state should remain consistent after approval failure
        await using var fixture = await ErrorHandlingFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var requestPayload = """{"requestText":"Plan bearing replacement.","assetIds":["P-101"]}""";

        var infeasibleResultPayload = $$"""
        {
          "outcome": "Infeasible",
          "result": {
            "violations": [],
            "assumptions": [],
            "evidenceReferences": [],
            "interpretation": null
          }
        }
        """;

        // Create and mark job as succeeded with infeasible result
        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = operationId,
                RequestPayload = requestPayload,
                Status = PlanningJobStatus.Succeeded,
                ResultPayload = infeasibleResultPayload,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Try to approve
        var approvalResult = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);
        Assert.False(approvalResult.Succeeded);

        // Verify job state unchanged
        await using (var dbContext = fixture.CreateDbContext())
        {
            var job = await dbContext.PlanningJobs.SingleAsync(j => j.Id == jobId);

            // State should be unchanged
            Assert.Equal(jobId, job.Id);
            Assert.Equal(operationId, job.OperationId);
            Assert.Equal(requestPayload, job.RequestPayload);
            Assert.Equal(PlanningJobStatus.Succeeded, job.Status);
            Assert.Equal(infeasibleResultPayload, job.ResultPayload);

            // No drafts should have been created
            var drafts = await dbContext.Drafts
                .Where(d => d.PlanningJobId == jobId)
                .ToListAsync();
            Assert.Empty(drafts);
        }
    }

    private sealed class ErrorHandlingFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;

        private ErrorHandlingFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            IServiceProvider serviceProvider)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            ServiceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider { get; }

        public static async Task<ErrorHandlingFixture> CreateAsync()
        {
            var connectionString =
                $"Data Source=AT10Int-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();
            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            var services = new ServiceCollection();
            services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
            services.AddApprovalServices();

            var serviceProvider = services.BuildServiceProvider();

            return new ErrorHandlingFixture(keeperConnection, connectionString, serviceProvider);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        public async ValueTask DisposeAsync()
        {
            await _keeperConnection.DisposeAsync();
        }
    }
}
