using System.Net;
using System.Net.Http.Json;
using System.Text;
using iTextSharp.text;
using iTextSharp.text.pdf;
using MaintenancePlanner.Api.Endpoints;
using MaintenancePlanner.Application.Imports;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Infrastructure.Imports;
using MaintenancePlanner.Infrastructure.ManualEvidence;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MaintenancePlanner.IntegrationTests;

/// <summary>
/// FR02: CSV, JSON and text-based PDF inputs are imported through the real import endpoints into a temporary
/// file-backed SQLite database seeded like the application. Valid files are saved; any invalid record rejects the
/// whole file, nothing is saved and every invalid record is reported with its row, record or page and reason.
/// </summary>
public sealed class FR02DataImportFileDatabaseTests
{
    private const string TaskColumns =
        "id,assetId,name,durationMinutes,requiredSkills,crewSize,requiredPartId,requiredPartQuantity,predecessorTaskId,sourceDocumentId,sourceSection";

    [Fact]
    public async Task ValidCsvAndJsonImports_AddEveryDataset_AndLeaveSeededRecordsUnchanged()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var seededBefore = await fixture.SnapshotSeedAsync();

        await fixture.AssertImportedAsync("assets", "assets.csv", 2, """
            id,plantId,name,model,priority
            P-102,PLANT-01,"Feed Pump P-102, north bay",CP-100,2
            P-103,PLANT-01,Cooling Fan P-103,CF-20,3
            """);
        await fixture.AssertImportedAsync("spare-parts", "parts.json", 1, """
            [{ "id": "SEAL-102", "name": "Mechanical seal", "unit": "each", "onHandQuantity": 4, "compatibleAssetIds": ["P-102"] }]
            """);
        await fixture.AssertImportedAsync("technicians", "technicians.csv", 1, """
            id,name,hourlyRate,skills
            TECH-004,Ravi Menon,40.25,MECHANICAL;SEALING
            """);
        await fixture.AssertImportedAsync("technician-availability", "availability.json", 2, """
            [
              { "technicianId": "TECH-004", "availableFrom": "2026-10-24T08:00:00+05:30", "availableTo": "2026-10-24T12:00:00+05:30" },
              { "technicianId": "TECH-004", "availableFrom": "2026-10-24T13:00:00+05:30", "availableTo": "2026-10-24T16:00:00+05:30" }
            ]
            """);
        // The predecessor is defined after the task that needs it, in the same file
        await fixture.AssertImportedAsync("tasks", "tasks.csv", 2, $"""
            {TaskColumns}
            P102-SEAL-REPLACEMENT,P-102,Seal replacement,90,MECHANICAL;SEALING,1,SEAL-102,2,P102-ISOLATION,MAN-TEST-001,Seal replacement
            P102-ISOLATION,P-102,"Isolation, lockout",30,MECHANICAL,1,,,,MAN-TEST-001,Pump isolation
            """);
        await fixture.AssertImportedAsync("maintenance-history", "history.json", 1, """
            [{ "id": "H-P102-001", "assetId": "P-102", "performedAt": "2025-06-01T10:00:00+05:30",
               "summary": "Seal replaced after leak", "outcome": "Completed", "sourceDocumentId": "MAN-TEST-001", "sourceReference": "WO-1001" }]
            """);

