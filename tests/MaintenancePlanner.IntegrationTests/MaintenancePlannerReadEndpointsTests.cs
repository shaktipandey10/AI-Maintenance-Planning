using System.Net;
using System.Net.Http.Json;
using MaintenancePlanner.Api.Contracts;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

public sealed class MaintenancePlannerReadEndpointsTests
{
    [Fact]
    public async Task GetRoutes_ReturnSeededSqliteData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MaintenancePlannerDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var seedContext = new MaintenancePlannerDbContext(options))
        {
            await DatabaseInitializer.InitializeAsync(seedContext);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(connection));
        builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();

        await using var app = builder.Build();
        app.MapMaintenancePlannerEndpoints();
        await app.StartAsync();

        using var client = app.GetTestClient();

        var assets = await client.GetFromJsonAsync<AssetResponse[]>("/api/assets");
        var technicians = await client.GetFromJsonAsync<TechnicianResponse[]>("/api/technicians");
        var parts = await client.GetFromJsonAsync<SparePartResponse[]>("/api/spareparts");
        var windows = await client.GetFromJsonAsync<MaintenanceWindowResponse[]>("/api/maintenance-windows");
        var tasks = await client.GetFromJsonAsync<MaintenanceTaskResponse[]>("/api/task-catalogue/P-101");
        var missingAsset = await client.GetAsync("/api/task-catalogue/UNKNOWN");

        Assert.Equal("P-101", Assert.Single(assets!).Id);
        Assert.Equal(3, technicians!.Length);
        Assert.Contains(technicians, technician => technician.Skills.Contains("ROTATING-EQUIPMENT"));
        Assert.Equal("BR-101", Assert.Single(parts!).Id);
        Assert.Equal(2, windows!.Length);
        Assert.Equal(
            new[] { "Preparation", "Bearing replacement", "Verification" },
            tasks!.Select(task => task.Name));
        Assert.Equal(HttpStatusCode.NotFound, missingAsset.StatusCode);

        await app.StopAsync();
    }
}