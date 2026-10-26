using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT11PlanProgressEndpointTests
{
    // AT11: Services supporting endpoints for progress tracking, completion, and export

    [Fact]
    public async Task AT11_MarkTaskCompleteService_ReturnsSuccessForApprovedDraft()
    {
        // Service called by POST /api/drafts/{draftId}/mark-task-complete
        await using var fixture = await EndpointServiceFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();
        var result = await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task AT11_MarkTaskCompleteService_RejectsUnapprovedDraft()
    {
        // Service rejects completion on unapproved draft (endpoint returns 409)
        await using var fixture = await EndpointServiceFixture.CreateAsync();
        var (_, _) = await fixture.CreateApprovedDraftWithReservations();
        var pendingJobId = Guid.NewGuid();
        var pendingDraftId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            var pendingJob = new PlanningJob
            {
                Id = pendingJobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Test","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            var pending = new Draft { Id = pendingDraftId, PlanningJobId = pendingJobId, Status = DraftStatus.Pending };
            await dbContext.PlanningJobs.AddAsync(pendingJob);
            await dbContext.Drafts.AddAsync(pending);
            await dbContext.SaveChangesAsync();
        }

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();
        var result = await progressService.MarkTaskCompleteAsync(pendingDraftId, "BR-101", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("approved", result.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AT11_GetPlanProgressService_ReturnsProgressWithRemainingWork()
    {
        // Service called by GET /api/drafts/{draftId}/progress
        await using var fixture = await EndpointServiceFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();
        var progress = await progressService.GetPlanProgressAsync(draftId, CancellationToken.None);

        Assert.Equal(3m, progress.TotalRequired);
        Assert.Equal(0m, progress.CompletedQuantity);
        Assert.Equal(3m, progress.RemainingQuantity);
        Assert.NotEmpty(progress.Tasks);
    }

    [Fact]
    public async Task AT11_GetPlanProgressService_ReflectsCompletedTasks()
    {
        // Progress endpoint reflects task completion changes
        await using var fixture = await EndpointServiceFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();

        // Mark one task complete
        await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);

        // Verify progress reflects the change
        var progress = await progressService.GetPlanProgressAsync(draftId, CancellationToken.None);
        Assert.Equal(3m, progress.TotalRequired);
        Assert.Equal(1m, progress.CompletedQuantity);
        Assert.Equal(2m, progress.RemainingQuantity);
    }

    [Fact]
    public async Task AT11_ExportJsonService_ReturnsValidJson()
    {
        // Service called by GET /api/drafts/{draftId}/export/json
        await using var fixture = await EndpointServiceFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();
        var json = await exportService.ExportAsJsonAsync(draftId, CancellationToken.None);

        Assert.NotEmpty(json);
        // JsonSerializerDefaults.Web uses camelCase
        Assert.Contains("draftId", json);
        Assert.Contains("progress", json);
        Assert.Contains("totalRequired", json);

        // Verify valid JSON structure
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }

    [Fact]
    public async Task AT11_ExportJsonService_RejectsUnapprovedDraft()
    {
        // Export service rejects unapproved draft (endpoint returns 409)
        await using var fixture = await EndpointServiceFixture.CreateAsync();
        var (_, _) = await fixture.CreateApprovedDraftWithReservations();
        var pendingJobId = Guid.NewGuid();
        var pendingDraftId = Guid.NewGuid();

        await using (var dbContext = fixture.CreateDbContext())
        {
            var pendingJob = new PlanningJob
            {
                Id = pendingJobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Test","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            var pending = new Draft { Id = pendingDraftId, PlanningJobId = pendingJobId, Status = DraftStatus.Pending };
            await dbContext.PlanningJobs.AddAsync(pendingJob);
            await dbContext.Drafts.AddAsync(pending);
            await dbContext.SaveChangesAsync();
        }

        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exportService.ExportAsJsonAsync(pendingDraftId, CancellationToken.None));
        Assert.Contains("approved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AT11_ExportJsonService_IncludesCompletionData()
    {
        // JSON export includes completion status
        await using var fixture = await EndpointServiceFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();
        await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);

        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();
        var json = await exportService.ExportAsJsonAsync(draftId, CancellationToken.None);

        // JsonSerializerDefaults.Web uses camelCase
        Assert.Contains("completedQuantity", json);
        Assert.Contains("remainingQuantity", json);
        // Verify completion was tracked (1 of 3 completed)
        var doc = JsonDocument.Parse(json);
        var completedQty = doc.RootElement.GetProperty("progress").GetProperty("completedQuantity").GetDouble();
        Assert.Equal(1.0, completedQty);
    }

    [Fact]
    public async Task AT11_ExportPdfService_ReturnsValidPdfBytes()
    {
        // Service called by GET /api/drafts/{draftId}/export/pdf
        await using var fixture = await EndpointServiceFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();
        var pdfBytes = await exportService.ExportAsPdfAsync(draftId, CancellationToken.None);

        Assert.NotEmpty(pdfBytes);
        // PDF files start with %PDF magic bytes
        Assert.Equal(0x25, pdfBytes[0]); // %
        Assert.Equal(0x50, pdfBytes[1]); // P
        Assert.Equal(0x44, pdfBytes[2]); // D
        Assert.Equal(0x46, pdfBytes[3]); // F
    }

    [Fact]
    public async Task AT11_ExportPdfAndJsonServices_BothReturnSameRevision()
    {
        // Both PDF and JSON exports represent the same approved revision
        await using var fixture = await EndpointServiceFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();
        var json = await exportService.ExportAsJsonAsync(draftId, CancellationToken.None);
        var pdfBytes = await exportService.ExportAsPdfAsync(draftId, CancellationToken.None);

        // Both should be non-empty
        Assert.NotEmpty(json);
        Assert.NotEmpty(pdfBytes);

        // JSON should be valid and reference the same draft (camelCase)
        var doc = JsonDocument.Parse(json);
        var exportedDraftId = doc.RootElement.GetProperty("draftId").GetString();
        Assert.Equal(draftId.ToString(), exportedDraftId);
    }

    private sealed class EndpointServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;
        private readonly IServiceProvider _serviceProvider;

        private EndpointServiceFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            IServiceProvider serviceProvider)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            _serviceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider => _serviceProvider;

        public static async Task<EndpointServiceFixture> CreateAsync()
        {
            var connectionString = $"Data Source=AT11EP-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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
            services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(connectionString));
            services.AddScoped<IPlanProgressService, PlanProgressService>();
            services.AddScoped<IPlanExportService, PlanExportService>();
            var serviceProvider = services.BuildServiceProvider();

            return new EndpointServiceFixture(keeperConnection, connectionString, serviceProvider);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        public async Task<(Guid DraftId, Guid JobId)> CreateApprovedDraftWithReservations()
        {
            var jobId = Guid.NewGuid();
            var draftId = Guid.NewGuid();

            await using var dbContext = CreateDbContext();

            var job = new PlanningJob
            {
                Id = jobId,
                OperationId = Guid.NewGuid(),
                RequestPayload = """{"requestText":"Test","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            var draft = new Draft
            {
                Id = draftId,
                PlanningJobId = jobId,
                Status = DraftStatus.Approved,
                ApprovedAtUtc = DateTimeOffset.UtcNow
            };

            var reservations = new[]
            {
                new Reservation { Id = Guid.NewGuid(), DraftId = draftId, SparePartId = "BR-101", QuantityReserved = 1m, Status = ReservationStatus.Confirmed, ConfirmedAtUtc = DateTimeOffset.UtcNow },
                new Reservation { Id = Guid.NewGuid(), DraftId = draftId, SparePartId = "BR-101", QuantityReserved = 1m, Status = ReservationStatus.Confirmed, ConfirmedAtUtc = DateTimeOffset.UtcNow },
                new Reservation { Id = Guid.NewGuid(), DraftId = draftId, SparePartId = "BR-101", QuantityReserved = 1m, Status = ReservationStatus.Confirmed, ConfirmedAtUtc = DateTimeOffset.UtcNow }
            };

            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.Drafts.AddAsync(draft);
            await dbContext.Reservations.AddRangeAsync(reservations);
            await dbContext.SaveChangesAsync();

            return (draftId, jobId);
        }

        public async ValueTask DisposeAsync()
        {
            await _keeperConnection.DisposeAsync();
        }
    }
}
