using System.Net;
using System.Net.Http.Json;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// F01: concurrent approvals competing for the single seeded BR-101, sent in parallel through the real approval
/// endpoint to a temporary file-backed SQLite database (not the demo database, and not an in-memory database).
/// Each competing plan has its own crew, so the part is the only contested resource.
/// </summary>
public sealed class F01ConcurrentInventoryApprovalFileDatabaseTests
{
    [Fact]
    public async Task ParallelApprovalsForLastUnit_OnFileDatabase_ExactlyOneReserves_LosersLeaveNoSideEffects()
    {
        await using var fixture = await FileDatabaseApprovalFixture.CreateAsync();
        var jobIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddJobsCompetingOnlyForPartsAsync(dbContext, jobIds);
        }

        var jobVersionsBefore = await fixture.JobVersionsAsync();
        var responses = await Task.WhenAll(jobIds.Select(jobId =>
            fixture.Client.PostAsync(
                $"/api/drafts/{jobId}/approve",
                new StringContent("", System.Text.Encoding.UTF8, "application/json"))));

        var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        var losers = responses.Where(r => r.StatusCode != HttpStatusCode.Accepted).ToArray();
        Assert.Equal(jobIds.Length - 1, losers.Length);
        foreach (var loser in losers)
        {
            Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
            var error = await loser.Content.ReadFromJsonAsync<ApprovalErrorResponse>();
            Assert.Equal("SparePart", error!.ResourceType);
            Assert.Equal("INSUFFICIENT_INVENTORY", error.Reason);
            Assert.Equal("BR-101", error.ResourceId);
            Assert.Contains("Insufficient inventory", error.Message);
        }

        var winningDraftId = Guid.Parse(winner.Headers.Location!.OriginalString.Split('/').Last());
        await using (var dbContext = fixture.CreateDbContext())
        {
            // One approved draft with its snapshot; nothing persisted for any losing job
            var draft = Assert.Single(await dbContext.Drafts.ToListAsync());
            Assert.Equal((winningDraftId, DraftStatus.Approved), (draft.Id, draft.Status));
            Assert.NotNull(draft.ApprovedCandidatePayload);

            var reservation = Assert.Single(await dbContext.Reservations.ToListAsync());
            Assert.Equal(
                (winningDraftId, "BR-101", 1m, ReservationStatus.Confirmed),
                (reservation.DraftId, reservation.SparePartId, reservation.QuantityReserved, reservation.Status));

            var locks = await dbContext.LockedTaskAssignments.ToListAsync();
            Assert.Equal(3, locks.Count);
            Assert.All(locks, l => Assert.Equal(winningDraftId, l.DraftId));

            var br101 = await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101");
            Assert.Equal(1m, br101.OnHandQuantity);
            Assert.Equal(1m, br101.ReservedQuantity);
            Assert.Equal(0m, br101.AvailableQuantity);
        }

        // Approval never modifies planning jobs, winning or losing
        Assert.Equal(jobVersionsBefore, await fixture.JobVersionsAsync());
    }

    [Fact]
    public async Task ParallelDuplicateApprovalsOfSameJob_OnFileDatabase_AreIdempotent()
    {
        await using var fixture = await FileDatabaseApprovalFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, jobId);
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            fixture.Client.PostAsync(
                $"/api/drafts/{jobId}/approve",
                new StringContent("", System.Text.Encoding.UTF8, "application/json"))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Single(responses.Select(r => r.Headers.Location!.OriginalString).Distinct());
        await using var verifyDb = fixture.CreateDbContext();
        Assert.Single(await verifyDb.Drafts.ToListAsync());
        Assert.Single(await verifyDb.Reservations.ToListAsync());
        Assert.Equal(3, await verifyDb.LockedTaskAssignments.CountAsync());
        Assert.Equal(1m, (await verifyDb.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
    }

    private sealed class FileDatabaseApprovalFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private FileDatabaseApprovalFixture(string databasePath, string connectionString, WebApplication app)
        {
            _databasePath = databasePath;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<FileDatabaseApprovalFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"maintenance-planner-f01-{Guid.NewGuid():N}.db");
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
            var app = builder.Build();
            app.MapApprovalEndpoints();
            await app.StartAsync();
            return new FileDatabaseApprovalFixture(databasePath, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        public async Task<string> JobVersionsAsync()
        {
            await using var dbContext = CreateDbContext();
            var jobs = await dbContext.PlanningJobs.AsNoTracking().OrderBy(j => j.Id).ToListAsync();
            return string.Join(",", jobs.Select(j => $"{j.Id}:{j.Status}:{j.ConcurrencyVersion}"));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                if (File.Exists(_databasePath + suffix))
                    File.Delete(_databasePath + suffix);
            }
        }
    }
}
