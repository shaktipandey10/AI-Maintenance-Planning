using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MaintenancePlanner.Application.Imports;
using MaintenancePlanner.Application.Planning;
using MaintenancePlanner.Domain.Entities;
using MaintenancePlanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MaintenancePlanner.Infrastructure.Imports;

/// <summary>
/// Imports assets, spare parts, technicians, technician availability, tasks and maintenance history from CSV or JSON
/// (FR02). Every record is validated for required fields, identifiers, units, the scheduling time granularity and its
/// references to saved records, to other records in the same file and, for tasks, to a section of a known manual.
/// Imports are all-or-nothing and insert-only: if any record is invalid nothing is saved, and records whose ID
/// already exists are rejected, so existing data is never changed.
/// </summary>
public sealed class ReferenceDataImportService(
    MaintenancePlannerDbContext dbContext,
    IManualEvidenceRetriever manualEvidence) : IReferenceDataImportService
{
    public const long MaximumFileBytes = 10 * 1024 * 1024;

    /// <summary>Units accepted for spare parts; count units require whole quantities.</summary>
    public static readonly IReadOnlyCollection<string> PartUnits = ["each", "pair", "set", "kg", "g", "l", "ml", "m"];
    private static readonly HashSet<string> CountUnits = new(["each", "pair", "set"], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<ImportDataset, string[]> Columns = new Dictionary<ImportDataset, string[]>
    {
        [ImportDataset.Assets] = ["id", "plantId", "name", "model", "priority"],
        [ImportDataset.SpareParts] = ["id", "name", "unit", "onHandQuantity", "compatibleAssetIds"],
        [ImportDataset.Technicians] = ["id", "name", "hourlyRate", "skills"],
        [ImportDataset.TechnicianAvailability] = ["technicianId", "availableFrom", "availableTo"],
        [ImportDataset.Tasks] =
        [
            "id", "assetId", "name", "durationMinutes", "requiredSkills", "crewSize", "requiredPartId",
            "requiredPartQuantity", "predecessorTaskId", "sourceDocumentId", "sourceSection"
        ],
        [ImportDataset.MaintenanceHistory] =
            ["id", "assetId", "performedAt", "summary", "outcome", "sourceDocumentId", "sourceReference"]
    };

    public static IReadOnlyList<string> ColumnsOf(ImportDataset dataset) => Columns[dataset];

    public async Task<ImportReport> ImportAsync(
        ImportDataset dataset,
        ImportFormat format,
        string? fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        var datasetName = ImportDatasets.Slug(dataset);
        var formatName = format.ToString().ToLowerInvariant();
        if (format is not (ImportFormat.Csv or ImportFormat.Json))
            return ImportReport.Rejected(datasetName, formatName, fileName, "Structured data must be CSV or JSON.");

        string text;
        using (var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync(cancellationToken);

        var read = ImportRecordReader.Read(format, text, Columns[dataset]);
        var issues = read.Issues.ToList();
        var entities = dataset switch
        {
            ImportDataset.Assets => await ValidateAssetsAsync(read.Records, issues, cancellationToken),
            ImportDataset.SpareParts => await ValidateSparePartsAsync(read.Records, issues, cancellationToken),
            ImportDataset.Technicians => await ValidateTechniciansAsync(read.Records, issues, cancellationToken),
            ImportDataset.TechnicianAvailability => await ValidateAvailabilityAsync(read.Records, issues, cancellationToken),
            ImportDataset.Tasks => await ValidateTasksAsync(read.Records, issues, cancellationToken),
            ImportDataset.MaintenanceHistory => await ValidateHistoryAsync(read.Records, issues, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, null)
        };

        if (issues.Count > 0)
        {
            dbContext.ChangeTracker.Clear();
            return Report(datasetName, formatName, fileName, read.TotalRecords, 0, issues);
        }

        try
        {
            await dbContext.AddRangeAsync(entities, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return Report(datasetName, formatName, fileName, read.TotalRecords, 0,
            [
                new ImportIssue(null, "file", null, null,
                    "The records conflict with data saved by another change at the same time; nothing was imported. " +
                    "Retry the import.")
            ]);
        }

        return Report(datasetName, formatName, fileName, read.TotalRecords, entities.Count, []);
    }

    private async Task<List<object>> ValidateAssetsAsync(
        IReadOnlyList<ImportRecord> records,
        List<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var existing = await ExistingIdsAsync(dbContext.Assets.Select(asset => asset.Id), cancellationToken);
        var entities = new List<object>();
        var seen = new Dictionary<string, ImportRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var fields = new RecordFields(record, issues);
            var id = NewId(fields, "id", existing, seen, "An asset");
            var plantId = fields.Identifier("plantId");
            var name = fields.Text("name");
            var model = fields.Text("model");
            var priority = fields.Integer("priority", minimum: 1, maximum: 99);
            if (fields.IsValid)
                entities.Add(new Asset { Id = id!, PlantId = plantId!, Name = name!, Model = model!, Priority = priority!.Value });
        }

        return entities;
    }

    private async Task<List<object>> ValidateSparePartsAsync(
        IReadOnlyList<ImportRecord> records,
        List<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var existing = await ExistingIdsAsync(dbContext.SpareParts.Select(part => part.Id), cancellationToken);
        var assets = await dbContext.Assets.ToDictionaryAsync(asset => asset.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var entities = new List<object>();
        var seen = new Dictionary<string, ImportRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var fields = new RecordFields(record, issues);
            var id = NewId(fields, "id", existing, seen, "A spare part");
            var name = fields.Text("name");
            var unit = fields.Unit("unit");
            var onHand = fields.Quantity("onHandQuantity", unit, allowZero: true);
            var compatibleAssetIds = fields.IdentifierList("compatibleAssetIds");
            foreach (var assetId in compatibleAssetIds ?? [])
            {
                if (!assets.ContainsKey(assetId))
                    fields.Fail("compatibleAssetIds", $"Asset '{assetId}' does not exist.");
            }

            if (fields.IsValid)
            {
                entities.Add(new SparePart
                {
                    Id = id!,
                    Name = name!,
                    Unit = unit!,
                    OnHandQuantity = onHand!.Value,
                    ReservedQuantity = 0,
                    CompatibleAssets = compatibleAssetIds!.Select(assetId => assets[assetId]).ToList()
                });
            }
        }

        return entities;
    }

    private async Task<List<object>> ValidateTechniciansAsync(
        IReadOnlyList<ImportRecord> records,
        List<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var existing = await ExistingIdsAsync(dbContext.Technicians.Select(technician => technician.Id), cancellationToken);
        var entities = new List<object>();
        var seen = new Dictionary<string, ImportRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var fields = new RecordFields(record, issues);
            var id = NewId(fields, "id", existing, seen, "A technician");
            var name = fields.Text("name");
            var hourlyRate = fields.Money("hourlyRate");
            var skills = fields.IdentifierList("skills");
            if (fields.IsValid)
            {
                // Built like the seed: skills are added through the technician so EF links them to it
                entities.Add(new Technician
                {
                    Id = id!,
                    Name = name!,
                    HourlyRate = hourlyRate!.Value,
                    TechnicalSkills = skills!.Select(skill => new TechnicalSkill { TechnicianId = id!, SkillCode = skill }).ToList()
                });
            }
        }

        return entities;
    }

    private async Task<List<object>> ValidateAvailabilityAsync(
        IReadOnlyList<ImportRecord> records,
        List<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var technicians = await dbContext.Technicians
            .Include(technician => technician.TechnicianAvailabilities)
            .ToDictionaryAsync(technician => technician.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var accepted = new List<(ImportRecord Record, TechnicianAvailability Availability)>();
        var entities = new List<object>();
        foreach (var record in records)
        {
            var fields = new RecordFields(record, issues);
            var technicianId = fields.Identifier("technicianId");
            fields.RecordId = technicianId;
            var from = fields.Timestamp("availableFrom", onSchedulingGrid: true);
            var to = fields.Timestamp("availableTo", onSchedulingGrid: true);
            if (technicianId is not null && !technicians.ContainsKey(technicianId))
                fields.Fail("technicianId", $"Technician '{technicianId}' does not exist.");
            if (from is not null && to is not null && from >= to)
                fields.Fail("availableTo", "availableTo must be later than availableFrom.");
            if (!fields.IsValid)
                continue;

            var technician = technicians[technicianId!];
            var saved = technician.TechnicianAvailabilities.FirstOrDefault(interval =>
                interval.AvailableFrom < to && from < interval.AvailableTo);
            if (saved is not null)
            {
                fields.Fail("availableFrom",
                    $"The interval overlaps saved availability {saved.AvailableFrom:O} to {saved.AvailableTo:O} for {technician.Id}.");
                continue;
            }

            var inFile = accepted.FirstOrDefault(other =>
                string.Equals(other.Availability.TechnicianId, technician.Id, StringComparison.OrdinalIgnoreCase) &&
                other.Availability.AvailableFrom < to && from < other.Availability.AvailableTo);
            if (inFile.Record is not null)
            {
                fields.Fail("availableFrom", $"The interval overlaps the interval for {technician.Id} at {inFile.Record.Location}.");
                continue;
            }

            // The technician is tracked, so the interval is linked to it rather than to a new technician
            var availability = new TechnicianAvailability
            {
                TechnicianId = technician.Id,
                Technician = technician,
                AvailableFrom = from!.Value,
                AvailableTo = to!.Value
            };
            accepted.Add((record, availability));
            entities.Add(availability);
        }

        return entities;
    }

    private async Task<List<object>> ValidateTasksAsync(
        IReadOnlyList<ImportRecord> records,
        List<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var existing = await ExistingIdsAsync(dbContext.MaintenanceTaskDefinitions.Select(task => task.Id), cancellationToken);
        var assetIds = await ExistingIdsAsync(dbContext.Assets.Select(asset => asset.Id), cancellationToken);
        var parts = await dbContext.SpareParts
            .AsNoTracking()
            .Include(part => part.CompatibleAssets)
            .ToDictionaryAsync(part => part.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var seen = new Dictionary<string, ImportRecord>(StringComparer.OrdinalIgnoreCase);
        var fileIds = records
            .Select(record => record.Fields.GetValueOrDefault("id")?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<(RecordFields Fields, MaintenanceTaskDefinition Task)>();

        foreach (var record in records)
        {
            var fields = new RecordFields(record, issues);
            var id = NewId(fields, "id", existing, seen, "A task");
            var assetId = fields.Identifier("assetId");
            var name = fields.Text("name");
            var duration = fields.Integer("durationMinutes", minimum: 1, maximum: 24 * 60);
            var requiredSkills = fields.IdentifierList("requiredSkills");
            var crewSize = fields.Integer("crewSize", minimum: 1, maximum: 10);
            var partId = fields.Identifier("requiredPartId", required: false);
            var predecessorId = fields.Identifier("predecessorTaskId", required: false);
            var sourceDocumentId = fields.Identifier("sourceDocumentId");
            var sourceSection = fields.Text("sourceSection");

            if (assetId is not null && !assetIds.Contains(assetId))
                fields.Fail("assetId", $"Asset '{assetId}' does not exist.");

            var increment = (int)DeterministicPlanningScheduler.SchedulingIncrement.TotalMinutes;
            if (duration is not null && duration % increment != 0)
                fields.Fail("durationMinutes", $"Durations must be a multiple of the {increment}-minute scheduling granularity.");

            decimal partQuantity = 0;
            if (partId is null)
            {
                if (!string.IsNullOrWhiteSpace(fields.Raw("requiredPartQuantity")) &&
                    fields.Quantity("requiredPartQuantity", unit: null, allowZero: true) is not (null or 0))
                {
                    fields.Fail("requiredPartQuantity", "A part quantity needs a requiredPartId.");
                }
            }
            else if (!parts.TryGetValue(partId, out var part))
            {
                fields.Fail("requiredPartId", $"Spare part '{partId}' does not exist.");
            }
            else
            {
                partQuantity = fields.Quantity("requiredPartQuantity", part.Unit, allowZero: false) ?? 0;
                if (assetId is not null && assetIds.Contains(assetId) &&
                    !part.CompatibleAssets.Any(asset => string.Equals(asset.Id, assetId, StringComparison.OrdinalIgnoreCase)))
                {
                    fields.Fail("requiredPartId", $"Spare part '{partId}' is not approved as compatible with asset '{assetId}'.");
                }
            }

            if (predecessorId is not null)
            {
                if (id is not null && string.Equals(predecessorId, id, StringComparison.OrdinalIgnoreCase))
                    fields.Fail("predecessorTaskId", "A task cannot be its own predecessor.");
                else if (!existing.Contains(predecessorId) && !fileIds.Contains(predecessorId))
                    fields.Fail("predecessorTaskId", $"Predecessor task '{predecessorId}' does not exist.");
            }

            if (sourceDocumentId is not null && sourceSection is not null)
                await CheckManualSectionAsync(fields, sourceDocumentId, sourceSection, cancellationToken);

            if (fields.IsValid)
            {
                tasks.Add((fields, new MaintenanceTaskDefinition
                {
                    Id = id!,
                    AssetId = assetId!,
                    Name = name!,
                    DurationMinutes = duration!.Value,
                    // Stored comma separated, as the scheduler and validators read it
                    RequiredSkills = string.Join(',', requiredSkills!),
                    RequiredCrewSize = crewSize!.Value,
                    RequiredPartId = partId,
                    RequiredPartQuantity = partQuantity,
                    PredecessorTaskId = predecessorId,
                    SourceDocumentId = sourceDocumentId!,
                    SourceSection = sourceSection!
                }));
            }
        }

        // Saved tasks cannot point at new ones, so a predecessor cycle can only run through tasks in this file
        var predecessors = tasks.ToDictionary(
            entry => entry.Task.Id,
            entry => entry.Task.PredecessorTaskId,
            StringComparer.OrdinalIgnoreCase);
        foreach (var (fields, task) in tasks)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { task.Id };
            for (var next = task.PredecessorTaskId;
                 next is not null && predecessors.TryGetValue(next, out var following);
                 next = following)
            {
                if (!visited.Add(next))
                {
                    fields.Fail("predecessorTaskId", $"Task '{task.Id}' is part of a predecessor cycle.");
                    break;
                }
            }
        }

        return tasks.Where(entry => entry.Fields.IsValid).Select(entry => (object)entry.Task).ToList();
    }

    private async Task<List<object>> ValidateHistoryAsync(
        IReadOnlyList<ImportRecord> records,
        List<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var existing = await ExistingIdsAsync(dbContext.MaintenanceHistoryRecords.Select(record => record.Id), cancellationToken);
        var assetIds = await ExistingIdsAsync(dbContext.Assets.Select(asset => asset.Id), cancellationToken);
        var entities = new List<object>();
        var seen = new Dictionary<string, ImportRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var fields = new RecordFields(record, issues);
            var id = NewId(fields, "id", existing, seen, "A history record");
            var assetId = fields.Identifier("assetId");
            var performedAt = fields.Timestamp("performedAt", onSchedulingGrid: false);
            var summary = fields.Text("summary", maximumLength: 2000);
            var outcome = fields.Text("outcome", maximumLength: 2000);
            var sourceDocumentId = fields.Identifier("sourceDocumentId", required: false);
            var sourceReference = fields.Text("sourceReference", required: false);

            if (assetId is not null && !assetIds.Contains(assetId))
                fields.Fail("assetId", $"Asset '{assetId}' does not exist.");
            if (performedAt > DateTimeOffset.UtcNow)
                fields.Fail("performedAt", "A history record cannot be dated in the future.");

            if (fields.IsValid)
            {
                entities.Add(new MaintenanceHistoryRecord
                {
                    Id = id!,
                    AssetId = assetId!,
                    PerformedAt = performedAt!.Value,
                    Summary = summary!,
                    Outcome = outcome!,
                    SourceDocumentId = sourceDocumentId ?? string.Empty,
                    SourceReference = sourceReference ?? string.Empty
                });
            }
        }

        return entities;
    }

    // A task must cite a section that the evidence retrievers used by planning can actually return
    private async Task CheckManualSectionAsync(
        RecordFields fields,
        string documentId,
        string section,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PlanningEvidenceReference> evidence;
        try
        {
            evidence = await manualEvidence.SearchAsync(section, documentId, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException)
        {
            fields.Fail("sourceDocumentId", $"Manual '{documentId}' could not be read.");
            return;
        }

        var cited = evidence.Where(reference =>
            reference.SourceKind == PlanningEvidenceSourceKind.MaintenanceManual &&
            string.Equals(reference.DocumentId, documentId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (cited.Length == 0)
        {
            fields.Fail("sourceDocumentId", $"No manual '{documentId}' with a section matching '{section}' is available.");
        }
        else if (!cited.Any(reference => string.Equals(reference.Section?.Trim(), section, StringComparison.OrdinalIgnoreCase)))
        {
            fields.Fail("sourceSection", $"Manual '{documentId}' has no section titled '{section}'.");
        }
    }

    private static string? NewId(
        RecordFields fields,
        string field,
        HashSet<string> existing,
        Dictionary<string, ImportRecord> seen,
        string kind)
    {
        var id = fields.Identifier(field);
        fields.RecordId = id;
        if (id is null)
            return null;

        if (existing.Contains(id))
            fields.Fail(field, $"{kind} with ID '{id}' already exists; imports add records and never change saved ones.");
        else if (seen.TryGetValue(id, out var first))
            fields.Fail(field, $"ID '{id}' is also used at {first.Location}.");
        else
            seen[id] = fields.Record;

        return id;
    }

    private static async Task<HashSet<string>> ExistingIdsAsync(IQueryable<string> ids, CancellationToken cancellationToken) =>
        (await ids.ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static ImportReport Report(
        string dataset,
        string format,
        string? fileName,
        int total,
        int imported,
        IReadOnlyList<ImportIssue> issues) =>
        new(dataset, format, fileName, issues.Count == 0 && imported > 0, total, imported,
            issues.Where(issue => issue.RecordNumber is not null).Select(issue => issue.RecordNumber).Distinct().Count(),
            issues.OrderBy(issue => issue.RecordNumber ?? 0).ToArray());

    /// <summary>Reads and checks the fields of one record, adding an issue for each problem found.</summary>
    private sealed class RecordFields(ImportRecord record, List<ImportIssue> issues)
    {
        private static readonly Regex TimestampPattern = new(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$",
            RegexOptions.CultureInvariant);

        private bool _failed;

        public ImportRecord Record => record;

        public string? RecordId { get; set; }

        public bool IsValid => !_failed;

        public void Fail(string? field, string message)
        {
            _failed = true;
            issues.Add(new ImportIssue(record.RecordNumber, record.Location, RecordId, field, message));
        }

        public string? Raw(string field) => record.Fields.GetValueOrDefault(field);

        public string? Text(string field, bool required = true, int maximumLength = 200)
        {
            var value = Raw(field)?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (required)
                    Fail(field, $"{field} is required.");
                return null;
            }

            if (value.Length > maximumLength)
            {
                Fail(field, $"{field} must be at most {maximumLength} characters.");
                return null;
            }

            return value;
        }

        public string? Identifier(string field, bool required = true)
        {
            var value = Text(field, required, maximumLength: 64);
            if (value is not null && !ImportIdentifiers.IsValid(value))
            {
                Fail(field, $"'{value}' is not a valid identifier. {ImportIdentifiers.Rule}");
                return null;
            }

            return value;
        }

        public IReadOnlyList<string>? IdentifierList(string field)
        {
            var value = Text(field, maximumLength: 2000);
            if (value is null)
                return null;

            var items = value.Split(ImportRecordReader.ListSeparator, StringSplitOptions.TrimEntries);
            if (items.Any(item => item.Length == 0))
            {
                Fail(field, $"{field} contains an empty entry; separate values with '{ImportRecordReader.ListSeparator}'.");
                return null;
            }

            var invalid = items.Where(item => !ImportIdentifiers.IsValid(item)).ToArray();
            if (invalid.Length > 0)
            {
                Fail(field, $"{field} contains invalid identifiers: {string.Join(", ", invalid)}. {ImportIdentifiers.Rule}");
                return null;
            }

            var duplicates = items.GroupBy(item => item, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (duplicates.Length > 0)
            {
                Fail(field, $"{field} lists {string.Join(", ", duplicates)} more than once.");
                return null;
            }

            return items;
        }

        public int? Integer(string field, int minimum, int maximum)
        {
            var value = Text(field);
            if (value is null)
                return null;

            if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            {
                Fail(field, $"{field} must be a whole number, but was '{value}'.");
                return null;
            }

            if (number < minimum || number > maximum)
            {
                Fail(field, $"{field} must be between {minimum} and {maximum}, but was {number}.");
                return null;
            }

            return number;
        }

        public decimal? Money(string field)
        {
            var value = Decimal(field);
            if (value is null)
                return null;

            if (value <= 0 || decimal.Round(value.Value, 2) != value)
            {
                Fail(field, $"{field} must be a positive amount with at most two decimal places, but was {value}.");
                return null;
            }

            return value;
        }

        public string? Unit(string field)
        {
            var value = Text(field);
            if (value is null)
                return null;

            var unit = PartUnits.FirstOrDefault(known => string.Equals(known, value, StringComparison.OrdinalIgnoreCase));
            if (unit is null)
            {
                Fail(field, $"Unit '{value}' is not supported. Supported units: {string.Join(", ", PartUnits)}.");
                return null;
            }

            return unit;
        }

        public decimal? Quantity(string field, string? unit, bool allowZero)
        {
            var value = Decimal(field);
            if (value is null)
                return null;

            if (value < 0 || (!allowZero && value == 0))
            {
                Fail(field, $"{field} must be {(allowZero ? "zero or more" : "greater than zero")}, but was {value}.");
                return null;
            }

            if (unit is not null && CountUnits.Contains(unit) && decimal.Truncate(value.Value) != value)
            {
                Fail(field, $"{field} must be a whole number of '{unit}', but was {value}.");
                return null;
            }

            return value;
        }

        public DateTimeOffset? Timestamp(string field, bool onSchedulingGrid)
        {
            var value = Text(field);
            if (value is null)
                return null;

            if (!TimestampPattern.IsMatch(value) ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            {
                Fail(field, $"{field} must be an ISO 8601 date and time with an explicit UTC offset " +
                            $"(e.g. 2026-10-24T08:00:00+05:30), but was '{value}'.");
                return null;
            }

            var increment = DeterministicPlanningScheduler.SchedulingIncrement;
            if (onSchedulingGrid && timestamp.TimeOfDay.Ticks % increment.Ticks != 0)
            {
                Fail(field, $"{field} must fall on the {increment.TotalMinutes}-minute scheduling granularity, but was '{value}'.");
                return null;
            }

            return timestamp;
        }

        private decimal? Decimal(string field)
        {
            var value = Text(field);
            if (value is null)
                return null;

            if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var number))
            {
                Fail(field, $"{field} must be a number using '.' as the decimal separator, but was '{value}'.");
                return null;
            }

            return number;
        }
    }
}
