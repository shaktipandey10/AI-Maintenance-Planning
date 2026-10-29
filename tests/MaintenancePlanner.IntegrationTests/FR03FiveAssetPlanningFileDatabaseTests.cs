using System.Net;
using System.Net.Http.Json;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Application.Imports;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Application.Queries;
using MaintenancePlanner.Application.Validation;
using MaintenancePlanner.Infrastructure.Imports;
using MaintenancePlanner.Infrastructure.ManualEvidence;
using MaintenancePlanner.Infrastructure.Persistence;
using MaintenancePlanner.Infrastructure.Planning;
using MaintenancePlanner.Infrastructure.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// FR03/FR04: the PLANT-01 sample dataset in data/fixtures/plant01 is imported through the import endpoints into a
/// temporary file-backed database seeded like the application, then planned for all five assets with the real
/// queries, manuals, evidence retrieval, validator and scheduler. Only the model interpreter is replaced, by one that
/// proposes the whole catalogue of the selected assets, so no model is needed.
/// </summary>
public sealed class FR03FiveAssetPlanningFileDatabaseTests
{
    private static readonly string[] FiveAssets = ["P-101", "P-102", "P-103", "P-104", "P-105"];
    private const string BaselineWindow = "MW-P101-BASELINE-20261024";

    [Fact]
    public async Task SampleDataset_ImportsThroughTheImportEndpoints_AndMeetsTheAssignmentMinimums()
    {
        await using var fixture = await PlantFixture.CreateAsync();
        var seededBefore = await fixture.SnapshotP101Async();

        await fixture.ImportSampleDatasetAsync();

        await using var db = fixture.CreateDbContext();
        Assert.Equal(FiveAssets, (await db.Assets.Select(a => a.Id).ToListAsync()).Order());
        Assert.All(await db.Assets.ToListAsync(), asset => Assert.Equal("PLANT-01", asset.PlantId));
        var tasks = await db.MaintenanceTaskDefinitions.ToListAsync();
        Assert.InRange(tasks.Count, 10, 15);
        Assert.Equal(FiveAssets, tasks.Select(t => t.AssetId).Distinct().Order());
        Assert.True(tasks.Count(t => t.PredecessorTaskId is not null) >= 4);
        var technicians = await db.Technicians.Include(t => t.TechnicalSkills).Include(t => t.TechnicianAvailabilities).ToListAsync();
        Assert.Equal(3, technicians.Count);
        Assert.All(technicians, t => Assert.NotEmpty(t.TechnicianAvailabilities));
        // Skill constraint: only one technician holds INSTRUMENTATION
        Assert.Single(technicians, t => t.TechnicalSkills.Any(s => s.SkillCode == "INSTRUMENTATION"));
        var parts = await db.SpareParts.Include(p => p.CompatibleAssets).ToListAsync();
        Assert.True(parts.Count >= 6);
        Assert.All(parts, part => Assert.NotEmpty(part.CompatibleAssets));
        var history = await db.MaintenanceHistoryRecords.ToListAsync();
        Assert.True(history.Count >= 10);
        Assert.Equal(FiveAssets, history.Select(h => h.AssetId).Distinct().Order());
        Assert.Contains(history, h => h.Outcome.Contains("differs from manual", StringComparison.Ordinal));
        Assert.Equal(2, await db.MaintenanceWindows.CountAsync());
        Assert.Equal(seededBefore, await fixture.SnapshotP101Async());

        // Running the setup again changes nothing: every file is rejected because its records already exist
        var (status, report) = await fixture.UploadAsync("/api/imports/assets", "01-assets.csv");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.All(report.Issues, issue => Assert.Contains("already exists", issue.Message));
        Assert.Equal(5, await db.Assets.CountAsync());
    }

