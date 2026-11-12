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
    // v0.9.16: run-based ORG recognizer fields (spec locked with Norman, 2026-11-06 hammering;
    // supersedes OrgEntityRx — rules R1-R9 live on ScanOrgRuns)
    private static readonly HashSet<string> OrgSuffixes = new(StringComparer.Ordinal)
    { "PLLC", "LLP", "LLC", "LLLP", "LP", "Inc", "Corp", "Corporation", "PC", "PA", "Ltd", "Company", "Co" };

    // R2: lowercase crossings — of/de join freely; "and" joins only when both flanking
    // sides are single words; every other lowercase word is a wall
    private static readonly HashSet<string> OrgJoiners = new(StringComparer.OrdinalIgnoreCase)
    { "of", "de" };

    // R4: terminate the run regardless of case (the verbs/determiners that precede names)
    private static readonly HashSet<string> OrgStopWords = new(StringComparer.OrdinalIgnoreCase)
    { "Has", "Have", "Had", "Was", "Were", "Is", "Are", "Said", "Says", "Sued", "Filed",
      "Received", "Notified", "Owes", "Per", "Against", "Rules", "Sent" };

    // v0.9.16: salutation-opened PERSON spans are junk ("Dear Smithfield" → "Hi Dear Smithfield,")
    private static readonly HashSet<string> Salutations = new(StringComparer.OrdinalIgnoreCase)
    { "Dear", "Hi", "Hello", "Hey", "Greetings", "Regards" };

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

    // ── v0.9.15: per-type noise gates for the probabilistic tiers ──────────────
    // Predator field report 2026-11-06 (8005 lane vault census): the presidio NLP
    // tier mints junk at scale — UI nouns as PERSON ("Tab", "Setup", "Workflow",
    // "Dummy", "Knowledge Base"), state lists as one PERSON/ORG ("Alabama, Arizona"
    // / "AL, CO, KY, MI, MO, OH, PA"), non-places as LOCATION ("Notice", "Q3",
    // "Budgeting", "Mobile App", "address1", "the Reporting section"), date and
    // amount fragments as PHONE_NUMBER ("2021113001", "06.124943", "1648"), a US
    // phone as UK_NHS ("267-941-1099" — NHS numbers carry a modulus-11 check digit,
    // so fakes never validate), MS license SKUs as US_DRIVER_LICENSE ("E1", "E3",
    // "O365", "u2013" escape artifacts), and lowercase blobs as MEDICAL_LICENSE
    // ("eb8029298"). Gates are strict per type; recall loss is confined to the
    // junk-prone classes — credential regex tiers are untouched.

    /// <summary>Places allowlist: US states (names + codes), territories, common
    /// countries, major US cities. Extend at runtime with SANITIZE_PLACES_EXTRA
    /// (comma-separated; "dawson county,winter park"). Lookup is whole-span after
    /// NormalizePlace, with a comma-part fallback for "Atlanta, GA" spans.</summary>
    private static readonly HashSet<string> PlaceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alabama","Alaska","Arizona","Arkansas","California","Colorado","Connecticut","Delaware","Florida","Georgia",
        "Hawaii","Idaho","Illinois","Indiana","Iowa","Kansas","Kentucky","Louisiana","Maine","Maryland",
        "Massachusetts","Michigan","Minnesota","Mississippi","Missouri","Montana","Nebraska","Nevada",
        "New Hampshire","New Jersey","New Mexico","New York","New York State","North Carolina","North Dakota",
        "Ohio","Oklahoma","Oregon","Pennsylvania","Rhode Island","South Carolina","South Dakota","Tennessee",
        "Texas","Utah","Vermont","Virginia","Washington","West Virginia","Wisconsin","Wyoming",
        "District of Columbia","Washington DC",
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD","MA",
        "MI","MN","MS","MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN",
        "TX","UT","VT","VA","WA","WV","WI","WY","DC",
        "Guam","Puerto Rico","USVI","US Virgin Islands","Virgin Islands","American Samoa","Samoa",
        "Northern Mariana Islands",
        "United States","United States of America","USA","US","America","Canada","Mexico","Brazil",
        "United Kingdom","UK","England","Scotland","Wales","Ireland","France","Germany","Italy","Spain",
        "Portugal","Netherlands","Belgium","Switzerland","Austria","Sweden","Norway","Denmark","Finland",
        "Poland","India","China","Japan","South Korea","Korea","Taiwan","Hong Kong","Philippines","Vietnam",
        "Thailand","Australia","New Zealand",
        "New York City","Los Angeles","Chicago","Houston","Philadelphia","Phoenix","San Antonio","San Diego",
        "Dallas","San Jose","Austin","Jacksonville","Fort Worth","Columbus","Charlotte","Indianapolis","Seattle",
        "Denver","Boston","Detroit","Nashville","Memphis","Portland","Oklahoma City","Las Vegas","Louisville",
        "Baltimore","Milwaukee","Albuquerque","Tucson","Fresno","Sacramento","Kansas City","Mesa","Atlanta",
        "Omaha","Raleigh","Miami","Oakland","Minneapolis","Tulsa","Wichita","New Orleans","Arlington",
        "Cleveland","Tampa","Honolulu","Irvine","San Bernardino","Orlando","St Louis","Pittsburgh",
        "Corpus Christi","Lincoln","Buffalo","Newark","Plano","Cincinnati","Anchorage","Chino","Winter Park",
        "Winter Haven","Madison","Chesapeake","Glendale","Scottsdale","Baton Rouge","Shreveport","Des Moines",
        "Boise","Little Rock","Springfield","Toledo","Akron","Dayton","El Paso","Fort Lauderdale",
        "Salt Lake City","Spokane","Richmond","Tacoma","Savannah","Charleston","Greenville","Knoxville",
        "Chattanooga","Birmingham","Montgomery","Jackson","Provo","Eugene","Salem","Fargo","Billings",
        "Cheyenne","Manchester","Providence","Hartford","Albany","Rochester","Syracuse","Jersey City",
        "Fort Wayne","Lubbock","Garland","Amarillo","McKinney","Frisco","Reno"
    };

    /// <summary>Single-word state names + 2-letter codes, for the "a span holding ≥2
    /// of these is a list, not a name/org" rule ("Alabama, Arizona" / "AL, CO, KY…").
    /// Multi-word names ("New Hampshire") are deliberately absent — their fragments
    /// must not count, or "New York" would read as two hits.</summary>
    private static readonly HashSet<string> StateTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD","MA",
        "MI","MN","MS","MO","MT","NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN",
        "TX","UT","VT","VA","WA","WV","WI","WY","DC",
        "Alabama","Alaska","Arizona","Arkansas","California","Colorado","Connecticut","Delaware","Florida",
        "Georgia","Hawaii","Idaho","Illinois","Indiana","Iowa","Kansas","Kentucky","Louisiana","Maine",
        "Maryland","Massachusetts","Michigan","Minnesota","Mississippi","Missouri","Montana","Nebraska",
        "Nevada","Ohio","Oklahoma","Oregon","Pennsylvania","Tennessee","Texas","Utah","Vermont","Virginia",
        "Washington","Wisconsin","Wyoming"
    };

    /// <summary>System/UI vocabulary that is never a person — word-boundary matched
    /// against PERSON/NRP spans (plural forms handled by a trailing-s probe).</summary>
    private static readonly HashSet<string> EntityNoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "tab","setup","workflow","token","model","dummy","info","notice","unpaid","account","ledger",
        "garnishment","hire","hires","calculation","match","ingest","assemble","perspective","checkbox",
        "complete","description","manager","merged","convert","user","login","base","knowledge","job","task",
        "ticket","outlook","acrobat","zammad","email","app","mobile","budgeting","section","reporting",
        "service","dept","misc","auto","awsservice","novatime","suser","student","loan","isp","seq","ux"
    };

    /// <summary>Lower→Upper transitions after these prefixes are name particles
    /// (McDonald, LaGuardia, DeLaCruz, O'Brien), not CamelCase glue.</summary>
    private static readonly string[] CamelExemptions =
        { "Mc", "Mac", "De", "Du", "Di", "Da", "La", "Le", "St", "Van", "Von", "O'" };

    private static HashSet<string>? _extraPlaces;

    private static HashSet<string> ExtraPlaces()
    {
        if (_extraPlaces is not null) return _extraPlaces;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = Environment.GetEnvironmentVariable("SANITIZE_PLACES_EXTRA");
        if (!string.IsNullOrWhiteSpace(raw))
            foreach (var p in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(NormalizePlace(p));
        _extraPlaces = set;
        return set;
    }

    private static string NormalizePlace(string value)
    {
        var v = value.Replace('\u2019', '\'').Trim();
        if (v.Length >= 4 && v.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) v = v[4..].Trim();
        while (v.Length > 2 && v.EndsWith("'s", StringComparison.OrdinalIgnoreCase)) v = v[..^2].TrimEnd();
        v = v.TrimEnd('\'').Replace(".", "");
        while (v.Contains("  ")) v = v.Replace("  ", " ");
        return v.Trim();
    }

    private static bool IsKnownPlace(string normalized)
    {
        if (PlaceNames.Contains(normalized) || ExtraPlaces().Contains(normalized)) return true;
        foreach (var part in normalized.Split(','))                      // "Atlanta, GA"
            if (PlaceNames.Contains(part.Trim()) || ExtraPlaces().Contains(part.Trim())) return true;
        return false;
    }

    /// <summary>Strict LOCATION allowlist — supersedes the v0.9.14 digits/typo probes
    /// (any non-place is now rejected, typos included). Env-extensible via
    /// SANITIZE_PLACES_EXTRA for client cities the list misses.</summary>
    public static bool IsNoiseLocationStrict(string value)
    {
        var p = NormalizePlace(value);
        if (p.Length == 0) return true;
        if (p.Any(char.IsDigit)) return true;                 // "Electrical District No 2", "address1"
        return !IsKnownPlace(p);
    }

    private static bool HasCamelGlue(string value)
    {
        for (var i = 1; i < value.Length; i++)
        {
            if (!char.IsUpper(value[i]) || !char.IsLower(value[i - 1])) continue;
            var take = Math.Min(4, i);
            var prefix = value.Substring(i - take, take);
            if (CamelExemptions.Any(x => prefix.EndsWith(x, StringComparison.Ordinal))) continue;
            return true;                                      // "theConvert", "HiresP.O. Box"
        }
        return false;
    }

    private static bool IsNoisePerson(string value)
    {
        if (value.Any(char.IsDigit)) return true;             // "PM-1707", "MN5208", "ticket/989824"
        if (!value.Any(char.IsUpper)) return true;            // "jhightower", "akira", "mark rpo"
        if (HasCamelGlue(value)) return true;                 // "theConvert", "HiresP.O. Box"
        if (value.Contains("'ll") || value.Contains("n't") || value.Contains("'ve") || value.Contains("'d "))
            return true;                                      // "I'll"
        if (value.Contains('\u2022')) return true;            // "Tab • Description"
        var tokens = System.Text.RegularExpressions.Regex.Matches(value, "[A-Za-z]+")
            .Select(m => m.Value).ToList();
        // v0.9.16: salutation-opened spans ("Dear Smithfield") and the legal-Latin pair
        // ("Et Al" — caption glue) are junk, not people
        if (tokens.Count > 0 && Salutations.Contains(tokens[0])) return true;
        for (var i = 0; i + 1 < tokens.Count; i++)
            if (tokens[i].Equals("et", StringComparison.OrdinalIgnoreCase) &&
                tokens[i + 1].Equals("al", StringComparison.OrdinalIgnoreCase))
                return true;
        foreach (var t in tokens)
        {
            if (EntityNoiseWords.Contains(t)) return true;    // "Tab", "Outlook", "Dummy"
            if (t.EndsWith("s") && EntityNoiseWords.Contains(t[..^1])) return true;  // "Garnishments"
        }
        if (tokens.Count == 1 && !Gazetteer.HasName(tokens[0])) return true;         // "Forperspective"
        if (tokens.Count(t => StateTokens.Contains(t)) >= 2) return true;            // "Alabama, Arizona"
        return false;
    }

    private static bool IsNoiseNrp(string value)
    {
        if (value.Any(char.IsDigit)) return true;             // "New F4/17/25", "CLI-000109", "CDept4"
        if (value.Length <= 4 && value.All(char.IsUpper)) return true;               // "ISP"
        if (HasCamelGlue(value)) return true;                 // "SUser"
        foreach (var t in System.Text.RegularExpressions.Regex.Matches(value, "[A-Za-z]+").Select(m => m.Value))
        {
            if (EntityNoiseWords.Contains(t)) return true;    // "Info", "Acrobat", "AWSService", "NOVAtime"
            if (t.EndsWith("s") && EntityNoiseWords.Contains(t[..^1])) return true;
        }
        return false;                                         // "American", "Marina Romanchik" — class-true, keep
    }

    /// <summary>NANP structural check: 10 digits (or 11 with a US+1), area and exchange
    /// codes in [2-9]xx, no N11 codes. Kills date/amount/short-number fragments
    /// ("2021113001", "6520771731", "1648", "06.124943") minted as phones. Note:
    /// international numbers without a +1 form will not validate — by design for
    /// US payroll lanes.</summary>
    public static bool IsValidNanp(string value)
    {
        var d = new string(value.Where(char.IsDigit).ToArray());
        if (d.Length == 11 && d[0] == '1') d = d[1..];
        if (d.Length != 10) return false;
        if (d[0] is < '2' or > '9') return false;             // area code
        if (d[3] is < '2' or > '9') return false;             // exchange
        if (d[1] == '1' && d[2] == '1') return false;         // N11 area (211/411/911…)
        if (d[4] == '1' && d[5] == '1') return false;         // N11 exchange
        return true;
    }

    /// <summary>UK NHS numbers carry a modulus-11 check digit — fakes (e.g. the US
    /// phone "267-941-1099") never validate.</summary>
    public static bool IsValidNhsNumber(string value)
    {
        var d = new string(value.Where(char.IsDigit).ToArray());
        if (d.Length != 10) return false;
        var sum = 0;
        for (var i = 0; i < 9; i++) sum += (d[i] - '0') * (10 - i);
        var r = sum % 11;
        if (r == 1) return false;                             // check digit would be 10 — invalid
        return (11 - r) % 11 == (d[9] - '0');
    }

    private static bool IsNoiseUsDl(string value)
    {
        var v = value.Trim();
        if (v.Length < 5) return true;                        // "E1", "E3", "V1", "O365"
        if (!v.Any(char.IsDigit)) return true;                // no US state issues letter-only DLs
        if (v.Length == 5 && (v[0] == 'u' || v[0] == 'U') && v[1..].All(char.IsDigit))
            return true;                                      // \u2013/-\u2014 escape artifacts
        if (v.StartsWith("000")) return true;                 // zero-padded fragments ("000091")
        return false;
    }

    private static bool IsNoiseMedLicense(string value)
    {
        // real US formats start with a digit or uppercase letter; "eb8029298" is a blob
        return value.Length > 0 && char.IsLower(value[0]);
    }

    private static bool IsStateCodeList(string value)
    {
        var codes = System.Text.RegularExpressions.Regex.Matches(value, "[A-Za-z]+")
            .Count(m => m.Value.Length == 2 && StateTokens.Contains(m.Value));
        return codes >= 2;                                    // "AL, CO, KY, MI, MO, OH, PA"
    }

    /// <summary>v0.9.15: per-type noise gate for the probabilistic tiers (Presidio +
    /// NER). Returns true when the span is junk for its entity type. Unknown types
    /// pass untouched — new presidio recognizers degrade to today's behavior.</summary>
    public static bool IsNoiseEntity(string type, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        switch (type.ToUpperInvariant())
        {
            case "LOCATION":
            case "LOC":
                return IsNoiseLocationStrict(value);
            case "PERSON":
            case "PER":
                return IsNoisePerson(value);
            case "NRP":
                return IsNoiseNrp(value);
            case "PHONE_NUMBER":
                return !IsValidNanp(value);
            case "UK_NHS":
                return !IsValidNhsNumber(value);
            case "US_DRIVER_LICENSE":
            case "DRIVER_LICENSE":
                return IsNoiseUsDl(value);
            case "MEDICAL_LICENSE":
                return IsNoiseMedLicense(value);
            case "ORG_ENTITY":
            case "ORGANIZATION":
            case "ORG":
                return IsStateCodeList(value);
            default:
                return false;
        }
    }

    // ── v0.9.16: run-based ORG recognizer (spec locked with Norman, 2026-11-06 hammering) ──
    // R1 run words: Capitalized, ALL-CAPS, or digit-led alphanumerics ("3M", "7-Eleven");
    //    any other lowercase word is a wall.
    // R2 lowercase crossings: of/de join freely; "and"/"And" joins only when both flanking
    //    sides are single words (Johnson and Johnson ✓; "…Revenue And Lopez Logistics" ✗).
    // R3 "&" crosses freely (a symbol, not a word).
    // R4 stop-list words terminate regardless of case; a LEADING capitalised "The" is
    //    exempt (The Home Depot Inc).
    // R5 a suffix-terminated side ends the name — the connector after it starts a new
    //    candidate (Acme LLC and Beta Corp = two mints, never one).
    // R6 a candidate must end in a legal suffix.
    // R7 per-side ≤3 words (an over-long leading side trims to the last 3); ≤2 connectors.
    // R8 chained suffixes are name words, not terminators ("Shipbuilding Co Ltd").
    // R9 narrow legacy guard: single ALL-CAPS name word + "PA" suffix skips (city, state).
    // v0.9.21: short pure-digit tokens (≤4 digits) are run words ('D') — they join
    // runs mid-name but never start one ("ABC 9 LLC" ✓; "5 LLC" still skips).

    private sealed record OrgTok(string Raw, string Core, int Start, int End, char Kind);

    private static char ClassifyOrgToken(string raw, string core)
    {
        if (core.Length == 0) return 'B';
        if (raw.EndsWith(":")) return 'B';                    // label ("Employer:")
        if (core == "&") return 'A';                          // R3
        if (OrgStopWords.Contains(core)) return 'T';          // R4
        if (core.Equals("and", StringComparison.OrdinalIgnoreCase)) return 'N';  // R2
        if (OrgJoiners.Contains(core)) return 'J';            // R2
        if (char.IsLower(core[0])) return 'B';                // R1
        if (!core.Any(char.IsLetter))
        {
            // v0.9.21: short pure-digit tokens are run words mid-name ("ABC 9 LLC",
            // "Level 3 Communications") — 'D' joins runs but never starts one, so bare
            // amounts stay out ("5 LLC" still skips; the run must open with a word).
            return core.Length <= 4 && core.All(char.IsDigit) ? 'D' : 'B';
        }
        if (OrgSuffixes.Contains(core)) return 'S';
        return 'W';
    }

    private static List<(int Start, int End)> ScanOrgRuns(string text)
    {
        var cands = new List<(int Start, int End)>();
        var lineStart = 0;
        while (lineStart < text.Length)
        {
            var nl = text.IndexOf('\n', lineStart);
            var lineEnd = nl < 0 ? text.Length : nl;
            ScanOrgLine(text, lineStart, lineEnd, cands);
            if (nl < 0) break;
            lineStart = nl + 1;
        }
        return cands;
    }

    private static void ScanOrgLine(string text, int lo, int hi, List<(int Start, int End)> cands)
    {
        var toks = new List<OrgTok>();
        foreach (Match m in Regex.Matches(text[lo..hi], @"\S+"))
            toks.Add(new OrgTok(m.Value, m.Value.TrimEnd(',', '.', ';', ':'), lo + m.Index, lo + m.Index + m.Length, 'X'));
        for (var i = 0; i < toks.Count; i++)
            toks[i] = toks[i] with { Kind = ClassifyOrgToken(toks[i].Raw, toks[i].Core) };
        for (var i = toks.Count - 2; i >= 0; i--)              // R8: chained suffix → name word
            if (toks[i].Kind == 'S' && toks[i + 1].Kind == 'S')
                toks[i] = toks[i] with { Kind = 'W' };
        var s = 0;
        while (s < toks.Count)
        {
            var k0 = toks[s].Kind;
            var startOk = k0 is 'W'
                || (k0 == 'T' && toks[s].Core.Equals("The", StringComparison.OrdinalIgnoreCase) && char.IsUpper(toks[s].Raw[0]));
            if (!startOk) { s++; continue; }
            var j = s + 1;
            while (j < toks.Count)
            {
                var k = toks[j].Kind;
                if (k is 'W' or 'D' or 'A' or 'J' or 'N') { j++; continue; }
                if (k == 'S') { j++; break; }
                break;
            }
            if (j > s + 1 && toks[j - 1].Kind == 'S')
            {
                var span = BuildOrgSpan(text, toks, s, j);
                if (span.Start < span.End) cands.Add(span);
            }
            s = Math.Max(j, s + 1);
        }
    }

    private static (int Start, int End) BuildOrgSpan(string text, List<OrgTok> toks, int start, int end)
    {
        // R2/R5: split at a conditional "and" whose flanking sides are not both single words
        var bounds = new List<int> { start };
        for (var t = start; t < end; t++)
        {
            if (toks[t].Kind != 'N') continue;
            var leftWords = OrgWordCount(toks, bounds[^1], t);
            var rightWords = OrgWordCount(toks, t + 1, end);
            if (leftWords == 1 && rightWords == 1) continue;
            bounds.Add(t + 1);
        }
        for (var b = 0; b < bounds.Count; b++)
        {
            var ps = bounds[b];
            var pe = b + 1 < bounds.Count ? bounds[b + 1] - 1 : end;   // exclude the joining "and"
            if (pe - 1 <= ps || toks[pe - 1].Kind != 'S') continue;    // R6: must end in a suffix
            // R7: sides by connector; ≤3 words per side (leading side trims), ≤2 connectors
            var sideStarts = new List<int> { ps };
            for (var t = ps; t < pe - 1; t++)
                if (toks[t].Kind is 'A' or 'J') sideStarts.Add(t + 1);
            if (sideStarts.Count > 3) sideStarts = sideStarts[^3..];
            while (sideStarts.Count > 1 && OrgWordCount(toks, sideStarts[0], sideStarts[1] - 1) > 3)
                sideStarts.RemoveAt(0);
            if (OrgWordCount(toks, sideStarts[0], pe) > 4)
            {
                var seen = 0;
                var cut = sideStarts[0];
                for (var t = pe - 1; t >= sideStarts[0]; t--)
                    if (toks[t].Kind == 'W' && ++seen == 3) { cut = t; break; }
                sideStarts[0] = cut;
            }
            // R9: single ALL-CAPS name word + "PA" suffix = "PHILADELPHIA, PA" (city, state)
            var wordToks = toks[sideStarts[0]..pe].Where(t => t.Kind == 'W').ToList();
            if (wordToks.Count == 1 && wordToks[0].Core.All(char.IsUpper) && toks[pe - 1].Core == "PA") continue;
            var e = toks[pe - 1].End;
            while (e > toks[sideStarts[0]].Start + 1 && (text[e - 1] == ',' || text[e - 1] == ';')) e--;
            return (toks[sideStarts[0]].Start, e);
        }
        return (0, 0);
    }

    private static int OrgWordCount(List<OrgTok> toks, int from, int to)
        => toks[from..to].Count(t => t.Kind == 'W');

    public static (string Text, List<Finding> Findings) Scrub(string text, VaultData vault)
    {
        var findings = new List<Finding>();

        // ---- ORG_ENTITY pass: run-based firm-name scan (v0.9.16, spec locked with Norman) ----
        foreach (var (os, oe) in ScanOrgRuns(text).OrderByDescending(c => c.Start))
        {
            var value = text[os..oe];
            // v0.9.15: a comma-list of state codes ("AL, CO, KY, MI, MO, OH, PA") is an
            // enumeration, not a firm.
            if (IsStateCodeList(value)) continue;
            if (LooksLikeCode(value)) continue;
            var ph = Sanitizer.Placeholder(vault, "ORG_ENTITY", value);
            var line = 1 + text[..os].Count(c => c == '\n');
            findings.Add(new Finding(line, "ORG_ENTITY", ph));
            text = text[..os] + "[" + ph + "]" + text[oe..];
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
            // v0.9.15: state names/codes sit in the Census surname set — "Alabama,
            // Arizona" is a list of states, not a person.
            if (StateTokens.Contains(surname) || StateTokens.Contains(firstName)) continue;
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
