using System.Text;
using System.Text.Json;

namespace MaintenancePlanner.Application.Imports;

/// <summary>One input record with its fields as text, keyed case-insensitively by column or property name.</summary>
public sealed record ImportRecord(int RecordNumber, string Location, IReadOnlyDictionary<string, string?> Fields);

/// <summary>
/// The records read from a file. Issues holds file-level problems and records that could not be read at all
/// (wrong column count, wrong JSON shape); those records are not in Records but count towards TotalRecords.
/// </summary>
public sealed record ImportRecordSet(
    int TotalRecords,
    IReadOnlyList<ImportRecord> Records,
    IReadOnlyList<ImportIssue> Issues);

/// <summary>
/// Reads CSV (RFC 4180: header row, comma separated, double-quoted fields) and JSON (an array of objects) into the
/// same record shape, so both formats go through one validator. List values are separated by ';' in CSV and are
/// JSON arrays in JSON. Unknown columns or properties are reported rather than ignored.
/// </summary>
public static class ImportRecordReader
{
    public const char ListSeparator = ';';

    public static ImportRecordSet Read(ImportFormat format, string text, IReadOnlyCollection<string> columns) =>
        format switch
        {
            ImportFormat.Csv => ReadCsv(text, columns),
            ImportFormat.Json => ReadJson(text, columns),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Only CSV and JSON hold records.")
        };

    public static ImportRecordSet ReadCsv(string text, IReadOnlyCollection<string> columns)
    {
        var issues = new List<ImportIssue>();
        var rows = ParseCsvRows(text.TrimStart('﻿'), issues);
        if (issues.Count > 0)
            return new ImportRecordSet(0, [], issues);

        if (rows.Count == 0)
            return new ImportRecordSet(0, [], [FileIssue("The file is empty; expected a header row and records.")]);

        var header = rows[0].Values.Select(value => value.Trim()).ToArray();
        var known = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        foreach (var column in header.Where(column => !known.Contains(column)))
        {
            issues.Add(FileIssue(column.Length == 0
                ? "The header contains an empty column name."
                : $"Unknown column '{column}'. Expected columns: {string.Join(", ", columns)}."));
        }

        foreach (var duplicate in header.GroupBy(column => column, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Key.Length > 0 && group.Count() > 1))
        {
            issues.Add(FileIssue($"Column '{duplicate.Key}' appears more than once in the header."));
        }

        if (issues.Count > 0)
            return new ImportRecordSet(rows.Count - 1, [], issues);

        var records = new List<ImportRecord>();
        foreach (var row in rows.Skip(1))
        {
            var location = $"row {row.LineNumber}";
            if (row.Values.Count != header.Length)
            {
                issues.Add(new ImportIssue(row.LineNumber, location, null, null,
                    $"Expected {header.Length} values but found {row.Values.Count}."));
                continue;
            }

            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < header.Length; index++)
                fields[header[index]] = row.Values[index];
            records.Add(new ImportRecord(row.LineNumber, location, fields));
        }

        if (rows.Count == 1)
            issues.Add(FileIssue("The file has a header row but no records."));

        return new ImportRecordSet(rows.Count - 1, records, issues);
    }

    public static ImportRecordSet ReadJson(string text, IReadOnlyCollection<string> columns)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text.TrimStart('﻿'));
        }
        catch (JsonException exception)
        {
            return new ImportRecordSet(0, [], [FileIssue($"The file is not valid JSON: {exception.Message}")]);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new ImportRecordSet(0, [], [FileIssue("The JSON file must contain an array of records.")]);

            var known = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
            var issues = new List<ImportIssue>();
            var records = new List<ImportRecord>();
            var recordNumber = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                recordNumber++;
                var location = $"record {recordNumber}";
                if (element.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new ImportIssue(recordNumber, location, null, null, "The record must be a JSON object."));
                    continue;
                }

                var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var readable = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (!known.Contains(property.Name))
                    {
                        issues.Add(new ImportIssue(recordNumber, location, null, property.Name,
                            $"Unknown property '{property.Name}'. Expected properties: {string.Join(", ", columns)}."));
                        readable = false;
                        continue;
                    }

                    if (fields.ContainsKey(property.Name))
                    {
                        issues.Add(new ImportIssue(recordNumber, location, null, property.Name,
                            $"Property '{property.Name}' appears more than once."));
                        readable = false;
                        continue;
                    }

                    if (!TryReadJsonValue(property.Value, out var value))
                    {
                        issues.Add(new ImportIssue(recordNumber, location, null, property.Name,
                            "Values must be strings, numbers, booleans, null or arrays of strings and numbers."));
                        readable = false;
                        continue;
                    }

                    fields[property.Name] = value;
                }

                if (readable)
                    records.Add(new ImportRecord(recordNumber, location, fields));
            }

            if (recordNumber == 0)
                issues.Add(FileIssue("The JSON array contains no records."));

            return new ImportRecordSet(recordNumber, records, issues);
        }
    }

    private static bool TryReadJsonValue(JsonElement value, out string? text)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                text = null;
                return true;
            case JsonValueKind.String:
                text = value.GetString();
                return true;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                text = value.GetRawText();
                return true;
            case JsonValueKind.Array:
                var items = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                    {
                        text = null;
                        return false;
                    }

                    items.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText());
                }

                text = string.Join(ListSeparator, items);
                return true;
            default:
                text = null;
                return false;
        }
    }

    private static List<CsvRow> ParseCsvRows(string text, List<ImportIssue> issues)
    {
        var rows = new List<CsvRow>();
        var values = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var rowStartLine = 1;
        var inQuotes = false;
        var fieldWasQuoted = false;

        void EndRow()
        {
            values.Add(field.ToString());
            field.Clear();
            fieldWasQuoted = false;
            // Blank lines separate nothing and are skipped rather than read as empty records
            if (values.Count > 1 || values[0].Length > 0)
                rows.Add(new CsvRow(rowStartLine, values.ToList()));
            values.Clear();
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (character == '\n')
                        line++;
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0 && !fieldWasQuoted:
                    inQuotes = true;
                    fieldWasQuoted = true;
                    break;
                case '"':
                    issues.Add(new ImportIssue(line, $"row {line}", null, null,
                        "A double quote may only start a field; quote the whole field and double any quotes inside it."));
                    return rows;
                case ',':
                    values.Add(field.ToString());
                    field.Clear();
                    fieldWasQuoted = false;
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow();
                    line++;
                    rowStartLine = line;
                    break;
                default:
                    if (fieldWasQuoted)
                    {
                        issues.Add(new ImportIssue(line, $"row {line}", null, null,
                            "Unexpected text after a closing double quote."));
                        return rows;
                    }

                    field.Append(character);
                    break;
            }
        }

        if (inQuotes)
        {
            issues.Add(new ImportIssue(rowStartLine, $"row {rowStartLine}", null, null,
                "A quoted field is not closed before the end of the file."));
            return rows;
        }

        EndRow();
        return rows;
    }

    private static ImportIssue FileIssue(string message) => new(null, "file", null, null, message);

    private sealed record CsvRow(int LineNumber, IReadOnlyList<string> Values);
}
