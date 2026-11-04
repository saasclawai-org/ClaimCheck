using System.Text.Json;

namespace ClaimCheck;

/// <summary>
/// Client-master gazetteer: the user's actual client roster as deterministic
/// detection lists — company names + known FEINs. No ML, no confidence threshold;
/// the strongest signal for known clients. Curated, so FP tolerance is higher
/// than the auto names-gazetteer's — but guardrails still apply.
///
/// Files (both hot-reload on mtime change — a DB refresh needs no restart):
///   ~/.sanitize-mcp/custom_clients.json  JSON array of client name strings
///   ~/.sanitize-mcp/custom_feins.json    JSON array of FEIN strings (any format;
///                                        normalized to digits on load)
///
/// v0.9.12: tokenized n-gram matching replaces per-client IndexOf — O(document ×
/// maxN) instead of O(clients × chars), flat latency at 10K+ entries. FEIN
/// matching added: digits-normalized, so 12-3456789 / 123456789 / 12 3456 789 all
/// hit the same vault entry. Guardrails (2026-10-27 corruption-FP lesson):
/// single-word names shorter than 4 characters are never matched; FEINs only ever
/// ticket digit spans whose normalized value is an exact set member.
/// </summary>
public static class CustomClients
{
    private const int MaxNameWords = 6;       // longer names are never matched (rare)
    private const int MinSingleWordChars = 4; // FP guardrail: no bare tiny-word tickets

    private static HashSet<string>? _names;                       // full roster (for Count)
    private static Dictionary<int, HashSet<string>>? _nameNgrams; // token-count -> normalized n-grams
    private static HashSet<string>? _feins;                       // digits-only
    private static readonly object Lock = new();
    private static DateTime _namesMtimeUtc = DateTime.MinValue;
    private static DateTime _feinsMtimeUtc = DateTime.MinValue;

    public static string DataPath => Path.Combine(Sanitizer.HomeDir, ".sanitize-mcp", "custom_clients.json");
    public static string FeinsPath => Path.Combine(Sanitizer.HomeDir, ".sanitize-mcp", "custom_feins.json");

    public static int Count => _names?.Count ?? 0;
    public static int FeinCount => _feins?.Count ?? 0;
    public static bool Loaded => _names is not null;

