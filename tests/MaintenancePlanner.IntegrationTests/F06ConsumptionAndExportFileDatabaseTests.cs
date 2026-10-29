using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using iTextSharp.text.pdf;
using PdfTextExtractor = iTextSharp.text.pdf.parser.PdfTextExtractor;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// F06 Part C (FR08, AT11): completing a task consumes the part reservation it needs, and the PDF and JSON exports
/// describe the selected approved revision's snapshot with its task statuses. Runs the real approval, progress and
/// export endpoints on a temporary file-backed SQLite database seeded with the single BR-101 unit. In the P-101
/// plans only P101-BEARING-REPLACEMENT needs a part (one BR-101).
/// </summary>
public sealed class F06ConsumptionAndExportFileDatabaseTests
{
    [Fact]
    public async Task CompletingThePartTask_ConsumesItsReservation_WhileInProgressDoesNot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draftId = await fixture.ApproveAsync(ApprovalTestSupport.StableIdCandidate());

        await fixture.MoveAsync(draftId, "P101-BEARING-REPLACEMENT", "InProgress");

        Assert.Null((await fixture.ReservationAsync(draftId)).CompletedAtUtc);

        await fixture.MoveAsync(draftId, "P101-BEARING-REPLACEMENT", "Completed");

        var consumed = await fixture.ReservationAsync(draftId);
        Assert.Equal(ReservationStatus.Confirmed, consumed.Status);
        await using (var dbContext = fixture.CreateDbContext())
        {
            var progress = await dbContext.DraftTaskProgress.SingleAsync(p => p.TaskId == "P101-BEARING-REPLACEMENT");
            Assert.Equal(progress.UpdatedAtUtc, consumed.CompletedAtUtc); // recorded in the same update
        }