        await using var db = fixture.CreateDbContext();
        var asset = await db.Assets.SingleAsync(a => a.Id == "P-102");
        Assert.Equal(("Feed Pump P-102, north bay", "CP-100", 2), (asset.Name, asset.Model, asset.Priority));
        var part = await db.SpareParts.Include(p => p.CompatibleAssets).SingleAsync(p => p.Id == "SEAL-102");
        Assert.Equal((4m, 0m, "each"), (part.OnHandQuantity, part.ReservedQuantity, part.Unit));
        Assert.Equal(new[] { "P-102" }, part.CompatibleAssets.Select(a => a.Id));
        var technician = await db.Technicians
            .Include(t => t.TechnicalSkills).Include(t => t.TechnicianAvailabilities)
            .SingleAsync(t => t.Id == "TECH-004");
        Assert.Equal(40.25m, technician.HourlyRate);
        Assert.Equal(new[] { "MECHANICAL", "SEALING" }, technician.TechnicalSkills.Select(s => s.SkillCode).Order());
        Assert.Equal(2, technician.TechnicianAvailabilities.Count);
        var task = await db.MaintenanceTaskDefinitions.SingleAsync(t => t.Id == "P102-SEAL-REPLACEMENT");
        Assert.Equal(("MECHANICAL,SEALING", "SEAL-102", 2m, "P102-ISOLATION"),
            (task.RequiredSkills, task.RequiredPartId, task.RequiredPartQuantity, task.PredecessorTaskId));
        Assert.Equal(new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.FromHours(5.5)),
            (await db.MaintenanceHistoryRecords.SingleAsync(h => h.Id == "H-P102-001")).PerformedAt);
        Assert.Equal(seededBefore, await fixture.SnapshotSeedAsync());
    }

    [Fact]
    public async Task InvalidAssetRows_RejectTheWholeCsv_AndReportEachRowWithItsReason()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        var report = await fixture.AssertRejectedAsync("assets", "assets.csv", """
            id,plantId,name,model,priority
            P-201,PLANT-01,Valid pump,CP-100,1
            P-202,PLANT-01,,CP-100,1
            P-201,PLANT-01,Duplicate pump,CP-100,1
            P-101,PLANT-01,Existing pump,CP-100,1
            P 204,PLANT-01,Bad id pump,CP-100,high
            """);

        Assert.Equal((5, 0, 4), (report.TotalRecords, report.ImportedRecords, report.InvalidRecords));
        AssertIssue(report, "row 3", "name", "name is required");
        AssertIssue(report, "row 4", "id", "also used at row 2");
        AssertIssue(report, "row 5", "id", "already exists");
        AssertIssue(report, "row 6", "id", "not a valid identifier");
        AssertIssue(report, "row 6", "priority", "must be a whole number");
        await using var db = fixture.CreateDbContext();
        Assert.Equal(new[] { "P-101" }, await db.Assets.Select(a => a.Id).ToListAsync());
    }

    [Fact]
    public async Task InvalidTaskRecords_AreReportedWithTheirReferenceUnitAndGranularityErrors_AndNothingIsSaved()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        await fixture.AssertImportedAsync("assets", "assets.json", 1, """
            [{ "id": "P-102", "plantId": "PLANT-01", "name": "Feed pump", "model": "CP-100", "priority": 2 }]
            """);

        var report = await fixture.AssertRejectedAsync("tasks", "tasks.json", """
            [
              { "id": "T-OK", "assetId": "P-101", "name": "Valid", "durationMinutes": 60, "requiredSkills": ["MECHANICAL"], "crewSize": 1, "sourceDocumentId": "MAN-TEST-001", "sourceSection": "Pump isolation" },
              { "id": "T-1", "assetId": "P-999", "name": "Unknown asset", "durationMinutes": 60, "requiredSkills": ["MECHANICAL"], "crewSize": 1, "sourceDocumentId": "MAN-TEST-001", "sourceSection": "Pump isolation" },
              { "id": "T-2", "assetId": "P-102", "name": "Incompatible part", "durationMinutes": 60, "requiredSkills": ["MECHANICAL"], "crewSize": 1, "requiredPartId": "BR-101", "requiredPartQuantity": 1, "sourceDocumentId": "MAN-TEST-001", "sourceSection": "Pump isolation" },
              { "id": "T-3", "assetId": "P-101", "name": "Off grid", "durationMinutes": 45, "requiredSkills": ["MECHANICAL"], "crewSize": 1, "sourceDocumentId": "MAN-TEST-001", "sourceSection": "Pump isolation" },
              { "id": "T-4", "assetId": "P-101", "name": "Missing predecessor", "durationMinutes": 60, "requiredSkills": ["MECHANICAL"], "crewSize": 1, "predecessorTaskId": "T-404", "sourceDocumentId": "MAN-TEST-001", "sourceSection": "Pump isolation" },
              { "id": "T-5", "assetId": "P-101", "name": "Wrong section", "durationMinutes": 60, "requiredSkills": ["MECHANICAL"], "crewSize": 1, "sourceDocumentId": "MAN-TEST-001", "sourceSection": "Seal replacement procedure" },
              { "id": "T-6", "assetId": "P-101", "name": "Cycle A", "durationMinutes": 60, "requiredSkills": ["MECHANICAL"], "crewSize": 1, "predecessorTaskId": "T-7", "sourceDocumentId": "MAN-TEST-001", "sourceSection": "Pump isolation" },
              { "id": "T-7", "assetId": "P-101", "name": "Cycle B", "durationMinutes": 60, "requiredSkills": ["MECHANICAL"], "crewSize": 1, "predecessorTaskId": "T-6", "sourceDocumentId": "MAN-TEST-001", "sourceSection": "Pump isolation" },
              { "id": "T-8", "assetId": "P-101", "name": "Half bearing", "durationMinutes": 60, "requiredSkills": ["MECHANICAL"], "crewSize": 1, "requiredPartId": "BR-101", "requiredPartQuantity": 1.5, "sourceDocumentId": "MAN-TEST-001", "sourceSection": "Pump isolation" },
              { "id": "T-9", "assetId": "P-101", "name": "Unknown property", "durationHours": 1 },
              { "id": "T-10", "assetId": "P-101", "name": "Unknown manual", "durationMinutes": 60, "requiredSkills": ["MECHANICAL"], "crewSize": 1, "sourceDocumentId": "MAN-NOPE", "sourceSection": "Pump isolation" }
            ]
            """);

        Assert.Equal((11, 0, 10), (report.TotalRecords, report.ImportedRecords, report.InvalidRecords));
        AssertIssue(report, "record 2", "assetId", "Asset 'P-999' does not exist");
        AssertIssue(report, "record 3", "requiredPartId", "not approved as compatible with asset 'P-102'");
        AssertIssue(report, "record 4", "durationMinutes", "multiple of the 30-minute scheduling granularity");
        AssertIssue(report, "record 5", "predecessorTaskId", "'T-404' does not exist");
        AssertIssue(report, "record 6", "sourceSection", "has no section titled 'Seal replacement procedure'");
        AssertIssue(report, "record 7", "predecessorTaskId", "predecessor cycle");
        AssertIssue(report, "record 8", "predecessorTaskId", "predecessor cycle");
        AssertIssue(report, "record 9", "requiredPartQuantity", "whole number of 'each'");
        AssertIssue(report, "record 10", "durationHours", "Unknown property 'durationHours'");
        AssertIssue(report, "record 11", "sourceDocumentId", "No manual 'MAN-NOPE'");
        Assert.DoesNotContain(report.Issues, issue => issue.Location == "record 1");
        await using var db = fixture.CreateDbContext();
        Assert.Equal(3, await db.MaintenanceTaskDefinitions.CountAsync());
    }

    [Fact]
    public async Task InvalidAvailabilityAndPartRows_ReportTimestampGridOverlapAndUnitErrors()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        var availability = await fixture.AssertRejectedAsync("technician-availability", "availability.csv", """
            technicianId,availableFrom,availableTo
            TECH-001,2026-10-25T08:00:00,2026-10-25T12:00:00+05:30
            TECH-001,2026-10-25T08:15:00+05:30,2026-10-25T12:00:00+05:30
            TECH-002,2026-10-25T12:00:00+05:30,2026-10-25T08:00:00+05:30
            TECH-003,2026-10-24T15:00:00+05:30,2026-10-24T17:00:00+05:30
            TECH-002,2026-10-26T08:00:00+05:30,2026-10-26T12:00:00+05:30
            TECH-002,2026-10-26T11:00:00+05:30,2026-10-26T13:00:00+05:30
            TECH-404,2026-10-26T08:00:00+05:30,2026-10-26T12:00:00+05:30
            """);
        AssertIssue(availability, "row 2", "availableFrom", "explicit UTC offset");
        AssertIssue(availability, "row 3", "availableFrom", "30-minute scheduling granularity");
        AssertIssue(availability, "row 4", "availableTo", "later than availableFrom");
        AssertIssue(availability, "row 5", "availableFrom", "overlaps saved availability");
        AssertIssue(availability, "row 7", "availableFrom", "overlaps the interval for TECH-002 at row 6");
        AssertIssue(availability, "row 8", "technicianId", "'TECH-404' does not exist");
        Assert.Equal(6, availability.InvalidRecords);

        var parts = await fixture.AssertRejectedAsync("spare-parts", "parts.csv", """
            id,name,unit,onHandQuantity,compatibleAssetIds
            GASKET-1,Gasket,box,3,P-101
            GASKET-2,Gasket,each,2.5,P-101
            GASKET-3,Gasket,each,-1,P-101
            GASKET-4,Gasket,each,1,P-101;P-404
            OIL-1,Oil,l,"1,5",P-101
            """);
        AssertIssue(parts, "row 2", "unit", "Unit 'box' is not supported");
        AssertIssue(parts, "row 3", "onHandQuantity", "whole number of 'each'");
        AssertIssue(parts, "row 4", "onHandQuantity", "zero or more");
        AssertIssue(parts, "row 5", "compatibleAssetIds", "Asset 'P-404' does not exist");
        AssertIssue(parts, "row 6", "onHandQuantity", "'.' as the decimal separator");

        await using var db = fixture.CreateDbContext();
        Assert.Equal(3, await db.TechnicianAvailabilities.CountAsync());
        Assert.Equal(new[] { "BR-101" }, await db.SpareParts.Select(p => p.Id).ToListAsync());
    }

    [Fact]
    public async Task MalformedFiles_AreRejectedWithFileAndRowIssues_AndBadUploadsReturnBadRequest()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        AssertIssue(await fixture.AssertRejectedAsync("assets", "assets.csv", "id,plantId,name,model,priority,colour\nP-301,PLANT-01,Pump,CP-100,1,red"),
            "file", null, "Unknown column 'colour'");
        AssertIssue(await fixture.AssertRejectedAsync("assets", "assets.csv", "id,plantId,name,model,priority\nP-301,PLANT-01,Pump,CP-100"),
            "row 2", null, "Expected 5 values but found 4");
        AssertIssue(await fixture.AssertRejectedAsync("assets", "assets.csv", "id,plantId,name,model,priority\n\"P-301,PLANT-01,Pump,CP-100,1"),
            "row 2", null, "not closed");
        AssertIssue(await fixture.AssertRejectedAsync("assets", "assets.json", "{ \"id\": \"P-301\" }"),
            "file", null, "must contain an array of records");
        AssertIssue(await fixture.AssertRejectedAsync("assets", "assets.json", "[{ \"id\": "),
            "file", null, "not valid JSON");
        AssertIssue(await fixture.AssertRejectedAsync("assets", "assets.json", "[\"P-301\"]"),
            "record 1", null, "must be a JSON object");
        AssertIssue(await fixture.AssertRejectedAsync("assets", "assets.csv", "id,plantId,name,model,priority\n"),
            "file", null, "no records");

        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.UploadAsync("work-orders", "orders.csv", "id\nW-1")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.UploadAsync("assets", "assets.txt", "id\nP-301")).Status);
        var noFile = await fixture.Client.PostAsync("/api/imports/assets", new MultipartFormDataContent { { new StringContent("x"), "other" } });
        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(new[] { "P-101" }, await db.Assets.Select(a => a.Id).ToListAsync());
    }

    [Fact]
    public async Task TextBasedPdfManual_IsImported_CitedByPageAndSection_AndUsableAsATaskSource()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var pdf = CreatePdf(
            ["Document ID: MAN-IMPORT-001", "Version: 2.0", "Section: Impeller inspection", "Inspect the impeller for wear."],
            ["Section: Impeller replacement", "Replace the impeller. Planning estimate: 120 minutes."]);

        var (status, report) = await fixture.UploadPdfAsync("manual.pdf", pdf, "MAN-IMPORT-001", "2.0");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(report.Imported);
        Assert.Equal((2, 2), (report.TotalRecords, report.ImportedRecords));
        var evidence = await fixture.Evidence.SearchAsync("Impeller replacement", "MAN-IMPORT-001", CancellationToken.None);
        var cited = Assert.Single(evidence, e => e.Section == "Impeller replacement");
        Assert.Equal(("MAN-IMPORT-001", "2.0", 2), (cited.DocumentId, cited.Version, cited.Page));
        Assert.Contains("Planning estimate: 120 minutes.", cited.Excerpt);

        await fixture.AssertImportedAsync("tasks", "tasks.csv", 1, $"""
            {TaskColumns}
            P101-IMPELLER,P-101,Impeller replacement,120,MECHANICAL,1,,,,MAN-IMPORT-001,Impeller replacement
            """);
    }

    [Fact]
    public async Task InvalidPdfManuals_AreRejectedWithPageIssues_AndNothingIsStored()
    {
        await using var fixture = await ImportFixture.CreateAsync();

        var (status, report) = await fixture.UploadPdfAsync(
            "manual.pdf",
            CreatePdf(["Document ID: MAN-OTHER-002", "Planning notes without any headings."], []),
            "MAN-IMPORT-002",
            "1.0");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.False(report.Imported);
        AssertIssue(report, "page 2", null, "no extractable text");
        AssertIssue(report, "page 1", "documentId", "declares document ID 'MAN-OTHER-002'");
        AssertIssue(report, "file", null, "no 'Section:' headings");

        var bundled = await fixture.UploadPdfAsync("manual.pdf", CreatePdf(["Section: Any", "Text."]), "MAN-TEST-001", "1.0");
        AssertIssue(bundled.Report, "file", "documentId", "already exists");

        var missingFields = await fixture.UploadPdfAsync("manual.pdf", CreatePdf(["Section: Any", "Text."]), "", " ");
        AssertIssue(missingFields.Report, "file", "documentId", "document ID is required");
        AssertIssue(missingFields.Report, "file", "version", "version is required");

        var notPdf = await fixture.UploadPdfAsync("manual.pdf", Encoding.UTF8.GetBytes("not a PDF"), "MAN-IMPORT-003", "1.0");
        AssertIssue(notPdf.Report, "file", null, "could not be read as a PDF");

        Assert.Empty(Directory.Exists(fixture.ManualDirectory) ? Directory.GetFiles(fixture.ManualDirectory) : []);
    }

    private static void AssertIssue(ImportReport report, string location, string? field, string messagePart) =>
        Assert.Contains(report.Issues, issue =>
            issue.Location == location &&
            issue.Field == field &&
            issue.Message.Contains(messagePart, StringComparison.Ordinal));

    // Each page is a list of lines; an empty page has no text at all, like a scanned page
    private static byte[] CreatePdf(params string[][] pages)
    {
        using var stream = new MemoryStream();
        var document = new Document(PageSize.A4);
        var writer = PdfWriter.GetInstance(document, stream);
        document.Open();
        for (var index = 0; index < pages.Length; index++)
        {
            if (index > 0)
                document.NewPage();
            if (pages[index].Length == 0)
                writer.PageEmpty = false;
            foreach (var line in pages[index])
                document.Add(new Paragraph(line));
        }

        document.Close();
        return stream.ToArray();
    }

    private sealed class ImportFixture : IAsyncDisposable
    {
        private const string TestManual = """
            Fictional planning reference: MAN-TEST-001
            Version: 1.0

            Section: Pump isolation
            Isolate and lock out the pump before work. Planning estimate: 30 minutes.

            Section: Seal replacement
            Replace the mechanical seal. Planning estimate: 90 minutes.
            """;

        private readonly string _root;
        private readonly string _connectionString;
        private readonly WebApplication _app;

        private ImportFixture(string root, string connectionString, WebApplication app)
        {
            _root = root;
            _connectionString = connectionString;
            _app = app;
            Client = app.GetTestClient();
            Evidence = app.Services.GetRequiredService<IManualEvidenceRetriever>();
        }

        public HttpClient Client { get; }

        public IManualEvidenceRetriever Evidence { get; }

        public string ManualDirectory => Path.Combine(_root, "imported-manuals");

        public static async Task<ImportFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"maintenance-planner-fr02-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var manualPath = Path.Combine(root, "MAN-TEST-001.txt");
            await File.WriteAllTextAsync(manualPath, TestManual);
            var databasePath = Path.Combine(root, "planner.db");
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
            builder.Services.AddSingleton(new ImportedManualStore(Path.Combine(root, "imported-manuals"), ["MAN-TEST-001"]));
            builder.Services.AddSingleton<ImportedManualEvidenceRetriever>();
            builder.Services.AddSingleton<IManualEvidenceRetriever>(services => new CompositeManualEvidenceRetriever(
            [
                new TextManualEvidenceRetriever(manualPath),
                services.GetRequiredService<ImportedManualEvidenceRetriever>()
            ]));
            builder.Services.AddScoped<IReferenceDataImportService, ReferenceDataImportService>();
            builder.Services.AddScoped<IManualImportService, ManualImportService>();
            var app = builder.Build();
            app.MapImportEndpoints();
            await app.StartAsync();
            return new ImportFixture(root, connectionString, app);
        }

        public MaintenancePlannerDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<MaintenancePlannerDbContext>().UseSqlite(_connectionString).Options);

        public async Task<(HttpStatusCode Status, ImportReport Report)> UploadAsync(string dataset, string fileName, string content)
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", fileName } };
            var response = await Client.PostAsync($"/api/imports/{dataset}", form);
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<ImportReport>())!);
        }

        public async Task<(HttpStatusCode Status, ImportReport Report)> UploadPdfAsync(
            string fileName,
            byte[] pdf,
            string documentId,
            string version)
        {
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(pdf), "file", fileName },
                { new StringContent(documentId), "documentId" },
                { new StringContent(version), "version" }
            };
            var response = await Client.PostAsync("/api/imports/manuals", form);
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<ImportReport>())!);
        }

        public async Task AssertImportedAsync(string dataset, string fileName, int expectedRecords, string content)
        {
            var (status, report) = await UploadAsync(dataset, fileName, content);
            Assert.True(status == HttpStatusCode.OK,
                $"{dataset} import failed: {string.Join("; ", report.Issues.Select(i => $"{i.Location} {i.Field}: {i.Message}"))}");
            Assert.True(report.Imported);
            Assert.Empty(report.Issues);
            Assert.Equal((expectedRecords, expectedRecords), (report.TotalRecords, report.ImportedRecords));
        }

        public async Task<ImportReport> AssertRejectedAsync(string dataset, string fileName, string content)
        {
            var (status, report) = await UploadAsync(dataset, fileName, content);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.False(report.Imported);
            Assert.Equal(0, report.ImportedRecords);
            Assert.NotEmpty(report.Issues);
            return report;
        }

        // The seeded P-101 records, including timestamps, as one comparable string
        public async Task<string> SnapshotSeedAsync()
        {
            await using var db = CreateDbContext();
            var asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == "P-101");
            var part = await db.SpareParts.AsNoTracking().SingleAsync(p => p.Id == "BR-101");
            var tasks = await db.MaintenanceTaskDefinitions.AsNoTracking()
                .Where(t => t.AssetId == "P-101").OrderBy(t => t.Id)
                .Select(t => $"{t.Id}|{t.DurationMinutes}|{t.RequiredSkills}|{t.RequiredPartId}|{t.PredecessorTaskId}")
                .ToListAsync();
            string[] seededTechnicians = ["TECH-001", "TECH-002", "TECH-003"];
            var availability = (await db.TechnicianAvailabilities.AsNoTracking()
                    .Where(a => seededTechnicians.Contains(a.TechnicianId)).ToListAsync())
                .Select(a => $"{a.TechnicianId}|{a.AvailableFrom:O}|{a.AvailableTo:O}").Order();
            var windows = (await db.MaintenanceWindows.AsNoTracking().ToListAsync())
                .Select(w => $"{w.Id}|{w.Start:O}|{w.End:O}").Order();
            return string.Join("\n",
                [$"{asset.Name}|{asset.Model}|{asset.Priority}", $"{part.OnHandQuantity}|{part.ReservedQuantity}|{part.Unit}",
                 .. tasks, .. availability, .. windows]);
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
