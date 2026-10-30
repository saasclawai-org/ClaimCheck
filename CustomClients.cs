using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaimCheck;

/// <summary>
/// Custom client-list import: the user's actual client roster as a CSV or plain-text
/// file. Every mention of a listed client gets caught deterministically — no ML,
/// no confidence threshold. The strongest signal for known clients.
/// Stored at ~/.sanitize-mcp/custom_clients.json (JSON array of names).
/// </summary>
public static class CustomClients
{
    private static HashSet<string>? _names;
    private static readonly object Lock = new();
    private static List<Regex>? _matchers;

    public static string DataPath => Path.Combine(
        Sanitizer.HomeDir,
        ".sanitize-mcp", "custom_clients.json");

    public static int Count => _names?.Count ?? 0;
    public static bool Loaded => _names is not null;

    public static void Load()
    {
        if (_names is not null) return;
        lock (Lock)
        {
            if (_names is not null) return;
            try
            {
                if (File.Exists(DataPath))
                {
                    var names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(DataPath));
                    _names = new HashSet<string>(names ?? [], StringComparer.OrdinalIgnoreCase);
                }
                else _names = new HashSet<string>();
            }
            catch { _names = new HashSet<string>(); }
        }
    }

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
            _names = names;
            _matchers = null;
        }
    }

    /// <summary>Find custom client mentions in the text. Returns findings.</summary>
    public static List<(int Start, int End, string Value)> Find(string text)
    {
        Load();
        if (_names is null || _names.Count == 0) return [];
        var found = new List<(int, int, string)>();
        foreach (var client in _names)
        {
            if (client.Length < 2) continue;
            var idx = 0;
            while (true)
            {
                idx = text.IndexOf(client, idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                // word-boundary check: the char before and after must not be a letter/digit
                var before = idx > 0 ? text[idx - 1] : ' ';
                var after = idx + client.Length < text.Length ? text[idx + client.Length] : ' ';
                if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after))
                {
                    found.Add((idx, idx + client.Length, client));
                    break; // one match per client is enough per request
                }
                idx++;
            }
        }
        return found;
    }

    /// <summary>Scrub custom client mentions from the text.</summary>
    public static (string Text, List<Finding> Findings) ScrubCustom(string text, VaultData vault)
    {
        var matches = Find(text);
        if (matches.Count == 0) return (text, []);

        var findings = new List<Finding>();
        var spans = matches.OrderByDescending(m => m.Item1).ToList();
        foreach (var (start, end, value) in spans)
        {
            var ph = Sanitizer.Placeholder(vault, "CLIENT", value);
            var line = 1 + text[..start].Count(c => c == '\n');
            findings.Add(new Finding(line, "CLIENT", ph));
            text = text[..start] + "[" + ph + "]" + text[end..];
        }
        findings.Sort((a, b) => a.Line.CompareTo(b.Line));
        return (text, findings);
    }
}