        await fixture.AssertBearingStockAsync(onHand: 1m, reserved: 1m);
        var partProgress = await fixture.GetProgressAsync(draftId);
        Assert.Equal((1m, 0m), (partProgress.CompletedQuantity, partProgress.RemainingQuantity));
    }

    [Fact]
    public async Task RepeatedOrRejectedCompletion_DoesNotConsumeAgain()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draftId = await fixture.ApproveAsync(ApprovalTestSupport.StableIdCandidate());
        await fixture.MoveAsync(draftId, "P101-BEARING-REPLACEMENT", "InProgress");
        await fixture.MoveAsync(draftId, "P101-BEARING-REPLACEMENT", "Completed");
        var consumedAt = (await fixture.ReservationAsync(draftId)).CompletedAtUtc;
        Assert.NotNull(consumedAt);

        foreach (var status in new[] { "Completed", "InProgress", "NotStarted" })
        {
            using var rejected = await fixture.PostStatusAsync(draftId, "P101-BEARING-REPLACEMENT", status);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        }

        var legacyCompletion = await fixture.MarkTaskCompleteAsync(draftId, "BR-101");

        Assert.False(legacyCompletion.Succeeded);
        var reservation = await fixture.ReservationAsync(draftId);
        Assert.Equal((ReservationStatus.Confirmed, consumedAt), (reservation.Status, reservation.CompletedAtUtc));
        await fixture.AssertBearingStockAsync(onHand: 1m, reserved: 1m);
    }

    [Fact]
    public async Task CompletingTheTask_KeepsAConsumptionAlreadyRecordedByMarkTaskComplete()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draftId = await fixture.ApproveAsync(ApprovalTestSupport.StableIdCandidate());
        Assert.True((await fixture.MarkTaskCompleteAsync(draftId, "BR-101")).Succeeded);
        var consumedAt = (await fixture.ReservationAsync(draftId)).CompletedAtUtc;
        Assert.NotNull(consumedAt);

        await fixture.MoveAsync(draftId, "P101-BEARING-REPLACEMENT", "InProgress");
        await fixture.MoveAsync(draftId, "P101-BEARING-REPLACEMENT", "Completed");

        Assert.Equal(consumedAt, (await fixture.ReservationAsync(draftId)).CompletedAtUtc);
        await fixture.AssertBearingStockAsync(onHand: 1m, reserved: 1m);
    }

    [Fact]
    public async Task CancellingAfterTaskCompletion_KeepsTheConsumedReservation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draftId = await fixture.ApproveAsync(ApprovalTestSupport.StableIdCandidate());
        await fixture.MoveAsync(draftId, "P101-BEARING-REPLACEMENT", "InProgress");
        await fixture.MoveAsync(draftId, "P101-BEARING-REPLACEMENT", "Completed");
        var consumed = await fixture.ReservationAsync(draftId);

        var cancellation = await fixture.CancelAsync(draftId);

        Assert.True(cancellation.Succeeded, cancellation.FailureReason);
        var afterCancellation = await fixture.ReservationAsync(draftId);
        Assert.Equal((consumed.Id, ReservationStatus.Confirmed, consumed.CompletedAtUtc),
            (afterCancellation.Id, afterCancellation.Status, afterCancellation.CompletedAtUtc));
        await fixture.AssertBearingStockAsync(onHand: 1m, reserved: 1m);
    }

    [Fact]
    public async Task Exports_DescribeTheSelectedApprovedRevisionAndItsTaskStatuses_InJsonAndPdf()
    {
        await using var fixture = await Fixture.CreateAsync();
        // Two alternatives that differ in crew; the second (lowest-cost) one is selected and approved
        var job = ApprovalTestSupport.CreateSucceededJob(
            Guid.NewGuid(), null, ApprovalTestSupport.StableIdCandidate(), ApprovalTestSupport.LowestCostCandidate());
        var draftId = await fixture.ApproveAsync(job, candidateIndex: 1);
        await fixture.MoveAsync(draftId, "P101-PREP", "InProgress");
        await fixture.MoveAsync(draftId, "P101-PREP", "Completed");
        await fixture.MoveAsync(draftId, "P101-BEARING-REPLACEMENT", "InProgress");

        string snapshot;
        await using (var dbContext = fixture.CreateDbContext())
            snapshot = (await dbContext.Drafts.SingleAsync(d => d.Id == draftId)).ApprovedCandidatePayload!;
        Assert.Equal(ApprovalTestSupport.CandidateJson(job, 1), snapshot);
        var expectedStatuses = new Dictionary<string, string>
        {
            ["P101-PREP"] = "Completed",
            ["P101-BEARING-REPLACEMENT"] = "InProgress",
            ["P101-VERIFICATION"] = "NotStarted"
        };
        var expected = ReadPlacements(snapshot)
            .Select(p => p with { Status = expectedStatuses[p.TaskId] })
            .ToList();

        // JSON: the selected revision's snapshot placements, in order, with their statuses
        using var jsonResponse = await fixture.Client.GetAsync($"/api/drafts/{draftId}/export/json");
        Assert.Equal(HttpStatusCode.OK, jsonResponse.StatusCode);
        using var json = JsonDocument.Parse(await jsonResponse.Content.ReadAsStringAsync());
        Assert.Equal(draftId.ToString(), json.RootElement.GetProperty("draftId").GetString());
        Assert.Equal(job.Id.ToString(), json.RootElement.GetProperty("planningJobId").GetString());
        var exported = ReadPlacements(json.RootElement.GetProperty("taskPlacements"));
        Assert.Equal(expected, exported, ExportedPlacementComparer.Instance);
        Assert.Equal(["TECH-003"], exported[0].TechnicianIds); // the selected alternative's crew, not the first's

        // PDF: the same draft and the same task placements and statuses
        using var pdfResponse = await fixture.Client.GetAsync($"/api/drafts/{draftId}/export/pdf");
        Assert.Equal(HttpStatusCode.OK, pdfResponse.StatusCode);
        var pdfText = ExtractText(await pdfResponse.Content.ReadAsByteArrayAsync());
        Assert.Contains($"Draft ID: {draftId}", pdfText);
        Assert.Contains($"Planning job ID: {job.Id}", pdfText);
        foreach (var placement in expected)
        {
            Assert.Contains($"{placement.TaskId}: {placement.Status}", pdfText);
            Assert.Contains(
                $"{Format(placement.Start)} to {Format(placement.End)} by {string.Join(", ", placement.TechnicianIds)}",
                pdfText);
        }
    }

    private sealed record ExportedPlacement(
        string TaskId,
        DateTimeOffset Start,
        DateTimeOffset End,
        IReadOnlyList<string> TechnicianIds,
        string? Status);

    /// <summary>Compares placements by value, including their technician lists.</summary>
    private sealed class ExportedPlacementComparer : IEqualityComparer<ExportedPlacement>
    {
        public static readonly ExportedPlacementComparer Instance = new();

        public bool Equals(ExportedPlacement? x, ExportedPlacement? y) =>
            x is not null && y is not null &&
            (x.TaskId, x.Start, x.End, x.Status) == (y.TaskId, y.Start, y.End, y.Status) &&
            x.TechnicianIds.SequenceEqual(y.TechnicianIds);

        public int GetHashCode(ExportedPlacement obj) => obj.TaskId.GetHashCode();
    }

    private static List<ExportedPlacement> ReadPlacements(string snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);
        return ReadPlacements(document.RootElement.GetProperty("taskPlacements"));
    }

    private static List<ExportedPlacement> ReadPlacements(JsonElement placements) =>
        placements.EnumerateArray()
            .Select(p => new ExportedPlacement(
                p.GetProperty("taskId").GetString()!,
                p.GetProperty("start").GetDateTimeOffset(),
                p.GetProperty("end").GetDateTimeOffset(),
                p.GetProperty("technicianIds").EnumerateArray().Select(t => t.GetString()!).ToList(),
                p.TryGetProperty("status", out var status) ? status.GetString() : null))
            .ToList();

    private static string Format(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

    /// <summary>The PDF's text with every whitespace run collapsed to one space.</summary>
    private static string ExtractText(byte[] pdf)
    {
        var reader = new PdfReader(pdf);
        try
        {
            var text = new StringBuilder();
            for (var page = 1; page <= reader.NumberOfPages; page++)
                text.Append(PdfTextExtractor.GetTextFromPage(reader, page)).Append('\n');
            return Regex.Replace(text.ToString(), @"\s+", " ");
        }
        finally
        {
            reader.Close();
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private Fixture(string databasePath, string connectionString, WebApplication app)
        {
            _databasePath = databasePath;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"maintenance-planner-f06c-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
            await using (var dbContext = new MaintenancePlannerDbContext(
                             new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(connectionString).Options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(connectionString));
            builder.Services.AddApprovalServices();
            builder.Services.AddScoped<IPlanProgressService, PlanProgressService>();
            builder.Services.AddScoped<IPlanExportService, PlanExportService>();
            var app = builder.Build();
            app.MapApprovalEndpoints();
            app.MapPlanProgressEndpoints();
            await app.StartAsync();
            return new Fixture(databasePath, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        public Task<Guid> ApproveAsync(object candidate) =>
            ApproveAsync(ApprovalTestSupport.CreateSucceededJob(Guid.NewGuid(), null, candidate), candidateIndex: 0);

        public async Task<Guid> ApproveAsync(PlanningJob job, int candidateIndex)
        {
            await using (var dbContext = CreateDbContext())
            {
                await dbContext.PlanningJobs.AddAsync(job);
                await dbContext.SaveChangesAsync();
            }

            await using var scope = _app.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
                .ApproveDraftAsync(job.Id, candidateIndex, CancellationToken.None);
            Assert.True(result.Succeeded, result.FailureReason);
            return result.DraftId!.Value;
        }

        public async Task<CancellationResult> CancelAsync(Guid draftId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDraftApprovalService>()
                .CancelDraftAsync(draftId, CancellationToken.None);
        }

        public async Task<PlanProgressResult> MarkTaskCompleteAsync(Guid draftId, string sparePartId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IPlanProgressService>()
                .MarkTaskCompleteAsync(draftId, sparePartId, CancellationToken.None);
        }

        public Task<HttpResponseMessage> PostStatusAsync(Guid draftId, string taskId, string status) =>
            Client.PostAsJsonAsync($"/api/drafts/{draftId}/tasks/{taskId}/progress", new { status });

        public async Task MoveAsync(Guid draftId, string taskId, string status)
        {
            using var response = await PostStatusAsync(draftId, taskId, status);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task<MaintenancePlanner.Api.Contracts.PlanProgressResponse> GetProgressAsync(Guid draftId) =>
            (await Client.GetFromJsonAsync<MaintenancePlanner.Api.Contracts.PlanProgressResponse>(
                $"/api/drafts/{draftId}/progress"))!;

        public async Task<Reservation> ReservationAsync(Guid draftId)
        {
            await using var dbContext = CreateDbContext();
            return await dbContext.Reservations.AsNoTracking().SingleAsync(r => r.DraftId == draftId);
        }

        /// <summary>Asserts BR-101's quantities and that ReservedQuantity equals its confirmed reservations.</summary>
        public async Task AssertBearingStockAsync(decimal onHand, decimal reserved)
        {
            await using var dbContext = CreateDbContext();
            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            var confirmedTotal = (await dbContext.Reservations
                    .Where(r => r.SparePartId == "BR-101" && r.Status == ReservationStatus.Confirmed)
                    .ToListAsync())
                .Sum(r => r.QuantityReserved);
            Assert.Equal((onHand, reserved, reserved), (br101.OnHandQuantity, br101.ReservedQuantity, confirmedTotal));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                try
                {
                    File.Delete(_databasePath + suffix);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of a temp file; a leftover file does not affect test outcomes
                }
            }
        }
    }
}