    private static DateTime MTimeUtc(string path) =>
        File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    /// <summary>Load (or hot-reload) both lists. Cheap per request: two stat() calls when fresh.</summary>
    public static void Load()
    {
        lock (Lock)
        {
            var namesMtime = MTimeUtc(DataPath);
            if (_names is null || namesMtime != _namesMtimeUtc)
            {
                List<string> names;
                try { names = File.Exists(DataPath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(DataPath)) ?? [] : []; }
                catch { names = []; }
                (_names, _nameNgrams) = BuildNameIndex(names);
                _namesMtimeUtc = namesMtime;
            }
            var feinsMtime = MTimeUtc(FeinsPath);
            if (_feins is null || feinsMtime != _feinsMtimeUtc)
            {
                List<string> raw;
                try { raw = File.Exists(FeinsPath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FeinsPath)) ?? [] : []; }
                catch { raw = []; }
                _feins = new HashSet<string>(
                    raw.Select(DigitsOnly).Where(d => d.Length is >= 6 and <= 11),
                    StringComparer.Ordinal);
                _feinsMtimeUtc = feinsMtime;
            }
        }
    }

    // ---- names -------------------------------------------------------------

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == '\'' || ch == '-') sb.Append(char.ToLowerInvariant(ch));
            else sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static (HashSet<string> Names, Dictionary<int, HashSet<string>> Ngrams) BuildNameIndex(List<string> names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var idx = new Dictionary<int, HashSet<string>>();
        foreach (var name in names)
        {
            var words = Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words.Length > MaxNameWords) continue;
            if (words.Length == 1 && words[0].Length < MinSingleWordChars) continue; // FP guardrail
            if (!idx.TryGetValue(words.Length, out var bucket))
                idx[words.Length] = bucket = new HashSet<string>(StringComparer.Ordinal);
            bucket.Add(string.Join(' ', words));
        }
        return (set, idx);
    }

    private static List<(string Word, int Start, int End)> Tokenize(string text)
    {
        var toks = new List<(string, int, int)>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            if (char.IsLetterOrDigit(c) || c == '\'' || c == '-')
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                toks.Add((text[start..i].ToLowerInvariant(), start, i));
                start = -1;
            }
        }
        return toks;
    }

    /// <summary>Find client-name mentions: tokenized n-gram lookup, longest match first.</summary>
    public static List<(int Start, int End, string Value)> Find(string text)
    {
        Load();
        if (_nameNgrams is null || _nameNgrams.Count == 0 || text.Length == 0) return [];
        var toks = Tokenize(text);
        var found = new List<(int, int, string)>();
        var i = 0;
        while (i < toks.Count)
        {
            var matched = false;
            var maxN = Math.Min(MaxNameWords, toks.Count - i);
            for (var n = maxN; n >= 1; n--)
            {
                if (!_nameNgrams.TryGetValue(n, out var bucket)) continue;
                var norm = string.Join(' ', toks.Skip(i).Take(n).Select(t => t.Word));
                if (!bucket.Contains(norm)) continue;
                var end = toks[i + n - 1].End;
                found.Add((toks[i].Start, end, text[toks[i].Start..end]));
                i += n;
                matched = true;
                break;
            }
            if (!matched) i++;
        }
        return found;
    }

    /// <summary>Scrub custom client mentions from the text.</summary>
    public static (string Text, List<Finding> Findings) ScrubCustom(string text, VaultData vault)
    {
        var matches = Find(text);
        if (matches.Count == 0) return (text, []);
        var findings = new List<Finding>();
        foreach (var (start, end, value) in matches.OrderByDescending(m => m.Item1))
        {
            var ph = Sanitizer.Placeholder(vault, "CLIENT", value);
            var line = 1 + text[..start].Count(c => c == '\n');
            findings.Add(new Finding(line, "CLIENT", ph));
            text = text[..start] + "[" + ph + "]" + text[end..];
        }
        findings.Sort((a, b) => a.Line.CompareTo(b.Line));
        return (text, findings);
    }

    // ---- FEINs -------------------------------------------------------------

    private static string DigitsOnly(string s) => new(s.Where(char.IsDigit).ToArray());
    private static bool IsFeinSep(char c) => c is ' ' or '-' or '.' or '/';

    /// <summary>
    /// Find known FEINs in any format. Digit runs — and short separator-joined runs
    /// ("12-3456789", "12 3456 789", "12.3456789") — are normalized to digits and
    /// matched exactly against the set; the set itself is the FP firewall, so only
    /// known values can ever ticket.
    /// </summary>
    public static List<(int Start, int End, string Value)> FindFeins(string text)
    {
        Load();
        if (_feins is null || _feins.Count == 0 || text.Length == 0) return [];

        var runs = new List<(string Digits, int Start, int End)>();
        {
            var start = -1;
            for (var i = 0; i <= text.Length; i++)
            {
                var c = i < text.Length ? text[i] : ' ';
                if (char.IsDigit(c)) { if (start < 0) start = i; }
                else if (start >= 0) { runs.Add((text[start..i], start, i)); start = -1; }
            }
        }

        bool BoundaryOk(int s, int e) =>
            (s == 0 || !char.IsLetterOrDigit(text[s - 1])) &&
            (e >= text.Length || !char.IsLetterOrDigit(text[e]));

        var found = new List<(int, int, string)>();
        var skipUntil = -1;
        for (var r = 0; r < runs.Count; r++)
        {
            if (runs[r].Start < skipUntil) continue;
            var digits = runs[r].Digits;
            var end = runs[r].End;
            var hitEnd = -1;
            // prefer the longest separator-merged match
            for (var e2 = r + 1; e2 < runs.Count; e2++)
            {
                if (digits.Length > 10) break;
                var gap = text[end..runs[e2].Start];
                if (gap.Length is 0 or > 3 || !gap.All(IsFeinSep)) break;
                var merged = digits + runs[e2].Digits;
                if (merged.Length > 10) break;
                digits = merged;
                end = runs[e2].End;
                if (hitEnd < 0 && digits.Length is >= 7 and <= 10 &&
                    _feins.Contains(digits) && BoundaryOk(runs[r].Start, end))
                    hitEnd = end;
            }
            if (hitEnd < 0 && runs[r].Digits.Length is >= 7 and <= 10 &&
                _feins.Contains(runs[r].Digits) && BoundaryOk(runs[r].Start, runs[r].End))
                hitEnd = runs[r].End;
            if (hitEnd > 0)
            {
                found.Add((runs[r].Start, hitEnd, text[runs[r].Start..hitEnd]));
                skipUntil = hitEnd;
            }
        }
        return found;
    }

    /// <summary>Scrub known-FEIN mentions from the text.</summary>
    public static (string Text, List<Finding> Findings) ScrubFeins(string text, VaultData vault)
    {
        var matches = FindFeins(text);
        if (matches.Count == 0) return (text, []);
        var findings = new List<Finding>();
        foreach (var (start, end, value) in matches.OrderByDescending(m => m.Item1))
        {
            var ph = Sanitizer.Placeholder(vault, "CLIENT_FEIN", value);
            var line = 1 + text[..start].Count(c => c == '\n');
            findings.Add(new Finding(line, "CLIENT_FEIN", ph));
            text = text[..start] + "[" + ph + "]" + text[end..];
        }
        findings.Sort((a, b) => a.Line.CompareTo(b.Line));
        return (text, findings);
    }

    // ---- import ------------------------------------------------------------

    /// <summary>Import client names from a CSV or plain-text file (one per line, or CSV with a "name" column).</summary>
    public static int ImportFrom(string filePath)
    {
        var added = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(DataPath))
        {
            var existing = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(DataPath));
            foreach (var n in existing ?? []) names.Add(n);
        }
        foreach (var line in File.ReadAllLines(filePath))
        {
            var name = line.Trim().Trim('"', ',');
            if (name.Length < 2) continue;
            if (names.Add(name)) added++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
        File.WriteAllText(DataPath, JsonSerializer.Serialize(names.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        _namesMtimeUtc = DateTime.MinValue; // force reload
        Load();
        return added;
    }

    /// <summary>Import FEINs from a CSV or plain-text file (one per line; any format — normalized to digits).</summary>
    public static int ImportFeinsFrom(string filePath)
    {
        var added = 0;
        var feins = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(FeinsPath))
        {
            var existing = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FeinsPath));
            foreach (var f in existing ?? []) feins.Add(f);
        }
        foreach (var line in File.ReadAllLines(filePath))
        {
            var d = DigitsOnly(line);
            if (d.Length is < 6 or > 11) continue;
            if (feins.Add(d)) added++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FeinsPath)!);
        File.WriteAllText(FeinsPath, JsonSerializer.Serialize(feins.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        _feinsMtimeUtc = DateTime.MinValue; // force reload
        Load();
        return added;
    }

    /// <summary>Add a single client name programmatically.</summary>
    public static void Add(string name)
    {
        lock (Lock)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(DataPath))
            {
                var existing = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(DataPath));
                foreach (var n in existing ?? []) names.Add(n);
            }
            names.Add(name.Trim());
            Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
            File.WriteAllText(DataPath, JsonSerializer.Serialize(names.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            _namesMtimeUtc = DateTime.MinValue; // force reload
        }
        Load();
    }
}
