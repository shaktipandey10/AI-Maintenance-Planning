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

/// <summary>Stage 1: the approve endpoint's optional {"candidateIndex": n} body and replacement routing.</summary>
public sealed class Stage1ApprovalHttpTests
{
    [Fact]
    public async Task Approve_WithCandidateIndexBody_ApprovesThatCandidate_WithoutBodyIsRejectedForMultipleCandidates()
    {
        await using var fixture = await HttpFixture.CreateAsync();
        var jobId = Guid.NewGuid();
        var job = ApprovalTestSupport.CreateSucceededJob(
            jobId, null, ApprovalTestSupport.StableIdCandidate(), ApprovalTestSupport.LowestCostCandidate());
        await using (var dbContext = fixture.CreateDbContext())
        {
            await dbContext.PlanningJobs.AddAsync(job);
            await dbContext.SaveChangesAsync();
        }

        // The current UI posts an empty JSON body; with two candidates that must not silently approve the first.
        var withoutSelection = await fixture.Client.PostAsync(
            $"/api/drafts/{jobId}/approve",
            new StringContent("", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, withoutSelection.StatusCode);
        var error = await withoutSelection.Content.ReadFromJsonAsync<ApprovalErrorResponse>();
        Assert.Equal("CANDIDATE_SELECTION_REQUIRED", error!.Reason);

        var selected = await fixture.Client.PostAsJsonAsync($"/api/drafts/{jobId}/approve", new { candidateIndex = 1 });
        Assert.Equal(HttpStatusCode.Accepted, selected.StatusCode);
        var repeated = await fixture.Client.PostAsJsonAsync($"/api/drafts/{jobId}/approve", new { candidateIndex = 1 });
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(selected.Headers.Location, repeated.Headers.Location);

        await using (var dbContext = fixture.CreateDbContext())
        {
            var draft = await dbContext.Drafts.SingleAsync();
            Assert.Equal(ApprovalTestSupport.CandidateJson(job, 1), draft.ApprovedCandidatePayload);
            Assert.Equal(1, await dbContext.Reservations.CountAsync());
        }
    }

    [Fact]
    public async Task Approve_ReplacementJob_SupersedesV1_AndRepeatedRequestStaysIdempotent()
    {
        await using var fixture = await HttpFixture.CreateAsync();
        var v1JobId = Guid.NewGuid();
        var v2JobId = Guid.NewGuid();
        await using (var dbContext = fixture.CreateDbContext())
        {
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, v1JobId);
            await ApprovalTestSupport.AddSucceededJobAsync(dbContext, v2JobId, v1JobId);
        }

        var v1 = await fixture.Client.PostAsJsonAsync($"/api/drafts/{v1JobId}/approve", new { candidateIndex = 0 });
        var v2 = await fixture.Client.PostAsJsonAsync($"/api/drafts/{v2JobId}/approve", new { candidateIndex = 0 });
        var v2Repeat = await fixture.Client.PostAsJsonAsync($"/api/drafts/{v2JobId}/approve", new { candidateIndex = 0 });

        Assert.Equal(HttpStatusCode.Accepted, v1.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, v2.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, v2Repeat.StatusCode);
        Assert.Equal(v2.Headers.Location, v2Repeat.Headers.Location);

        await using (var dbContext = fixture.CreateDbContext())
        {
            Assert.Equal(DraftStatus.Superseded, (await dbContext.Drafts.SingleAsync(d => d.PlanningJobId == v1JobId)).Status);
            Assert.Equal(DraftStatus.Approved, (await dbContext.Drafts.SingleAsync(d => d.PlanningJobId == v2JobId)).Status);
            Assert.Single(await dbContext.Reservations.Where(r => r.Status == ReservationStatus.Confirmed).ToListAsync());
            Assert.Equal(1m, (await dbContext.SpareParts.SingleAsync(p => p.Id == "BR-101")).ReservedQuantity);
        }
    }

    private sealed class HttpFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private HttpFixture(SqliteConnection connection, string connectionString, WebApplication app)
        {
            _connection = connection;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<HttpFixture> CreateAsync()
        {
            var connectionString = $"Data Source=Stage1Http-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using (var dbContext = new MaintenancePlannerDbContext(
                             new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(connection).Options))
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

            return new HttpFixture(connection, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
