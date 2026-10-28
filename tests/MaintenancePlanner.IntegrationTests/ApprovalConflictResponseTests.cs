using System.Text.Json;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class ApprovalConflictResponseTests
{
    [Fact]
    public async Task ApproveDraft_InsufficientInventory_ReturnsStructuredConflictData()
    {
        // Arrange: Set up scenario with insufficient inventory
        await using var fixture = await TestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        // Create planning job requiring BR-101
        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithBR101RequirementAsync(dbContext, jobId);
            // Reduce BR-101 inventory to 0
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            br101.OnHandQuantity = 0;
            await dbContext.SaveChangesAsync();
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Act: Attempt to approve the draft
        var result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        // Assert: Verify structured conflict data
        Assert.False(result.Succeeded);
        Assert.NotNull(result.ConflictData);
        Assert.Equal("SparePart", result.ConflictData.ResourceType);
        Assert.Equal("INSUFFICIENT_INVENTORY", result.ConflictData.Reason);
        Assert.Equal("BR-101", result.ConflictData.ResourceId);
        Assert.Contains("Insufficient", result.FailureReason);
    }

    [Fact]
    public async Task ApproveDraft_DraftCancelled_ReturnsStructuredConflictData()
    {
        // Arrange: Create a cancelled draft and try to re-approve
        await using var fixture = await TestFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        Guid draftId;

        // Create and cancel a draft
        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithBR101RequirementAsync(dbContext, jobId);

            var draft = new Draft
            {
                Id = Guid.NewGuid(),
                PlanningJobId = jobId,
                Status = DraftStatus.Cancelled
            };
            await dbContext.Drafts.AddAsync(draft);
            await dbContext.SaveChangesAsync();
            draftId = draft.Id;
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Act: Attempt to approve the cancelled draft
        var result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        // Assert: Verify structured conflict data
        Assert.False(result.Succeeded);
        Assert.NotNull(result.ConflictData);
        Assert.Equal("Draft", result.ConflictData.ResourceType);
        Assert.Equal("DRAFT_CANCELLED", result.ConflictData.Reason);
        Assert.Equal(draftId.ToString("D"), result.ConflictData.ResourceId);
        Assert.Contains("cancelled", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApproveDraft_PlanningJobNotFound_ReturnsStructuredConflictData()
    {
        // Arrange: Use non-existent planning job ID
        await using var fixture = await TestFixture.CreateAsync();
        var nonExistentJobId = Guid.NewGuid();

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Act: Attempt to approve non-existent planning job
        var result = await approvalService.ApproveDraftAsync(nonExistentJobId, CancellationToken.None);

        // Assert: Verify structured conflict data
        Assert.False(result.Succeeded);
        Assert.NotNull(result.ConflictData);
        Assert.Equal("PlanningJob", result.ConflictData.ResourceType);
        Assert.Equal("PLANNING_JOB_NOT_FOUND", result.ConflictData.Reason);
        Assert.Equal(nonExistentJobId.ToString("D"), result.ConflictData.ResourceId);
        Assert.Contains("not found", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelDraft_InvalidState_ReturnsStructuredConflictData()
    {
        // Arrange: Create a draft in Pending state (not Approved)
        await using var fixture = await TestFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        Guid draftId;

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithBR101RequirementAsync(dbContext, jobId);

            var draft = new Draft
            {
                Id = Guid.NewGuid(),
                PlanningJobId = jobId,
                Status = DraftStatus.Pending
            };
            await dbContext.Drafts.AddAsync(draft);
            await dbContext.SaveChangesAsync();
            draftId = draft.Id;
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Act: Attempt to cancel draft in Pending state
        var result = await approvalService.CancelDraftAsync(draftId, CancellationToken.None);

        // Assert: Verify structured conflict data
        Assert.False(result.Succeeded);
        Assert.NotNull(result.ConflictData);
        Assert.Equal("Draft", result.ConflictData.ResourceType);
        Assert.Equal("INVALID_DRAFT_STATE", result.ConflictData.Reason);
        Assert.Equal(draftId.ToString("D"), result.ConflictData.ResourceId);
        Assert.Contains("not in approved state", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelDraft_NotFound_ReturnsFailureWithoutConflictData()
    {
        // Arrange: Use non-existent draft ID
        await using var fixture = await TestFixture.CreateAsync();
        var nonExistentDraftId = Guid.NewGuid();

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Act: Attempt to cancel non-existent draft
        var result = await approvalService.CancelDraftAsync(nonExistentDraftId, CancellationToken.None);

        // Assert: Verify it fails but without conflict data (will be handled as 404 in HTTP layer)
        Assert.False(result.Succeeded);
        Assert.Equal("Draft not found", result.FailureReason);
        // For 404, conflict data is not populated
    }

    [Fact]
    public async Task ApproveDraft_Success_ReturnsSuccessWithoutConflictData()
    {
        // Arrange: Create valid planning job with inventory
        await using var fixture = await TestFixture.CreateAsync();
        var jobId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            await CreatePlanningJobWithBR101RequirementAsync(dbContext, jobId);
        }

        var approvalService = fixture.ServiceProvider.GetRequiredService<IDraftApprovalService>();

        // Act: Approve the draft
        var result = await approvalService.ApproveDraftAsync(jobId, CancellationToken.None);

        // Assert: Verify success without conflict data
        Assert.True(result.Succeeded);
        Assert.NotNull(result.DraftId);
        Assert.Null(result.ConflictData);
    }

    // Approval revalidates the candidate, so the job carries a schedulable P-101 plan requiring one BR-101.
    private static Task CreatePlanningJobWithBR101RequirementAsync(
        MaintenancePlannerDbContext dbContext,
        Guid jobId) =>
        ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId);

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;

        private TestFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            IServiceProvider serviceProvider)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            ServiceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider { get; }

        public static async Task<TestFixture> CreateAsync()
        {
            var connectionString =
                $"Data Source=ApprovalConflict-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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

            var services = new ServiceCollection();
            services.AddDbContext<MaintenancePlannerDbContext>(db =>
                db.UseSqlite(connectionString));
            services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            services.AddApprovalServices();
            var serviceProvider = services.BuildServiceProvider();

            return new TestFixture(keeperConnection, connectionString, serviceProvider);
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