    [Fact]
    public async Task FiveAssetRequest_ProducesTwoDistinctValidatedAlternativesWithTotalsAndEvidence()
    {
        await using var fixture = await PlantFixture.CreateAsync();
        await fixture.ImportSampleDatasetAsync();

        var result = await fixture.PlanAsync(FiveAssets, BaselineWindow);

        var generated = Assert.IsType<PlanningCandidatesGeneratedResult>(result);
        Assert.Equal(2, generated.Candidates.Count);
        await using var db = fixture.CreateDbContext();
        var allTaskIds = (await db.MaintenanceTaskDefinitions.Select(t => t.Id).ToListAsync()).Order().ToArray();
        foreach (var candidate in generated.Candidates)
        {
            Assert.Equal(allTaskIds, candidate.TaskPlacements.Select(p => p.TaskId).Order());
            Assert.True(candidate.TotalLabourCostAmount > 0);
            Assert.True(candidate.CompletionTimeMinutes > 0);
            Assert.False(string.IsNullOrWhiteSpace(candidate.Rationale));
            var validation = await fixture.ValidateAsync(FiveAssets, BaselineWindow, candidate);
            Assert.True(validation.IsFeasible, string.Join("; ", validation.Violations.Select(v => $"{v.Code}: {v.Message}")));
        }

        // Distinct plans: they differ in who does the work or when, and their calculated totals are compared
        var first = Describe(generated.Candidates[0]);
        var second = Describe(generated.Candidates[1]);
        Assert.NotEqual(first, second);
        Assert.Single(generated.Candidates, candidate => candidate.IsLowestCostCandidate);

        // Evidence is sourced from all three manuals that the dataset's tasks cite, and from maintenance history
        var manuals = generated.Candidates[0].EvidenceReferences
            .Where(e => e.SourceKind == PlanningEvidenceSourceKind.MaintenanceManual)
            .Select(e => e.DocumentId).Distinct().Order();
        Assert.Equal(new[] { "MAN-P101-001", "MAN-P102-SCOPE-001", "MAN-PLANT01-SERVICE-001" }, manuals);
        Assert.Contains(generated.Candidates[0].EvidenceReferences, e =>
            e.SourceKind == PlanningEvidenceSourceKind.MaintenanceManual && e.DocumentId == "MAN-PLANT01-SERVICE-001" && e.Page == 2);
        Assert.True(generated.EvidenceReferences.Count(e => e.SourceKind == PlanningEvidenceSourceKind.MaintenanceHistory) >= 10);
    }

    [Fact]
    public async Task P101OnlyRequest_IsUnchangedByTheImportedAssets()
    {
        await using var fixture = await PlantFixture.CreateAsync();
        var before = Assert.IsType<PlanningCandidatesGeneratedResult>(await fixture.PlanAsync(["P-101"], BaselineWindow));

        await fixture.ImportSampleDatasetAsync();
        var after = Assert.IsType<PlanningCandidatesGeneratedResult>(await fixture.PlanAsync(["P-101"], BaselineWindow));

        Assert.Equal(before.Candidates.Select(Describe), after.Candidates.Select(Describe));
        Assert.Equal(
            before.Candidates.Select(c => (c.TotalLabourCostAmount, c.CompletionTimeMinutes)),
            after.Candidates.Select(c => (c.TotalLabourCostAmount, c.CompletionTimeMinutes)));
    }

    private static string Describe(PlanningCandidate candidate) =>
        string.Join(" | ", candidate.TaskPlacements
            .OrderBy(p => p.TaskId, StringComparer.Ordinal)
            .Select(p => $"{p.TaskId}@{p.Start:HH:mm}-{p.End:HH:mm}:{string.Join('+', p.TechnicianIds)}"));

    private static string DescribeResult(PlanningWorkflowResult result)
    {
        var violations = result.GetType().GetProperty("Violations")?.GetValue(result) as IEnumerable<PlanningValidationViolation>;
        var questions = result.GetType().GetProperty("Questions")?.GetValue(result) as IEnumerable<string>;
        return $"{result.GetType().Name}: " +
               string.Join("; ", (violations ?? []).Select(v => $"{v.Code} {v.TaskId}: {v.Message}").Concat(questions ?? []));
    }

