using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClaimCheck;

public sealed record Finding(int Line, string Type, string Placeholder);

public sealed class VaultData
{
    [JsonPropertyName("map")] public Dictionary<string, string> Map { get; set; } = new();
    [JsonPropertyName("reverse")] public Dictionary<string, string> Reverse { get; set; } = new();
    [JsonPropertyName("counts")] public Dictionary<string, int> Counts { get; set; } = new();
}

public static class Sanitizer
{
    public const int MaxBytes = 2_000_000;

    public static string VaultDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sanitize-mcp", "vaults");

    /// <summary>Guards vault mutation (Placeholder minting) and file save/load.
    /// Vault ops are in-memory microseconds; one coarse lock keeps concurrent proxy requests safe.</summary>
    public static readonly object VaultLock = new();

    // SANITIZE_ROOTS — separator is platform-native (';' on Windows, ':' on Unix). Empty = unrestricted (local stdio use).
    public static string[] Roots { get; } = (Environment.GetEnvironmentVariable("SANITIZE_ROOTS") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(r => Path.GetFullPath(Environment.ExpandEnvironmentVariables(r)))
        .ToArray();

    public static void CheckRoot(string fullPath)
    {
        if (Roots.Length == 0) return;
        foreach (var root in Roots)
        {
            var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, r, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;
        }
        throw new UnauthorizedAccessException($"path outside SANITIZE_ROOTS allowlist: {fullPath}");
    }

    public static bool Luhn(string s)
    {
        var digits = new List<int>();
        foreach (var ch in s) if (char.IsAsciiDigit(ch)) digits.Add(ch - '0');
        if (digits.Count is < 13 or > 19) return false;
        int total = 0;
        for (int i = 0; i < digits.Count; i++)
        {
            int d = digits[^(i + 1)];
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            total += d;
        }
        return total % 10 == 0;
    }

    private static readonly (string Type, Regex Rx, Func<string, bool>? Validator)[] BuiltInPatterns =
    {
        ("FEIN",       new Regex(@"\b\d{2}-\d{7}\b"), null),
        ("SSN",        new Regex(@"\b(?!000|666|9\d\d)\d{3}[- ](?!00)\d{2}[- ](?!0000)\d{4}\b"), null),
        ("EMAIL",      new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"), null),
        ("IMEI",           new Regex(@"(?i)\b(?:imei)\s*(?:no\.?|number|#)?\s*[:=]?\s*\d{15}\b"), s => Sanitizer.Luhn(s[^15..])),
        // IBAN before CARD: a 13-19 digit run inside a long IBAN can match CARD's generic
        // pattern (and even pass Luhn), fragmenting the IBAN and leaking its tail
        // (2026-10-26 field leak: "IBAN: [IBAN_1] 00").
        ("IBAN",       new Regex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,8}(?: ?[A-Z0-9]{2,4})?\b"), null),
        // CARD keyword-anchored, no Luhn gate: sandbox/test cards and mistyped real cards
        // fail the checksum, but a card keyword makes intent unambiguous (2026-10-26 leak:
        // "Card: 5500 0000 5555 5559" passed through plain because Luhn rejects it).
        ("CARD",       new Regex(@"(?i)\b(?:card(?:\s*holder)?|credit|debit|visa|mastercard|amex|cc)\s*(?:no\.?|number|#)?\s*[:=]?\s*(?:\d[ -]?){13,19}(?!\d)"), null),
        ("CARD",       new Regex(@"(?<!\d)(?:\d[ -]?){13,19}(?!\d)"), Luhn),
        ("PHONE",      new Regex(@"(?<![\d-])(\+?1[-. ])?(\(\d{3}\)[- ]|\d{3}[-. ])\d{3}[-. ]\d{4}(?!\d)"), null),
        ("AWS_KEY",    new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"), null),
        ("GH_TOKEN",   new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,255}\b"), null),
        ("API_KEY",    new Regex(@"\bsk[-_][A-Za-z0-9_-]{16,}\b"), null),
        ("SLACK_TOKEN",new Regex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"), null),
        ("JWT",        new Regex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*"), null),
        // Passwords & secrets behind password/pwd/passwd/passphrase/secret + : or = — covers
        // JSON ("password": "x"), YAML (password: x), quoted .env (password="x"), prefixed keys
        // (DB_PASSWORD=x, CLIENT_SECRET=x) and plain password=x. Label + value are masked as one
        // span (the placeholder name carries the type); quoted values may contain spaces.
        // (2026-10-26 professional-audit finding: the old = -anchored pattern leaked the
        // JSON/YAML/quoted/prefixed variants.)
        ("PASSWORD",   new Regex(@"(?i)(?:^|(?<=[^A-Za-z0-9]))[""']?(?:password|passwd|pwd|passphrase|secret)[""']?\s*[:=]\s*(""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;""'&\}\[\]]+)"), null),
        ("CVV",            new Regex(@"(?i)\b(?:cvv|cvc|card verification(?: code)?|security code)\s*[:=]\s*\d{3,4}\b"), null),
        ("EXPIRY",         new Regex(@"(?i)\b(?:exp(?:ires?|iry)?|valid thru|good thru)\s*[:=]?\s*(?:0[1-9]|1[0-2])[/-]\d{2,4}\b"), null),
        ("DOB",            new Regex(@"(?i)\b(?:dob|date of birth|born on)\s*[:=]?\s*\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}\b"), null),
        ("PASSPORT",       new Regex(@"(?i)\bpassport\s*(?:no\.?|number|#)?\s*[:=]?\s*[A-Z0-9]{6,9}\b"), null),
        // DATE: full numeric dates (4-digit year only — bare years are Safe-Harbor-safe) and
        // named-month forms. Regex enforces month 01-12 / day 01-31; Feb-31 passes but the
        // cost is a reversible mask, not data loss.
        ("DATE",           new Regex(@"(?i)\b(?:(?:0?[1-9]|1[0-2])[/.-](?:0?[1-9]|[12]\d|3[01])[/.-](?:19|20)\d{2}|(?:19|20)\d{2}[-/.](?:0?[1-9]|1[0-2])[-/.](?:0?[1-9]|[12]\d|3[01])|(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+\d{1,2},?\s+(?:19|20)\d{2}|\d{1,2}\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?,?\s+(?:19|20)\d{2})\b"), null),
        ("DL",             new Regex(@"(?i)\b(?:driver'?s?\s+lic(?:ense)?|dl)\s*(?:no\.?|number|#)?\s*[:=]?\s*[A-Z0-9]{5,12}\b"), null),
        ("ROUTING",        new Regex(@"(?i)\b(?:routing|aba)\s*(?:no\.?|number|#)?\s*[:=]?\s*\d{9}\b"), null),
        ("IP",             new Regex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b"), null),
        ("MEDICAL_LICENSE",new Regex(@"(?i)\b(?:medical\s+lic(?:ense)?|lic(?:ense)?)\s*(?:no\.?|number|#)?\s*[:=]?\s*[A-Z]{2}-[A-Z]{2}-\d{4,8}\b"), null),
        ("INSURANCE_ID",   new Regex(@"(?i)\b(?:member|policy|insurance)\s*(?:id|#|number)?\s*[:=]?\s*[A-Z]{2,5}[- ]\d{4,12}\b"), null),
        ("ACCOUNT",        new Regex(@"(?i)(?<![A-Za-z])(?:bank\s+)?(?:acct|account)\s*(?:no\.?|number|#)?\s*[:=]?\s*\d{4,17}\b"), null),
        ("ID_LABEL",       new Regex(@"(?i)\b(?:customer|client|invoice|policy|case|member|patient|mrn|record|serial|sn|device|biometric|fingerprint|voiceprint|claim|order|ticket|file|ref(?:erence)?|project|contract|vendor|supplier|emp(?:loyee)?)\s*(?:no\.?|number|#|id)?\s*[:=]?\s*\d{4,17}\b"), null),
        ("VIN",            new Regex(@"(?i)\bvin\s*(?:no\.?|number|#)?\s*[:=]?\s*[A-HJ-NPR-Z0-9]{17}\b"), null),
        ("ADDRESS",        new Regex(@"(?i)\b\d{1,5}\s+(?:[A-Z][a-z]+\s){1,4}(?:street|st|avenue|ave|blvd|boulevard|road|rd|drive|dr|lane|ln|court|ct|way|terrace|ter|place|pl|circle|cir)\.?,?\s+[A-Z][a-zA-Z]+(?:\s+[A-Z][a-zA-Z]+)*,?\s+[A-Z]{2}\s+\d{5}(?:-\d{4})?\b"), null),
    };

    public static List<(string Type, Regex Rx, Func<string, bool>?)> BuiltIn() => BuiltInPatterns.ToList();

    private static readonly Regex EncodedBlobRx = new Regex(@"[A-Za-z0-9+/]{48,}={0,2}|[0-9a-fA-F]{96,}");

    /// <summary>Detect base64/hex blobs, decode them, and mask the whole blob when the decoded
    /// content contains matches. Closes the "base64 .env" / hex-dump leak that defeats pattern
    /// matching. The original blob goes into the vault and is restored on output.</summary>
    public static (string Text, int Count) ScrubEncodedBlobs(string text, VaultData vault,
        List<(string Type, Regex Rx, Func<string, bool>?)> patterns)
    {
        var count = 0;
        var result = EncodedBlobRx.Replace(text, m =>
        {
            var blob = m.Value;
            string replacement = null;
            var found = 0;
            if (blob.Length % 2 == 0 && blob.All(Uri.IsHexDigit))
            {
                try
                {
                    var d = Encoding.UTF8.GetString(Convert.FromHexString(blob));
                    var (lines2, f2) = SanitizeLines(d.Split('\n'), patterns, vault);
                    if (f2.Count > 0) { replacement = string.Join('\n', lines2); found = f2.Count; }
                }
                catch { }
            }
            if (replacement is null)
            {
                try
                {
                    var d = Encoding.UTF8.GetString(Convert.FromBase64String(blob));
                    var (lines2, f2) = SanitizeLines(d.Split('\n'), patterns, vault);
                    if (f2.Count > 0) { replacement = string.Join('\n', lines2); found = f2.Count; }
                }
                catch { }
            }
            if (replacement is null) return blob;
            count += found;
            return "[" + Placeholder(vault, "ENCODED", blob) + "]";
        });
        return (result, count);
    }

    private static readonly Regex PemBlockRx = new Regex(
        @"-----BEGIN ((?:[A-Z0-9]+ )*PRIVATE KEY(?: BLOCK)?)-----[\s\S]*?-----END \1-----",
        RegexOptions.Compiled);

    /// <summary>Mask PEM private key blocks (BEGIN..END, multi-line) directly. The base64
    /// body decodes to binary DER, which the encoded-blob scanner cannot flag by content —
    /// so the whole block goes into the vault as one ticket.</summary>
    public static (string Text, int Count) ScrubPemBlocks(string text, VaultData vault)
    {
        var count = 0;
        var result = PemBlockRx.Replace(text, m =>
        {
            count++;
            return "[" + Placeholder(vault, "PRIVATE_KEY", m.Value) + "]";
        });
        return (result, count);
    }

    public static List<(string Type, Regex Rx, Func<string, bool>?)> PatternsFor(DirectoryInfo root)
    {
        var list = BuiltInPatterns.ToList();
        var f = Path.Combine(root.FullName, ".sanitize-patterns.json");
        if (File.Exists(f))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string name =
                        (el.TryGetProperty("placeholder", out var phEl) ? phEl.GetString() : null) ??
                        (el.TryGetProperty("name", out var nmEl) ? nmEl.GetString() : null) ?? "CUSTOM";
                    var rx = el.GetProperty("regex").GetString() ?? throw new InvalidOperationException("missing regex");
                    list.Add((name.ToUpperInvariant(), new Regex(rx), null));
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"sanitize: ignoring bad .sanitize-patterns.json: {e.Message}");
            }
        }
        return list;
    }

    public static DirectoryInfo ProjectRoot(DirectoryInfo start)
    {
        for (var dir = start; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".git")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".clinerules")) ||
                File.Exists(Path.Combine(dir.FullName, ".sanitize-patterns.json")))
                return dir;
        }
        return start;
    }

    public static (FileInfo File, VaultData Data) LoadVault(DirectoryInfo root)
    {
        Directory.CreateDirectory(VaultDir);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(root.FullName))).ToLowerInvariant()[..12];
        var file = new FileInfo(Path.Combine(VaultDir, key + ".json"));
        VaultData v = new();
        if (file.Exists)
        {
            // v0.6.0: an encrypted vault that cannot be decrypted THROWS rather than
            // silently returning an empty vault (which would overwrite the real mapping
            // on the next save). Plain-text JSON-corruption keeps the legacy fallback.
            string json = VaultCrypto.ReadJson(file.FullName);
            try
            {
                v = JsonSerializer.Deserialize<VaultData>(json) ?? new VaultData();
                // self-heal: reverse is fully derivable from the map with typed keys,
                // which also migrates legacy files that keyed reverse on the bare value
                v.Reverse.Clear();
                foreach (var (ph, val) in v.Map)
                {
                    var t = ph[..ph.LastIndexOf('_')];
                    v.Reverse[t + "|" + val] = ph;
                }
            }
            catch { v = new VaultData(); }
        }
        return (file, v);
    }

    public static void SaveVault(FileInfo file, VaultData v)
    {
        lock (VaultLock)
        {
            var tmp = file.FullName + ".tmp";
            VaultCrypto.WriteJson(tmp, JsonSerializer.Serialize(v, new JsonSerializerOptions { WriteIndented = true }));
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }
            // Atomic publish: readers see the old file or the new one, never a torn write.
            File.Move(tmp, file.FullName, overwrite: true);
        }
        // At-rest protection: Windows = DPAPI (CurrentUser) by default, or AES-GCM with
        // SANITIZE_VAULT_PASSWORD; Linux = plaintext + 0600 unless SANITIZE_VAULT_PASSWORD is set.
    }

    public static string Placeholder(VaultData v, string type, string val)
    {
        lock (VaultLock)
        {
            var key = type + "|" + val;
            if (v.Reverse.TryGetValue(key, out var existing)) return existing;
            var n = (v.Counts.TryGetValue(type, out var c) ? c : 0) + 1;
            v.Counts[type] = n;
            var ph = $"{type}_{n}";
            v.Reverse[key] = ph;
            v.Map[ph] = val;
            return ph;
        }
    }

    public static (List<string> Lines, List<Finding> Findings) SanitizeLines(
        string[] lines, List<(string Type, Regex Rx, Func<string, bool>?)> patterns, VaultData vault)
    {
        var result = new List<string>(lines.Length);
        var findings = new List<Finding>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (var (type, rx, validator) in patterns)
            {
                line = rx.Replace(line, m =>
                {
                    var val = m.Value;
                    if (validator is not null && !validator(val)) return val;
                    var ph = Placeholder(vault, type, val);
                    findings.Add(new Finding(i + 1, type, ph));
                    return "[" + ph + "]";
                });
            }
            result.Add(line);
        }
        return (result, findings);
    }

    public static Dictionary<string, object> Summary(List<Finding> findings) => new()
    {
        ["total"] = findings.Count,
        ["by_type"] = findings.GroupBy(f => f.Type).ToDictionary(g => g.Key, g => g.Count()),
    };

    public static (string Text, string FullPath) LoadTextFile(string path)
    {
        if (path.StartsWith('~'))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[1..].TrimStart('/', '\\'));
        var p = Path.GetFullPath(path);
        CheckRoot(p);
        if (!File.Exists(p)) throw new FileNotFoundException($"not found: {p}");
        var fi = new FileInfo(p);
        if (fi.Length > MaxBytes) throw new InvalidDataException($"file too large ({fi.Length} bytes > {MaxBytes}); split it first");
        using (var fs = File.OpenRead(p))
        {
            Span<byte> head = stackalloc byte[8192];
            var read = fs.Read(head);
            foreach (var b in head[..read]) if (b == 0) throw new InvalidDataException("looks binary — refusing to scrub");
        }
        using var sr = new StreamReader(p, Encoding.UTF8);
        return (sr.ReadToEnd(), p);
    }

    public static DirectoryInfo DirOf(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        return new DirectoryInfo(string.IsNullOrEmpty(dir) ? Path.GetPathRoot(fullPath) ?? "/" : dir);
    }
}
