using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Queries;
using MaintenancePlanner.Infrastructure.Seeding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class PlanningJobEndpointsTests
{
    [Fact]
    public async Task PostPlanningJob_PersistsQueuedJobAndReturnsStatusUrl()
    {
        await using var testApi = await PlanningJobTestApi.CreateAsync();
        using var request = CreateRequest(JsonContent.Create(
            new
            {
                requestText = "Plan full bearing replacement for P-101.",
                assetIds = new[] { "P-101" },
                windowId = "MW-P101-BASELINE-20261024"
            }));
        using var response = await testApi.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>();
        Assert.NotNull(accepted);
        Assert.NotEqual(Guid.Empty, accepted.JobId);
        Assert.Equal($"/api/planning-jobs/{accepted.JobId}", accepted.StatusUrl);
        Assert.Equal(accepted.StatusUrl, response.Headers.Location?.OriginalString);

        await using var dbContext = testApi.CreateDbContext();
        var persisted = await dbContext.PlanningJobs.SingleAsync(job => job.Id == accepted.JobId);
        var savedRequest = JsonSerializer.Deserialize<PlanningJobRequestPayload>(
            persisted.RequestPayload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(savedRequest);
        Assert.Equal("Plan full bearing replacement for P-101.", savedRequest.RequestText);
        Assert.Equal(new[] { "P-101" }, savedRequest.AssetIds);
        Assert.Equal("MW-P101-BASELINE-20261024", savedRequest.WindowId);
        Assert.Equal(PlanningJobStatus.Queued, persisted.Status);
        Assert.Equal(TimeSpan.Zero, persisted.CreatedAtUtc.Offset);
        Assert.Null(persisted.StartedAtUtc);
        Assert.Null(persisted.CompletedAtUtc);
        Assert.Null(persisted.ErrorMessage);
    }

    [Fact]
    public async Task PostPlanningJob_RepeatedIdempotencyKeyReturnsExistingJob()
    {
        await using var testApi = await PlanningJobTestApi.CreateAsync();
        var operationId = Guid.NewGuid();
        var payload = JsonContent.Create(new
        {
            requestText = "Plan full bearing replacement for P-101.",
            assetIds = new[] { "P-101" }
        });
        using var firstRequest = CreateRequest(payload, operationId);
        using var firstResponse = await testApi.Client.SendAsync(firstRequest);
        using var secondRequest = CreateRequest(JsonContent.Create(new
        {
            requestText = "Plan full bearing replacement for P-101.",
            assetIds = new[] { "P-101" }
        }), operationId);
        using var secondResponse = await testApi.Client.SendAsync(secondRequest);

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>();
        var second = await secondResponse.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>();
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.JobId, second.JobId);

        await using var dbContext = testApi.CreateDbContext();
        Assert.Single(await dbContext.PlanningJobs.ToListAsync());
    }

    [Fact]
    public async Task PostPlanningJob_ConcurrentIdenticalSubmissionsCreateOneJob()
    {
        await using var testApi = await PlanningJobTestApi.CreateAsync();
        var operationId = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var request = CreateRequest(JsonContent.Create(new
            {
                requestText = "Plan full bearing replacement for P-101.",
                assetIds = new[] { "P-101" }
            }), operationId);
            return await testApi.Client.SendAsync(request);
        }));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
            var accepted = await Task.WhenAll(responses.Select(
                response => response.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>()));
            Assert.All(accepted, response => Assert.NotNull(response));
            Assert.Single(accepted.Select(response => response!.JobId).Distinct());

            await using var dbContext = testApi.CreateDbContext();
            Assert.Single(await dbContext.PlanningJobs.ToListAsync());
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Fact]
    public async Task PostPlanningJob_ReusedIdempotencyKeyWithDifferentRequestReturnsConflict()
    {
        await using var testApi = await PlanningJobTestApi.CreateAsync();
        var operationId = Guid.NewGuid();
        using var firstRequest = CreateRequest(JsonContent.Create(new
        {
            requestText = "Plan full bearing replacement for P-101.",
            assetIds = new[] { "P-101" }
        }), operationId);
        using var firstResponse = await testApi.Client.SendAsync(firstRequest);
        using var conflictingRequest = CreateRequest(JsonContent.Create(new
        {
            requestText = "Plan inspection only for P-101.",
            assetIds = new[] { "P-101" }
        }), operationId);

        using var conflictResponse = await testApi.Client.SendAsync(conflictingRequest);

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        await using var dbContext = testApi.CreateDbContext();
        Assert.Single(await dbContext.PlanningJobs.ToListAsync());
    }

    [Fact]
    public async Task PostPlanningJob_RejectsMissingIdempotencyKey()
    {
        await using var testApi = await PlanningJobTestApi.CreateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
        {
            Content = JsonContent.Create(new
            {
                requestText = "Plan full bearing replacement for P-101.",
                assetIds = new[] { "P-101" }
            })
        };

        using var response = await testApi.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var dbContext = testApi.CreateDbContext();
        Assert.Empty(await dbContext.PlanningJobs.ToListAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"requestText":"   ","assetIds":["P-101"]}""")]
    [InlineData("""{"requestText":null,"assetIds":["P-101"]}""")]
    [InlineData("""{"requestText":"Plan P-101.","assetIds":[]}""")]
    [InlineData("""{"requestText":"Plan P-101.","assetIds":null}""")]
    [InlineData("""{"requestText":"Plan P-101.","assetIds":["  "]}""")]
    [InlineData("""{"requestText":"Plan P-101.","assetIds":[null]}""")]
    [InlineData("""{"assetIds":["P-101"]}""")]
    [InlineData("""{"requestText":"Plan P-101."}""")]
    public async Task PostPlanningJob_RejectsInvalidPayload(string payload)
    {
        await using var testApi = await PlanningJobTestApi.CreateAsync();
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var request = CreateRequest(content);

        using var response = await testApi.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var dbContext = testApi.CreateDbContext();
        Assert.Empty(await dbContext.PlanningJobs.ToListAsync());
    }

    [Fact]
    public async Task GetPlanningJobStatus_ReturnsPersistedJobAndNotFoundForUnknownId()
    {
        await using var testApi = await PlanningJobTestApi.CreateAsync();
        using var createRequest = CreateRequest(JsonContent.Create(
            new
            {
                requestText = "Plan full bearing replacement for P-101.",
                assetIds = new[] { "P-101" }
            }));
        using var createResponse = await testApi.Client.SendAsync(createRequest);
        var accepted = await createResponse.Content.ReadFromJsonAsync<PlanningJobAcceptedResponse>();
        Assert.NotNull(accepted);

        using var response = await testApi.Client.GetAsync(accepted.StatusUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<PlanningJobStatusResponse>();
        Assert.NotNull(status);
        Assert.Equal(accepted.JobId, status.JobId);
        Assert.Equal("Queued", status.Status);
        Assert.Equal(TimeSpan.Zero, status.CreatedAtUtc.Offset);
        Assert.Null(status.StartedAtUtc);
        Assert.Null(status.CompletedAtUtc);
        Assert.Null(status.ErrorMessage);

        using var missingResponse = await testApi.Client.GetAsync(
            $"/api/planning-jobs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    private static HttpRequestMessage CreateRequest(HttpContent content, Guid? operationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/planning-jobs")
        {
            Content = content
        };
        request.Headers.Add("Idempotency-Key", (operationId ?? Guid.NewGuid()).ToString());
        return request;
    }

    private sealed class PlanningJobTestApi : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private PlanningJobTestApi(
            SqliteConnection connection,
            string connectionString,
            WebApplication app)
        {
            _connection = connection;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<PlanningJobTestApi> CreateAsync()
        {
            var connectionString =
                $"Data Source=PlanningJobEndpoint-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(connection)
                .Options;

            await using (var dbContext = new MaintenancePlannerDbContext(options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(
                db => db.UseSqlite(connectionString));
            builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
            builder.Services.AddScoped<IDraftApprovalService, DraftApprovalService>();
            builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();

            var app = builder.Build();
            app.MapPlanningJobEndpoints();
            await app.StartAsync();

            return new PlanningJobTestApi(connection, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
