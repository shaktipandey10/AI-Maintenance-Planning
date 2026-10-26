using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Api.Workers;
using MaintenancePlanner.Application.Approvals;
using MaintenancePlanner.Application.Edits;
using MaintenancePlanner.Application.Locks;
using MaintenancePlanner.Application.PlanningJobs;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Infrastructure.Approvals;
using MaintenancePlanner.Infrastructure.Edits;
using MaintenancePlanner.Infrastructure.Locks;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.ManualEvidence;
using MaintenancePlanner.Infrastructure.Planning;
using MaintenancePlanner.Infrastructure.PlanningJobs;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});
builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
builder.Services.AddScoped<ISparePartReadModelProvider>(sp =>
    new ScenarioAwareSparePartReadModelProvider(
        sp.GetRequiredService<IMaintenancePlannerQueries>()));
builder.Services.AddScoped<PlanningCandidateValidator>();
builder.Services.AddScoped<CandidateCostCalculator>();
builder.Services.AddScoped<DeterministicPlanningScheduler>();
builder.Services.AddSingleton<TextManualEvidenceRetriever>(_ => new TextManualEvidenceRetriever(
    Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        "..",
        "..",
        "data",
        "manuals",
        "MAN-P101-001.txt"))));
builder.Services.AddSingleton<PdfManualEvidenceRetriever>(_ =>
    new PdfManualEvidenceRetriever(
        Path.GetFullPath(Path.Combine(
            builder.Environment.ContentRootPath,
            "..",
            "..",
            "data",
            "manuals",
            "MAN-P101-PLANNING-001.pdf")),
        "MAN-P101-PLANNING-001",
        "1.0"));
builder.Services.AddSingleton<PdfManualEvidenceRetriever>(_ =>
    new PdfManualEvidenceRetriever(
        Path.GetFullPath(Path.Combine(
            builder.Environment.ContentRootPath,
            "..",
            "..",
            "data",
            "manuals",
            "MAN-P102-SCOPE-001.pdf")),
        "MAN-P102-SCOPE-001",
        "1.0"));
builder.Services.AddSingleton<IManualEvidenceRetriever>(serviceProvider =>
    new CompositeManualEvidenceRetriever(
    [
        serviceProvider.GetRequiredService<TextManualEvidenceRetriever>(),
        .. serviceProvider.GetServices<PdfManualEvidenceRetriever>()
    ]));
builder.Services.AddOllamaPlanningInterpreter(builder.Configuration);
builder.Services.AddScoped<IPlanningWorkflow, PlanningWorkflow>();
builder.Services.AddScoped<IPlanningJobs, PlanningJobs>();
builder.Services.AddScoped<IPlanningJobProcessor, PlanningJobProcessor>();
builder.Services.AddScoped<IDraftApprovalService, DraftApprovalService>();
builder.Services.AddScoped<ITaskEditorService, TaskEditorService>();
builder.Services.AddScoped<IEditValidationService, EditValidationService>();
builder.Services.AddScoped<ITaskLockService, TaskLockService>();
builder.Services.AddScoped<IPlanProgressService, PlanProgressService>();
builder.Services.AddScoped<IPlanExportService, PlanExportService>();
builder.Services.AddOptions<PlanningJobWorkerOptions>()
    .Bind(builder.Configuration.GetSection("PlanningJobs"));
builder.Services.AddHostedService<PlanningJobWorker>();
var sqliteConnection = new SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("MaintenancePlanner"));
if (!Path.IsPathRooted(sqliteConnection.DataSource))
{
    sqliteConnection.DataSource = Path.GetFullPath(
        Path.Combine(builder.Environment.ContentRootPath, sqliteConnection.DataSource));
}

builder.Services.AddDbContext<MaintenancePlannerDbContext>(options =>
    options.UseSqlite(sqliteConnection.ToString()));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MaintenancePlannerDbContext>();
    await DatabaseInitializer.InitializeAsync(dbContext);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "v1");
    });
}

app.UseCors("AllowFrontend");
app.UseHttpsRedirection();

app.MapMaintenancePlannerEndpoints();
app.MapPlanningJobEndpoints();
app.MapApprovalEndpoints();
app.MapEditEndpoints();
app.MapPlanProgressEndpoints();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
