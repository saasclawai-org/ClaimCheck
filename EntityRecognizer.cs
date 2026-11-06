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

    // v0.9.13: span-quality guard for the probabilistic tiers (NER + Presidio).
    // bert-base-NER over code-heavy documents over-tags capitalized identifiers
    // (2026-11-05 battery: m.Length / DbSet / StatusCode ticketed as PERSON in a
    // line-numbered C# listing; BIO spans crossed newlines and swallowed the
    // numbered-list labels — values like "data;\n25|"; escaped-JSON content put
    // literal \n runs inside single lines, defeating whitespace-based guards).
    // Real names never contain code punctuation, backslash escapes, CamelCase
    // compound words as a single token, or calendar tokens ("Wednesday, November").
    // Edge cases accepted: McDonald / LaGuardia (PascalCase rule), "May" as a name
    // (calendar rule). O'Brien kept — apostrophe is not in CodeChars.
    private static readonly Regex LowerDotIdentifierRx = new(@"[a-z]\.[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex CalendarTokenRx = new(
        @"\b(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|January|February|March|April|May|June|July|August|September|October|November|December)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly char[] CodeChars = "(){}[]<>=;|\"`\\#\n\r".ToCharArray();

    // v0.9.13: tech-brand tokens that live in the gazetteer but are never first names —
    // "Stripe, Linear" (claimcheck's own test prose) reverse-name-minted as a person.
    private static readonly HashSet<string> NonNameTokens = new(StringComparer.OrdinalIgnoreCase)
    { "Stripe", "Linear", "Notion", "Slack", "GitHub", "GitLab", "Jira", "Figma",
      "Trello", "Asana", "Zoom", "Vercel", "Netlify", "Docker", "Shopify",
      "PayPal", "Twilio", "Datadog" };

    /// <summary>True when a probabilistic-tier span looks like code/structure rather
    /// than a name — NER and Presidio findings are dropped when this fires.</summary>
    public static bool LooksLikeCode(string value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        if (value.IndexOfAny(CodeChars) >= 0) return true;
        if (LowerDotIdentifierRx.IsMatch(value)) return true;
        if (CalendarTokenRx.IsMatch(value)) return true;
        // single-token CamelCase compound = code identifier (DbSet, StatusCode, TokenRe)
        if (!value.Contains(' ') && Regex.IsMatch(value, @"^[A-Z][a-z]+[A-Z]")) return true;
        return false;
    }

    // v0.9.14: LOCATION noise guards for the probabilistic tiers (Presidio LOCATION,
    // NER LOC). Predator field report 2026-11-06: the vault collected "288" (digits-only
    // span minted as a place) and "atltanta" (Presidio NLP typo-tolerance tagging a
    // misspelled city — the gazetteer has "Atlanta", the scrub text had the typo).
    // Digits-only spans are never places; a candidate that is NOT a gazetteer name
    // (the set is case-insensitive) but is one edit away from one is a misspelling —
    // skip it. The canonical spelling still tickets when it appears properly.
    // Scoping: these fire on LOCATION spans only — PERSON/ORG keep full recall, since
    // a rare real name can sit one edit from a common gazetteer word ("Gonzales"/
    // "Gonzalez") and dropping it would be a false-negative leak, not mere noise.
    public static bool IsNoiseLocation(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var hasLetter = false;
        foreach (var ch in value)
            if (char.IsLetter(ch)) { hasLetter = true; break; }
        if (!hasLetter) return true;                 // "288" — digits/punctuation only
        return IsNearGazetteerTypo(value);           // "atltanta" — one edit from "atlanta"
    }

    /// <summary>True when the value is absent from the gazetteer but within one edit
    /// (deletion / substitution / insertion) of a gazetteer name. Probe cost is a few
    /// hundred case-insensitive HashSet lookups per candidate — no fuzzy index needed.</summary>
    public static bool IsNearGazetteerTypo(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var len = value.Length;
        if (len < 5 || len > 40) return false;       // probe band; short strings collide too easily
        if (Gazetteer.HasName(value)) return false;  // a real name (any casing) is never a typo
        for (var i = 0; i < len; i++)                // deletion: "atltanta" -> "atlanta"
            if (Gazetteer.HasName(value.Remove(i, 1))) return true;
        for (var i = 0; i < len; i++)                // substitution: same length, one char off
            for (var c = 'a'; c <= 'z'; c++)
            {
                if (char.ToLowerInvariant(value[i]) == c) continue;
                if (Gazetteer.HasName(value[..i] + c + value[(i + 1)..])) return true;
            }
        for (var i = 0; i <= len; i++)               // insertion: candidate missing one char
            for (var c = 'a'; c <= 'z'; c++)
                if (Gazetteer.HasName(value[..i] + c + value[i..])) return true;
        return false;
    }

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
            // v0.9.13: "PHILADELPHIA, PA" — a state abbreviation is also an entity
            // suffix (PA = Professional Association); an all-caps single-word "firm
            // name" is a city or acronym, not a firm. Skip those.
            var namePart = m.Groups[1].Value.Trim();
            if (!namePart.Contains(' ') && namePart.All(char.IsUpper)) continue;
            if (LooksLikeCode(value)) continue;
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
            // v0.9.13: dateline guard — "Wednesday, November" (both tokens in the
            // gazetteer, yet nobody's name) and acronym first names ("Lopez, FEIN" —
            // FEIN is a label, not a first name).
            if (CalendarTokenRx.IsMatch(fullName)) continue;
            if (NonNameTokens.Contains(surname) || NonNameTokens.Contains(firstName)) continue;
            if (firstName.All(char.IsUpper)) continue;
            if (LooksLikeCode(fullName)) continue;
            var ph = Sanitizer.Placeholder(vault, "PERSON", fullName);
            var line = 1 + text[..m.Index].Count(c => c == '\n');
            findings.Add(new Finding(line, "PERSON", ph));
            text = text[..m.Index] + "[" + ph + "]" + text[(m.Index + m.Length)..];
        }

        return (text, findings);
    }
}
