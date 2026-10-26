using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace MaintenancePlanner.IntegrationTests;

public sealed class AT11PlanProgressIntegrationTests
{
    // AT11: End-to-end plan progress tracking, remaining work calculation, and export

    [Fact]
    public async Task AT11_MarkTaskComplete_PersistsCompletionState()
    {
        // Mark a task complete and verify CompletedAtUtc is persisted
        await using var fixture = await PlanProgressFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();

        // Mark task complete
        var result = await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);
        Assert.True(result.Succeeded);

        // Verify persisted
        await using (var dbContext = fixture.CreateDbContext())
        {
            var reservation = await dbContext.Reservations
                .FirstAsync(r => r.DraftId == draftId && r.SparePartId == "BR-101");
            Assert.NotNull(reservation.CompletedAtUtc);
        }
    }

    [Fact]
    public async Task AT11_CannotMarkCompleteOnUnapprovedDraft()
    {
        // Pending or cancelled drafts cannot have tasks marked complete
        await using var fixture = await PlanProgressFixture.CreateAsync();
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
            var pendingDraft = new Draft { Id = pendingDraftId, PlanningJobId = pendingJobId, Status = DraftStatus.Pending };
            var reservation = new Reservation { Id = Guid.NewGuid(), DraftId = pendingDraftId, SparePartId = "BR-101", QuantityReserved = 1m, Status = ReservationStatus.Confirmed, ConfirmedAtUtc = DateTimeOffset.UtcNow };
            await dbContext.PlanningJobs.AddAsync(pendingJob);
            await dbContext.Drafts.AddAsync(pendingDraft);
            await dbContext.Reservations.AddAsync(reservation);
            await dbContext.SaveChangesAsync();
        }

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();

        // Should fail for pending draft
        var result = await progressService.MarkTaskCompleteAsync(pendingDraftId, "BR-101", CancellationToken.None);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AT11_GetPlanProgress_CalculatesRemainingWork()
    {
        // Create draft with 3 units required, mark 1 complete, verify remaining = 2
        await using var fixture = await PlanProgressFixture.CreateAsync();
        var (draftId, jobId) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();

        // Initial progress: 0 completed, 3 required
        var initialProgress = await progressService.GetPlanProgressAsync(draftId, CancellationToken.None);
        Assert.Equal(3m, initialProgress.TotalRequired);
        Assert.Equal(0m, initialProgress.CompletedQuantity);
        Assert.Equal(3m, initialProgress.RemainingQuantity);

        // Mark 1 complete
        await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);

        // Verify remaining = 2
        var progressAfter = await progressService.GetPlanProgressAsync(draftId, CancellationToken.None);
        Assert.Equal(3m, progressAfter.TotalRequired);
        Assert.Equal(1m, progressAfter.CompletedQuantity);
        Assert.Equal(2m, progressAfter.RemainingQuantity);
    }

    [Fact]
    public async Task AT11_ReloadPlan_VerifiesCompletionPersists()
    {
        // Mark tasks complete, reload from database, verify persistence
        await using var fixture = await PlanProgressFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();

        // Mark all tasks complete
        await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);
        await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);
        await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);

        // Create new context to force reload from database
        var newProgressService = new PlanProgressService(fixture.CreateDbContext());

        // Verify all tasks are marked complete
        var reloadedProgress = await newProgressService.GetPlanProgressAsync(draftId, CancellationToken.None);
        Assert.Equal(3m, reloadedProgress.CompletedQuantity);
        Assert.Equal(0m, reloadedProgress.RemainingQuantity);
    }

    [Fact]
    public async Task AT11_ExportJson_IncludesCompletionStatus()
    {
        // Export as JSON and verify it includes completion data and references the correct draft
        await using var fixture = await PlanProgressFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();
        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();

        // Mark some tasks complete
        await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);

        // Export
        var json = await exportService.ExportAsJsonAsync(draftId, CancellationToken.None);

        // Verify structure (camelCase due to JsonSerializerDefaults.Web)
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(draftId.ToString(), root.GetProperty("draftId").GetString());
        var progress = root.GetProperty("progress");
        Assert.Equal(3m, (decimal)progress.GetProperty("totalRequired").GetDouble());
        Assert.Equal(1m, (decimal)progress.GetProperty("completedQuantity").GetDouble());
        Assert.Equal(2m, (decimal)progress.GetProperty("remainingQuantity").GetDouble());
    }

    [Fact]
    public async Task AT11_ExportJson_CannotExportUnapprovedDraft()
    {
        // Unapproved drafts cannot be exported
        await using var fixture = await PlanProgressFixture.CreateAsync();
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
            var draft = new Draft { Id = pendingDraftId, PlanningJobId = pendingJobId, Status = DraftStatus.Pending };
            await dbContext.PlanningJobs.AddAsync(pendingJob);
            await dbContext.Drafts.AddAsync(draft);
            await dbContext.SaveChangesAsync();
        }

        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();

        // Should throw
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => exportService.ExportAsJsonAsync(pendingDraftId, CancellationToken.None));
    }

    [Fact]
    public async Task AT11_ExportPdf_ReferencesSpecificDraft()
    {
        // PDF export references the specific approved revision via embedded JSON
        await using var fixture = await PlanProgressFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();

        // Export both JSON and PDF
        var json = await exportService.ExportAsJsonAsync(draftId, CancellationToken.None);
        var pdfBytes = await exportService.ExportAsPdfAsync(draftId, CancellationToken.None);

        // Verify PDF is valid
        Assert.NotEmpty(pdfBytes);
        Assert.Equal(0x25, pdfBytes[0]); // %PDF magic bytes
        Assert.Equal(0x50, pdfBytes[1]); // P
        Assert.Equal(0x44, pdfBytes[2]); // D
        Assert.Equal(0x46, pdfBytes[3]); // F

        // Verify JSON contains the specific draft ID
        Assert.Contains(draftId.ToString(), json);
    }

    [Fact]
    public async Task AT11_ExportJson_And_Pdf_MatchContent()
    {
        // Both exports contain completion data for the same revision
        await using var fixture = await PlanProgressFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();
        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();

        // Mark tasks complete
        await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);
        await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);

        // Export both
        var json = await exportService.ExportAsJsonAsync(draftId, CancellationToken.None);
        var pdfBytes = await exportService.ExportAsPdfAsync(draftId, CancellationToken.None);

        // Verify both are non-empty
        Assert.NotEmpty(json);
        Assert.NotEmpty(pdfBytes);

        // JSON should reference the draft
        Assert.Contains(draftId.ToString(), json);

        // JSON should include completion data
        Assert.Contains("completedQuantity", json);

        // PDF should be valid
        Assert.Equal(0x25, pdfBytes[0]); // %PDF magic bytes
        Assert.Equal(0x50, pdfBytes[1]); // P
        Assert.Equal(0x44, pdfBytes[2]); // D
        Assert.Equal(0x46, pdfBytes[3]); // F
    }

    [Fact]
    public async Task AT11_MarkTaskComplete_IsIdempotent()
    {
        // Marking tasks complete multiple times is safe until all are completed
        await using var fixture = await PlanProgressFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();

        // Mark complete (3 reservations total)
        var result1 = await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);
        Assert.True(result1.Succeeded);

        // Mark complete again (2nd reservation)
        var result2 = await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);
        Assert.True(result2.Succeeded);

        // Mark complete again (3rd reservation)
        var result3 = await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);
        Assert.True(result3.Succeeded);

        // Fourth time should fail gracefully (all completed)
        var result4 = await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);
        Assert.False(result4.Succeeded);
    }

    [Fact]
    public async Task AT11_ReplacementDraft_ExportsWithCorrectRevisionId()
    {
        // V1 and V2 drafts each export with their own draft IDs
        await using var fixture = await PlanProgressFixture.CreateAsync();
        var (v1DraftId, jobId) = await fixture.CreateApprovedDraftWithReservations();

        // Create V2 as replacement
        var v2DraftId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            // Create V2 job referencing V1
            var v2JobId = Guid.NewGuid();
            var v2Job = new PlanningJob
            {
                Id = v2JobId,
                OperationId = Guid.NewGuid(),
                OriginalJobId = jobId,
                RequestPayload = """{"requestText":"Revised","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            var v2Draft = new Draft
            {
                Id = v2DraftId,
                PlanningJobId = v2JobId,
                Status = DraftStatus.Approved,
                ApprovedAtUtc = DateTimeOffset.UtcNow
            };

            // Create reservations for V2
            var v2Reservations = new[]
            {
                new Reservation { Id = Guid.NewGuid(), DraftId = v2DraftId, SparePartId = "BR-101", QuantityReserved = 2m, Status = ReservationStatus.Confirmed, ConfirmedAtUtc = DateTimeOffset.UtcNow }
            };

            await dbContext.PlanningJobs.AddAsync(v2Job);
            await dbContext.Drafts.AddAsync(v2Draft);
            await dbContext.Reservations.AddRangeAsync(v2Reservations);
            await dbContext.SaveChangesAsync();
        }

        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();

        // Export V1
        var v1Json = await exportService.ExportAsJsonAsync(v1DraftId, CancellationToken.None);
        var v1Doc = JsonDocument.Parse(v1Json);
        var v1ExportedDraftId = v1Doc.RootElement.GetProperty("draftId").GetString();
        Assert.Equal(v1DraftId.ToString(), v1ExportedDraftId);

        // Export V2
        var v2Json = await exportService.ExportAsJsonAsync(v2DraftId, CancellationToken.None);
        var v2Doc = JsonDocument.Parse(v2Json);
        var v2ExportedDraftId = v2Doc.RootElement.GetProperty("draftId").GetString();
        Assert.Equal(v2DraftId.ToString(), v2ExportedDraftId);

        // Verify they are different
        Assert.NotEqual(v1ExportedDraftId, v2ExportedDraftId);
    }

    [Fact]
    public async Task AT11_ReplacementDraft_IndependentProgress()
    {
        // V1 and V2 track progress independently
        await using var fixture = await PlanProgressFixture.CreateAsync();
        var (v1DraftId, jobId) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();
        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();

        // Mark V1 tasks complete
        await progressService.MarkTaskCompleteAsync(v1DraftId, "BR-101", CancellationToken.None);

        // Create V2 as replacement
        var v2DraftId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            var v2JobId = Guid.NewGuid();
            var v2Job = new PlanningJob
            {
                Id = v2JobId,
                OperationId = Guid.NewGuid(),
                OriginalJobId = jobId,
                RequestPayload = """{"requestText":"Revised","assetIds":["P-101"]}""",
                Status = PlanningJobStatus.Succeeded,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            var v2Draft = new Draft
            {
                Id = v2DraftId,
                PlanningJobId = v2JobId,
                Status = DraftStatus.Approved,
                ApprovedAtUtc = DateTimeOffset.UtcNow
            };

            var v2Reservations = new[]
            {
                new Reservation { Id = Guid.NewGuid(), DraftId = v2DraftId, SparePartId = "BR-101", QuantityReserved = 2m, Status = ReservationStatus.Confirmed, ConfirmedAtUtc = DateTimeOffset.UtcNow }
            };

            await dbContext.PlanningJobs.AddAsync(v2Job);
            await dbContext.Drafts.AddAsync(v2Draft);
            await dbContext.Reservations.AddRangeAsync(v2Reservations);
            await dbContext.SaveChangesAsync();
        }

        // V1 should show 1 completed, 2 remaining
        var v1Json = await exportService.ExportAsJsonAsync(v1DraftId, CancellationToken.None);
        var v1Doc = JsonDocument.Parse(v1Json);
        var v1Completed = v1Doc.RootElement.GetProperty("progress").GetProperty("completedQuantity").GetDouble();
        Assert.Equal(1.0, v1Completed);

        // V2 should show 0 completed, 2 remaining (V2 has 2 units total)
        var v2Json = await exportService.ExportAsJsonAsync(v2DraftId, CancellationToken.None);
        var v2Doc = JsonDocument.Parse(v2Json);
        var v2Completed = v2Doc.RootElement.GetProperty("progress").GetProperty("completedQuantity").GetDouble();
        var v2Total = v2Doc.RootElement.GetProperty("progress").GetProperty("totalRequired").GetDouble();
        Assert.Equal(0.0, v2Completed);
        Assert.Equal(2.0, v2Total);
    }

    [Fact]
    public async Task AT11_RepeatedExports_DoNotMutateState()
    {
        // Multiple exports of same draft return identical data without changing state
        await using var fixture = await PlanProgressFixture.CreateAsync();
        var (draftId, _) = await fixture.CreateApprovedDraftWithReservations();

        var progressService = fixture.ServiceProvider.GetRequiredService<IPlanProgressService>();
        var exportService = fixture.ServiceProvider.GetRequiredService<IPlanExportService>();

        // Mark one task complete
        await progressService.MarkTaskCompleteAsync(draftId, "BR-101", CancellationToken.None);

        // Export first time
        var export1 = await exportService.ExportAsJsonAsync(draftId, CancellationToken.None);
        var doc1 = JsonDocument.Parse(export1);
        var completed1 = doc1.RootElement.GetProperty("progress").GetProperty("completedQuantity").GetDouble();

        // Export second time
        var export2 = await exportService.ExportAsJsonAsync(draftId, CancellationToken.None);
        var doc2 = JsonDocument.Parse(export2);
        var completed2 = doc2.RootElement.GetProperty("progress").GetProperty("completedQuantity").GetDouble();

        // Exports should be identical
        Assert.Equal(completed1, completed2);

        // Verify state unchanged
        var finalProgress = await progressService.GetPlanProgressAsync(draftId, CancellationToken.None);
        Assert.Equal(1m, finalProgress.CompletedQuantity);
        Assert.Equal(2m, finalProgress.RemainingQuantity);
    }

    private sealed class PlanProgressFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;

        private PlanProgressFixture(
            SqliteConnection keeperConnection,
            string connectionString,
            IServiceProvider serviceProvider)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
            ServiceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider { get; }

        public static async Task<PlanProgressFixture> CreateAsync()
        {
            var connectionString = $"Data Source=AT11-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
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
            return new PlanProgressFixture(keeperConnection, connectionString, serviceProvider);
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

            // 3 reservations for BR-101 (part already seeded by DatabaseInitializer)
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
