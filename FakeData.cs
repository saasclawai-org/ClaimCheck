using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaimCheck;

/// <summary>
/// Realistic mode (SANITIZE_FAKE_DATA=1): supported structured ticket types are
/// replaced with format-valid, self-identifying fake values derived
/// deterministically from the ticket name — same ticket, same fake, forever.
/// Reserved namespaces only (these never collide with real issued values):
///   SSN   9XX-00-XXXX   (area 9XX + group 00 are already excluded by the SSN
///                        recognizer, so fakes never re-mint on history re-sends)
///   FEIN  00-XXXXXXX    (00 prefix is not issued; recognizer excludes it)
///   EMAIL ccXXXXXX@claimcheck.invalid (RFC 2606 .invalid; recognizer excludes it)
///   PHONE (555) 010-XXXX (NANP fictional range; recognizer excludes it)
///   CARD  4XXX-XXXX-XXXX-XXXX (Luhn-valid test card)
/// Unsupported types (PERSON, ORG, LOCATION, PASSWORD, keys, tokens, blobs)
/// keep their bracketed tickets — no reserved namespace exists for free text.
/// </summary>
internal static class FakeData
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("SANITIZE_FAKE_DATA") == "1";

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
        { "SSN", "FEIN", "EMAIL", "PHONE", "CARD" };

    /// <summary>Trailing partial-fake forms to hold back during SSE streaming
    /// (mirrors the bracketed PartialTailRx treatment — a fake split across
    /// chunks must never be emitted unre stored). Includes single-char starts:
    /// a bare trailing '0' (FEIN 00-… start) and 'cc' + hex local-part partials.</summary>
    public static readonly Regex PartialTail = new(
        @"(0?0(-\d{0,7})?|cc[0-9a-f]{0,12}(@claimcheck(\.in)?v?a?l?i?d?)?|[\w.%+-]*@claimcheck(\.in)?v?a?l?i?d?|9\d{0,2}(-\d{0,2}(-\d{0,4})?)?|\(555\) ?010?-?\d{0,4}|4\d{0,3}(-\d{0,4}){0,3})$",
        RegexOptions.IgnoreCase);

    /// <summary>Streaming hold, exact form: the longest buffer suffix that is a
    /// PREFIX of any vault fake is held back (it may complete into a fake on the
    /// next fragment). Class-pattern holds leaked per-character fakes — leftmost
    /// match semantics held short wrong fragments and let the rest escape
    /// (2026-10-30 FEIN/EMAIL streaming leak). Prefix matching is exact.</summary>
    public static string? HeldFakeSuffix(string restored, VaultData vault)
    {
        var maxFake = 0;
        foreach (var ph in vault.Map.Keys)
        {
            var f = For(ph);
            if (f is not null && f.Length > maxFake) maxFake = f.Length;
        }
        if (maxFake == 0) return null;

        var limit = Math.Min(maxFake, restored.Length);
        for (var len = limit; len >= 1; len--)
        {
            var suffix = restored[^len..];
            foreach (var ph in vault.Map.Keys)
            {
                var fake = For(ph);
                if (fake is not null && fake.StartsWith(suffix, StringComparison.Ordinal))
                    return suffix;
            }
        }
        return null;
    }

    public static string? For(string ticket)
    {
        var idx = ticket.LastIndexOf('_');
        if (idx <= 0) return null;
        var type = ticket[..idx];
        if (!Supported.Contains(type)) return null;
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("claimcheck-fake:v1:" + ticket));
        return type switch
        {
            "SSN" => $"9{D(h,0)}{D(h,1)}-00-{D(h,2)}{D(h,3)}{D(h,4)}{D(h,5)}",
            "FEIN" => $"00-{D(h,0)}{D(h,1)}{D(h,2)}{D(h,3)}{D(h,4)}{D(h,5)}{D(h,6)}",
            "EMAIL" => $"cc{Convert.ToHexString(h, 0, 6).ToLowerInvariant()}@claimcheck.invalid",
            "PHONE" => $"(555) 010-{D(h,0)}{D(h,1)}{D(h,2)}{D(h,3)}",
            "CARD" => Card(h),
            _ => null,
        };
    }

    /// <summary>Swap bracketed supported tickets for their stable fake values.</summary>
    public static string Substitute(string text, VaultData vault)
    {
        foreach (var (ph, _) in vault.Map)
        {
            var fake = For(ph);
            if (fake is null) continue;
            text = text.Replace("[" + ph + "]", fake);
        }
        return text;
    }

    /// <summary>Swap fake values back to the real ones (inbound restore).</summary>
    public static string RestoreFake(string text, VaultData vault)
    {
        foreach (var (ph, val) in vault.Map)
        {
            var fake = For(ph);
            if (fake is null) continue;
            text = text.Replace(fake, val);
        }
        return text;
    }

    private static string Card(byte[] h)
    {
        var body = "4";
        for (var i = 0; i < 14; i++) body += D(h, i);
        for (var check = 0; check <= 9; check++)
            if (Sanitizer.Luhn(body + check))
                return $"{body[..4]}-{body[4..8]}-{body[8..12]}-{body[12..]}{check}";
        return body;
    }

    private static string D(byte[] h, int i) => (h[i % h.Length] % 10).ToString();
}
