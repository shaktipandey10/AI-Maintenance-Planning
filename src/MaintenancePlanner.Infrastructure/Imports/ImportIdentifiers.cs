using System.Text.RegularExpressions;

namespace MaintenancePlanner.Infrastructure.Imports;

/// <summary>The identifier rule for imported records, manuals, skills and references (e.g. P-101, BR-101, TECH-001).</summary>
public static class ImportIdentifiers
{
    public const string Rule =
        "Identifiers must be 1-64 characters: a letter or digit followed by letters, digits, '.', '_' or '-'.";

    private static readonly Regex Pattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    public static bool IsValid(string value) => Pattern.IsMatch(value);
}
