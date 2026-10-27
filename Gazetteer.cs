using System.Text.Json;

namespace ClaimCheck;

/// <summary>
/// English-names gazetteer: SSA given names + Census surnames (250K+ entries).
/// Detects first+last name bigrams in prose — the values regex can't pattern-match.
/// Deterministic, no ML model, no network. Complements the NER layer.
/// </summary>
public static class Gazetteer
{
    private static HashSet<string>? _names;
    private static readonly object LoadLock = new();

    public static bool Loaded => _names is not null;
    public static int NameCount => _names?.Count ?? 0;

    /// <summary>Check if a single name is in the gazetteer (any casing).</summary>
    public static bool HasName(string name)
    {
        EnsureLoaded();
        return _names?.Contains(name) ?? false;
    }

    public static void EnsureLoaded()
    {
        if (_names is not null) return;
        lock (LoadLock)
        {
            if (_names is not null) return;
            var asm = typeof(Gazetteer).Assembly;
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith("GazetteerNames.json", StringComparison.OrdinalIgnoreCase));
            if (resName is null) { _names = new HashSet<string>(); Console.Error.WriteLine($"claimcheck gazetteer: resource NOT FOUND"); return; }
            Console.Error.WriteLine($"claimcheck gazetteer: loading {resName}");
            using var stream = asm.GetManifestResourceStream(resName);
            var names = JsonSerializer.Deserialize<List<string>>(stream!) ?? new List<string>();
            _names = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            Console.Error.WriteLine($"claimcheck gazetteer: loaded {_names.Count} names");
        }
    }

    /// <summary>Default OFF (v0.8.3): bigram matching over structured/code payloads traded
    /// utility for corruption-class false positives — and a skipped-token adjacency bug let
    /// fake bigrams span lines and eat line labels (battery repro 2026-10-27, items 5-7:
    /// "[CARD_7] … 6. IBAN" matched as the name "Card Iban"). Opt in with
    /// SANITIZE_GAZETTEER=1 for prose-heavy sources; presidio's NER covers free-text
    /// names when enabled.</summary>
    public static bool Enabled => string.Equals(
        Environment.GetEnvironmentVariable("SANITIZE_GAZETTEER"), "1", StringComparison.Ordinal);

    /// <summary>Find "First Last" bigrams where both words are in the gazetteer.
    /// Returns the scrubbed text (names → PERSON placeholders) and findings.</summary>
    public static (string Text, List<Finding> Findings) Scrub(string text, VaultData vault)
    {
        if (!Enabled) return (text, new List<Finding>());
        EnsureLoaded();
        if (_names is null || _names.Count == 0) return (text, new List<Finding>());

        var words = TokenizeWords(text);
        var spans = new List<(int Start, int End, string Value)>();
        for (var i = 0; i < words.Count - 1; i++)
        {
            var (w1, s1, e1) = words[i];
            var (w2, s2, e2) = words[i + 1];
            // v0.8.3 fix: the words must be TRUE neighbors — exactly one space between
            // them. TokenizeWords skips digits/punctuation/brackets, so the old check
            // (just "char before w2 is a space") let "[CARD_7] … 6. IBAN" become a fake
            // "Card Iban" bigram whose span crossed lines and ate the line labels.
            if (s2 != e1 + 1 || text[e1] != ' ') continue;
            if (!_names.Contains(w1) || !_names.Contains(w2)) continue;
            if (!char.IsUpper(w1[0]) || !char.IsUpper(w2[0])) continue;
            spans.Add((s1, e2, $"{w1} {w2}"));
            i++; // skip the last-name token
        }
        if (spans.Count == 0) return (text, new List<Finding>());

        // replace right-to-left
        spans.Sort((a, b) => b.Start.CompareTo(a.Start));
        var findings = new List<Finding>();
        foreach (var (start, end, value) in spans)
        {
            var ph = Sanitizer.Placeholder(vault, "PERSON", value);
            var line = 1 + text[..start].Count(c => c == '\n');
            findings.Add(new Finding(line, "PERSON", ph));
            text = text[..start] + "[" + ph + "]" + text[end..];
        }
        return (text, findings);
    }

    private static List<(string Word, int Start, int End)> TokenizeWords(string text)
    {
        var words = new List<(string, int, int)>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            if (char.IsLetter(c) || c == '\'' || c == '-')
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                words.Add((text[start..i], start, i));
                start = -1;
            }
        }
        return words;
    }
}
