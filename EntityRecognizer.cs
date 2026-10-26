using System.Text.RegularExpressions;

namespace ClaimCheck;

/// <summary>
/// Entity recognizers for patterns the regex tier and gazetteer don't cover:
///   1. ORG_ENTITY: business firm names (Dewey, Cheathem &amp; Howe, PLLC)
///   2. REVERSE_NAME: "Last, First MI" format (payroll/tax standard)
/// Both are deterministic — no ML model, no network.
/// </summary>
public static class EntityRecognizer
{
    /// <summary>Business entity suffixes — the anchor that makes firm names recognizable.</summary>
    private static readonly HashSet<string> EntitySuffixes = new(StringComparer.OrdinalIgnoreCase)
    { "PLLC", "LLP", "LLC", "LLLP", "LP", "PC", "PA", "Inc", "Corp", "Corporation", "Ltd", "Company", "Co" };

    /// <summary>Matches: capitalized words (with commas/ampersands) ending in an entity suffix.
    /// e.g. "Dewey, Cheathem &amp; Howe, PLLC" or "Bright Path Accounting, LLC"</summary>
    private static readonly Regex OrgEntityRx = new(
        @"(?<![a-z])([A-Z][a-zA-Z]*(?:\s*(?:,\s*|&|and\s+|of\s+|the\s+|de\s+)\s*[A-Z][a-zA-Z]*)*)\s*,?\s*\b(PLLC|LLP|LLC|LLLP|LP|Inc\.?|Corp(?:oration)?|PC|PA|Ltd)\b",
        RegexOptions.Compiled);

    /// <summary>Matches: "Last, First MI" where Last and First are in the gazetteer.
    /// Payroll/tax standard format for client identification.</summary>
    private static readonly Regex ReverseNameRx = new(
        @"(?<![a-zA-Z])([A-Z][a-zA-Z]+),\s*([A-Z][a-zA-Z]+)(?:\s+([A-Z]))?(?=\b|[,.;])",
        RegexOptions.Compiled);

    public static (string Text, List<Finding> Findings) Scrub(string text, VaultData vault)
    {
        var findings = new List<Finding>();

        // ---- ORG_ENTITY pass: firm names ----
        var orgMatches = OrgEntityRx.Matches(text).Cast<Match>()
            .Where(m => m.Success && m.Index >= 0)
            .OrderByDescending(m => m.Index)
            .ToList();
        foreach (var m in orgMatches)
        {
            var value = m.Value.Trim();
            var ph = Sanitizer.Placeholder(vault, "ORG_ENTITY", value);
            var line = 1 + text[..m.Index].Count(c => c == '\n');
            findings.Add(new Finding(line, "ORG_ENTITY", ph));
            text = text[..m.Index] + "[" + ph + "]" + text[(m.Index + m.Length)..];
        }

        // ---- REVERSE_NAME pass: "Last, First MI" validated against the gazetteer ----
        Gazetteer.EnsureLoaded();
        var revMatches = ReverseNameRx.Matches(text).Cast<Match>()
            .Where(m => m.Success)
            .OrderByDescending(m => m.Index)
            .ToList();
        foreach (var m in revMatches)
        {
            var surname = m.Groups[1].Value;
            var firstName = m.Groups[2].Value;
            var mi = m.Groups[3].Success ? " " + m.Groups[3].Value : "";
            var fullName = surname + ", " + firstName + mi;
            if (!Gazetteer.HasName(surname) || !Gazetteer.HasName(firstName)) continue;
            if (surname.Length < 3 || firstName.Length < 2) continue;
            var ph = Sanitizer.Placeholder(vault, "PERSON", fullName);
            var line = 1 + text[..m.Index].Count(c => c == '\n');
            findings.Add(new Finding(line, "PERSON", ph));
            text = text[..m.Index] + "[" + ph + "]" + text[(m.Index + m.Length)..];
        }

        return (text, findings);
    }
}