    /// <summary>Proposes every catalogue task of the selected assets, as a resolved scope.</summary>
    private sealed class WholeCatalogueInterpreter : IPlanningRequestInterpreter
    {
        public Task<PlanningInterpretation> InterpretAsync(
            PlanningRequest request,
            IReadOnlyList<AssetReadModel> authoritativeAssets,
            IReadOnlyList<MaintenanceTaskReadModel> authoritativeTasks,
            CancellationToken cancellationToken)
        {
            var tasks = authoritativeTasks
                .Where(task => request.AssetIds.Contains(task.AssetId, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            return Task.FromResult(new PlanningInterpretation(
                "Planned maintenance",
                "All catalogue tasks of the selected assets",
                request.AssetIds,
                tasks.Select(task => task.Id).ToArray(),
                [],
                ["Test interpreter: the whole catalogue of each selected asset is requested."])
            {
                ScopeResolution = PlanningScopeResolution.Resolved,
                ScopeMappings = tasks.Select(task => new PlanningRequestScopeMapping(task.Id, task.AssetId, request.RequestText)).ToArray()
            });
        }
    }

    private sealed class PlantFixture : IAsyncDisposable
    {
        private static readonly string DataDirectory = Path.Combine(AppContext.BaseDirectory, "data");

        private readonly string _root;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private PlantFixture(string root, string connectionString, WebApplication app)
        {
            _root = root;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
        }

        private HttpClient Client { get; }

        public static async Task<PlantFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"maintenance-planner-fr03-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "planner.db"),
                Pooling = false
            }.ToString();
            await using (var dbContext = new MaintenancePlannerDbContext(
                             new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(connectionString).Options))
            {
                await dbContext.Database.MigrateAsync();
                await DatabaseInitializer.InitializeAsync(dbContext);
            }

            // The same manuals as the application: the bundled ones plus those imported at run time
            var manuals = Path.Combine(DataDirectory, "manuals");
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<MaintenancePlannerDbContext>(db => db.UseSqlite(connectionString));
            builder.Services.AddSingleton(new ImportedManualStore(
                Path.Combine(root, "imported-manuals"),
                ["MAN-P101-001", "MAN-P101-PLANNING-001", "MAN-P102-SCOPE-001"]));
            builder.Services.AddSingleton<ImportedManualEvidenceRetriever>();
            builder.Services.AddSingleton<IManualEvidenceRetriever>(services => new CompositeManualEvidenceRetriever(
            [
                new TextManualEvidenceRetriever(Path.Combine(manuals, "MAN-P101-001.txt")),
                new PdfManualEvidenceRetriever(Path.Combine(manuals, "MAN-P101-PLANNING-001.pdf"), "MAN-P101-PLANNING-001", "1.0"),
                new PdfManualEvidenceRetriever(Path.Combine(manuals, "MAN-P102-SCOPE-001.pdf"), "MAN-P102-SCOPE-001", "1.0"),
                services.GetRequiredService<ImportedManualEvidenceRetriever>()
            ]));
            builder.Services.AddScoped<IReferenceDataImportService, ReferenceDataImportService>();
            builder.Services.AddScoped<IManualImportService, ManualImportService>();
            builder.Services.AddScoped<IMaintenancePlannerQueries, MaintenancePlannerQueries>();
            builder.Services.AddScoped<ISparePartReadModelProvider>(services =>
                new ScenarioAwareSparePartReadModelProvider(services.GetRequiredService<IMaintenancePlannerQueries>()));
            builder.Services.AddScoped<PlanningCandidateValidator>();
            builder.Services.AddScoped<CandidateCostCalculator>();
            builder.Services.AddScoped<DeterministicPlanningScheduler>();
            builder.Services.AddSingleton<IPlanningRequestInterpreter, WholeCatalogueInterpreter>();
            builder.Services.AddScoped<IPlanningWorkflow, PlanningWorkflow>();
            var app = builder.Build();
            app.MapImportEndpoints();
            await app.StartAsync();
            return new PlantFixture(root, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        // The same files, order and form fields as data/fixtures/plant01/import-plant01.ps1
        public async Task ImportSampleDatasetAsync()
        {
            (string Path, string File, Dictionary<string, string>? Fields)[] steps =
            [
                ("/api/imports/assets", "01-assets.csv", null),
                ("/api/imports/spare-parts", "02-spare-parts.csv", null),
                ("/api/imports/manuals", "MAN-PLANT01-SERVICE-001.pdf",
                    new() { ["documentId"] = "MAN-PLANT01-SERVICE-001", ["version"] = "1.0" }),
                ("/api/imports/tasks", "04-tasks.json", null),
                ("/api/imports/maintenance-history", "05-maintenance-history.csv", null)
            ];
            foreach (var (path, file, fields) in steps)
            {
                var (status, report) = await UploadAsync(path, file, fields);
                Assert.True(status == HttpStatusCode.OK,
                    $"{file}: {string.Join("; ", report.Issues.Select(i => $"{i.Location} {i.Field}: {i.Message}"))}");
            }
        }

        public async Task<(HttpStatusCode Status, ImportReport Report)> UploadAsync(
            string path,
            string file,
            Dictionary<string, string>? fields = null)
        {
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(DataDirectory, "fixtures", "plant01", file))), "file", file }
            };
            foreach (var (name, value) in fields ?? [])
                form.Add(new StringContent(value), name);
            var response = await Client.PostAsync(path, form);
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<ImportReport>())!);
        }

        public async Task<PlanningWorkflowResult> PlanAsync(IReadOnlyList<string> assetIds, string windowId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IPlanningWorkflow>().ExecuteAsync(
                new PlanningRequest("Plan the catalogue maintenance for the selected PLANT-01 assets.", assetIds, windowId),
                CancellationToken.None);
            Assert.True(result is PlanningCandidatesGeneratedResult, DescribeResult(result));
            return result;
        }

        public async Task<PlanningCandidateValidationResult> ValidateAsync(
            IReadOnlyList<string> assetIds,
            string windowId,
            PlanningCandidate candidate)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PlanningCandidateValidator>().ValidateAsync(
                new PlanningCandidateValidationRequest(assetIds, windowId, candidate),
                CancellationToken.None);
        }

        // The seeded P-101 records as one comparable string
        public async Task<string> SnapshotP101Async()
        {
            await using var db = CreateDbContext();
            var asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == "P-101");
            var part = await db.SpareParts.AsNoTracking().SingleAsync(p => p.Id == "BR-101");
            var tasks = await db.MaintenanceTaskDefinitions.AsNoTracking().Where(t => t.AssetId == "P-101").OrderBy(t => t.Id)
                .Select(t => $"{t.Id}|{t.DurationMinutes}|{t.RequiredSkills}|{t.RequiredCrewSize}|{t.RequiredPartId}|{t.PredecessorTaskId}")
                .ToListAsync();
            var availability = (await db.TechnicianAvailabilities.AsNoTracking().ToListAsync())
                .Select(a => $"{a.TechnicianId}|{a.AvailableFrom:O}|{a.AvailableTo:O}").Order();
            var windows = (await db.MaintenanceWindows.AsNoTracking().ToListAsync())
                .Select(w => $"{w.Id}|{w.Start:O}|{w.End:O}").Order();
            return string.Join("\n",
                [$"{asset.Name}|{asset.Model}|{asset.Priority}", $"{part.OnHandQuantity}|{part.ReservedQuantity}", .. tasks, .. availability, .. windows]);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }
}
