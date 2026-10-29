using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MaintenancePlanner.Application.Planning.Tools;

/// <summary>
/// Strict reader for a tool's arguments object. Unknown properties, wrong types, blank strings, overlong values and
/// duplicate identifiers are errors; the first error is reported and nothing is guessed.
/// </summary>
internal sealed class PlanningToolArguments
{
    public const int MaximumIdentifierLength = 128;
    public const int MaximumListLength = 50;

    private readonly JsonElement _arguments;

    private PlanningToolArguments(JsonElement arguments, string? error)
    {
        _arguments = arguments;
        Error = error;
    }

    /// <summary>The first validation error, or null while the arguments are valid.</summary>
    public string? Error { get; private set; }

    public static PlanningToolArguments Read(JsonElement arguments, IReadOnlyCollection<string> allowedProperties)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return new PlanningToolArguments(arguments, "Arguments must be a JSON object.");

        var unknown = arguments.EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(name => !allowedProperties.Contains(name, StringComparer.Ordinal));
        return new PlanningToolArguments(
            arguments,
            unknown is null ? null : $"Unknown argument '{unknown}'.");
    }

    public string? OptionalIdentifier(string name)
    {
        if (Error is not null || !_arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return ReadIdentifier(value, name);
    }

    public string? RequiredIdentifier(string name)
    {
        if (Error is not null)
            return null;

        if (!_arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            Error = $"Argument '{name}' is required.";
            return null;
        }

        return ReadIdentifier(value, name);
    }

    public IReadOnlyList<string>? OptionalIdentifierList(string name)
    {
        if (Error is not null || !_arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return ReadIdentifierList(value, name, minimumCount: 1, rejectDuplicates: true);
    }

    public IReadOnlyList<string>? RequiredIdentifierList(string name)
    {
        if (Error is not null)
            return null;

        if (!_arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            Error = $"Argument '{name}' is required.";
            return null;
        }

        return ReadIdentifierList(value, name, minimumCount: 1, rejectDuplicates: true);
    }

    /// <summary>
    /// Reads candidate task placements. Only their shape is checked here: duplicate tasks or technicians, intervals,
    /// durations and crew sizes are domain rules that <c>PlanningCandidateValidator</c> reports as violations.
    /// </summary>
    public IReadOnlyList<PlanningTaskPlacement>? RequiredPlacements(string name)
    {
        if (Error is not null)
            return null;

        if (!_arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            Error = $"Argument '{name}' is required.";
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > MaximumListLength)
        {
            Error = $"Argument '{name}' must be an array of 1 to {MaximumListLength} task placements.";
            return null;
        }

        var placements = new List<PlanningTaskPlacement>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var path = $"{name}[{index++}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                Error = $"Argument '{path}' must be an object.";
                return null;
            }

            var unknown = item.EnumerateObject()
                .Select(property => property.Name)
                .FirstOrDefault(property => !PlacementProperties.Contains(property, StringComparer.Ordinal));
            if (unknown is not null)
            {
                Error = $"Unknown argument '{path}.{unknown}'.";
                return null;
            }

            var missing = PlacementProperties.FirstOrDefault(property =>
                !item.TryGetProperty(property, out var field) || field.ValueKind == JsonValueKind.Null);
            if (missing is not null)
            {
                Error = $"Argument '{path}.{missing}' is required.";
                return null;
            }

            var taskId = ReadIdentifier(item.GetProperty("taskId"), $"{path}.taskId");
            var start = ReadTimestamp(item.GetProperty("start"), $"{path}.start");
            var end = ReadTimestamp(item.GetProperty("end"), $"{path}.end");
            var technicianIds = ReadIdentifierList(
                item.GetProperty("technicianIds"),
                $"{path}.technicianIds",
                minimumCount: 0,
                rejectDuplicates: false);
            if (Error is not null)
                return null;

            placements.Add(new PlanningTaskPlacement(taskId!, start!.Value, end!.Value, technicianIds!));
        }

        return placements;
    }

    private static readonly string[] PlacementProperties = ["taskId", "start", "end", "technicianIds"];

    // An explicit UTC designator or offset is required, so no timestamp is interpreted in an assumed time zone
    private static readonly Regex TimestampPattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant);

    private DateTimeOffset? ReadTimestamp(JsonElement value, string name)
    {
        if (Error is not null)
            return null;

        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text ||
            !TimestampPattern.IsMatch(text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            Error = $"Argument '{name}' must be an ISO 8601 timestamp with an explicit offset, such as 2026-10-24T08:00:00+05:30.";
            return null;
        }

        return timestamp;
    }

    private IReadOnlyList<string>? ReadIdentifierList(JsonElement value, string name, int minimumCount, bool rejectDuplicates)
    {
        if (Error is not null)
            return null;

        if (value.ValueKind != JsonValueKind.Array)
        {
            Error = $"Argument '{name}' must be an array of identifiers.";
            return null;
        }

        if (value.GetArrayLength() < minimumCount || value.GetArrayLength() > MaximumListLength)
        {
            Error = $"Argument '{name}' must contain between {minimumCount} and {MaximumListLength} identifiers.";
            return null;
        }

        var identifiers = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var identifier = ReadIdentifier(item, name);
            if (identifier is null)
                return null;

            if (rejectDuplicates && identifiers.Contains(identifier, StringComparer.OrdinalIgnoreCase))
            {
                Error = $"Argument '{name}' contains '{identifier}' more than once.";
                return null;
            }

            identifiers.Add(identifier);
        }

        return identifiers;
    }

    private string? ReadIdentifier(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            Error = $"Argument '{name}' must contain string identifiers.";
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumIdentifierLength || text != text.Trim())
        {
            Error = $"Argument '{name}' must contain nonblank identifiers of at most {MaximumIdentifierLength} characters without surrounding spaces.";
            return null;
        }

        return text;
    }
}
