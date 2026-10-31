using System.Text;

namespace ClaimCheck;

/// <summary>Tier-0 normalization (v0.9.3, spec: docs/INJECTION-TIER.md §Tier 0).
/// Recognizers match against a normalized VIEW of the text — zero-width/format
/// characters stripped, NFKC fold, homoglyph lookalikes folded to ASCII — while
/// values extracted into the vault stay byte-exact originals, so restore is
/// unchanged. Borrowed philosophy: Sunglasses' Clean stage, natively in C#.</summary>
public static class Normalize
{
    /// <summary>Zero-width and soft-hyphen characters: invisible smuggle carriers.</summary>
    public static bool IsInvisible(char c) =>
        (c >= '\u200B' && c <= '\u200F') ||
        (c >= '\u2060' && c <= '\u206F') ||
        c == '\uFEFF' || c == '\u00AD';

    /// <summary>Bidi control chars (RTL override / isolates) — the classic "reorder
    /// the display so the model reads A but the human sees B" smuggle. Flagged as
    /// findings, never rewritten.</summary>
    public static bool IsBidiControl(char c) =>
        (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069');

    // Homoglyph lookalikes: Cyrillic + Greek confusables that fold to ASCII.
    // Fullwidth forms are handled by NFKC, not this table.
    private static readonly Dictionary<char, char> Homoglyph = new()
    {
        // Cyrillic
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c',
        ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ѕ'] = 's', ['ј'] = 'j',
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M',
        ['Н'] = 'H', ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T',
        ['У'] = 'Y', ['Х'] = 'X', ['І'] = 'I',
        // Greek
        ['ο'] = 'o', ['α'] = 'a', ['ε'] = 'e', ['ρ'] = 'p', ['ν'] = 'v',
        ['τ'] = 't', ['ι'] = 'i', ['κ'] = 'k', ['β'] = 'B', ['Η'] = 'H',
    };

    private static string Fold(char c)
    {
        if (c < 0x80) return c.ToString();
        try
        {
            var n = c.ToString().Normalize(NormalizationForm.FormKC);
            if (n.Length == 1)
            {
                var f = n[0];
                if (f < 0x80) return f.ToString();
                if (Homoglyph.TryGetValue(char.ToLowerInvariant(f), out var a) && char.IsUpper(f) == char.IsUpper(n[0]))
                {
                    // case-matched fold only when the table entry case agrees
                    if (char.IsUpper(f) == char.IsUpper(a)) return a.ToString();
                }
                if (Homoglyph.TryGetValue(f, out var a2)) return a2.ToString();
            }
            return n;
        }
        catch { return c.ToString(); }
    }

    /// <summary>Normalize for matching. Returns the normalized string plus a map:
    /// map[i] = original index of the source character that produced Norm[i].
    /// Every normalized char is attributable to exactly one original index, so a
    /// regex match on the view maps back to a byte-exact original span.</summary>
    public static (string Norm, int[] Map) ForMatch(string s)
    {
        var sb = new StringBuilder(s.Length);
        var map = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (IsInvisible(c) || IsBidiControl(c)) continue;
            var folded = Fold(c);
            foreach (var ch in folded) { sb.Append(ch); map.Add(i); }
        }
        return (sb.ToString(), map.ToArray());
    }
}
