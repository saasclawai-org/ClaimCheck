using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaimCheck;

// Optional detection layer backed by a live Microsoft Presidio analyzer service
// (SANITIZE_PRESIDIO_URL, e.g. the official presidio-analyzer container on :3000).
// Findings land in the probabilistic tier: they mint through the same vault (stable
// placeholders + restore) and are labeled with their Presidio entity type, but they
// never enter the validator-backed guarantee tier. Fail-soft: service down, slow or
// malformed => the pass is skipped and the failure is reported via /status.
// Spike evidence (2026-10-26): CRYPTO/PERSON/LOCATION detection the built-ins miss;
// composition order (deterministic tiers first, Presidio on the masked text) validated;
// DATE_TIME false positives on benign prose => excluded by default.
public static class Presidio
{
    private static int _consecutiveFailures;
    private static DateTime _lastFailureUtc;
    private static string? _lastError;

    public static bool Enabled => !string.IsNullOrWhiteSpace(Url);
    public static string? Url => Environment.GetEnvironmentVariable("SANITIZE_PRESIDIO_URL");
    public static int ConsecutiveFailures => _consecutiveFailures;
    public static string? LastFailure => _lastError;
    public static DateTime LastFailureUtc => _lastFailureUtc;

    public static double ScoreThreshold
    {
        get
        {
            var s = Environment.GetEnvironmentVariable("SANITIZE_PRESIDIO_SCORE");
            return double.TryParse(s, out var d) && d is > 0 and <= 1 ? d : 0.4;
        }
    }

    private static HashSet<string>? EntityAllowlist()
    {
        var raw = Environment.GetEnvironmentVariable("SANITIZE_PRESIDIO_ENTITIES");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return new HashSet<string>(
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    // NER noise from the spike: DATE_TIME/TIME flagged benign prose ("May and June are
    // good months") at 0.85 — excluded unless the allowlist explicitly asks for them.
    private static readonly HashSet<string> DefaultExcluded =
        new(StringComparer.OrdinalIgnoreCase) { "DATE_TIME", "TIME" };

    private static readonly Regex PlaceholderRx = new(@"\[[A-Z][A-Z_0-9]*_\d+\]");
    // 30s: a cold Presidio service can spend far more than 5s on its first inference
    // (NLP model load) — a 5s client timeout turned that into a silent fail-soft skip
    // and a raw leak on the first request after idle (2026-10-26 predator field report).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Fire-and-forget warm-up at proxy start: load the analyzer's NLP models before the
    // first user request so the cold-start window can't leak.
    public static void WarmUp()
    {
        if (!Enabled) return;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var endpoint = Url!.TrimEnd('/');
                if (!endpoint.EndsWith("/analyze", StringComparison.OrdinalIgnoreCase)) endpoint += "/analyze";
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                { Content = new StringContent("{\"text\":\"warmup\",\"language\":\"en\",\"score_threshold\":1}", Encoding.UTF8, "application/json") };
                using var resp = await Http.SendAsync(req);
                Console.Error.WriteLine($"claimcheck presidio: warm-up {(int)resp.StatusCode}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"claimcheck presidio: warm-up skipped — {e.Message}");
            }
        });
    }

    public static (string Text, List<Finding> Findings) Scrub(string text, VaultData vault)
    {
        var findings = new List<Finding>();
        if (!Enabled || string.IsNullOrEmpty(text)) return (text, findings);

        var allow = EntityAllowlist();
        var payloadObj = new Dictionary<string, object>
        {
            ["text"] = text,
            ["language"] = "en",
            ["score_threshold"] = ScoreThreshold,
        };
        if (allow is not null) payloadObj["entities"] = allow.OrderBy(x => x).ToList();
        var payload = JsonSerializer.Serialize(payloadObj);

        string body;
        try
        {
            // The URL is the analyzer service base (e.g. http://127.0.0.1:3000); we append
            // /analyze unless the caller already included it.
            var endpoint = Url.TrimEnd('/');
            if (!endpoint.EndsWith("/analyze", StringComparison.OrdinalIgnoreCase)) endpoint += "/analyze";
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            using var resp = Http.SendAsync(req).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                RecordFailure($"upstream status {(int)resp.StatusCode}");
                return (text, findings);
            }
            body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            RecordFailure("analyzer call: " + e.Message);
            return (text, findings);
        }

        JsonElement items;
        try
        {
            using var doc = JsonDocument.Parse(body);
            items = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            RecordFailure("unparseable response: " + e.Message);
            return (text, findings);
        }
        if (items.ValueKind != JsonValueKind.Array) return (text, findings);

        var hits = new List<(int Start, int End, string Type)>();
        foreach (var e in items.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (!e.TryGetProperty("entity_type", out var tEl)) continue;
            if (!e.TryGetProperty("start", out var sEl) || !e.TryGetProperty("end", out var eEl)) continue;
            var type = tEl.GetString();
            if (string.IsNullOrWhiteSpace(type)) continue;
            if (allow is null && DefaultExcluded.Contains(type)) continue;
            if (allow is not null && !allow.Contains(type)) continue;
            int start, end;
            try { start = sEl.GetInt32(); end = eEl.GetInt32(); }
            catch (InvalidOperationException) { continue; }
            if (start < 0 || end <= start || end > text.Length) continue;
            hits.Add((start, end, type.ToUpperInvariant()));
        }

        // Right-to-left splice (offsets stay valid), skipping spans that overlap an
        // already-spliced region or contain an existing placeholder token.
        var acceptedStart = int.MaxValue;
        foreach (var h in hits.OrderByDescending(h => h.Start))
        {
            if (h.End > acceptedStart) continue;
            var seg = text[h.Start..h.End];
            if (PlaceholderRx.IsMatch(seg)) continue;
            var ph = Sanitizer.Placeholder(vault, h.Type, seg);
            var line = 1 + text[..h.Start].Count(c => c == '\n');
            findings.Add(new Finding(line, h.Type, ph));
            text = text[..h.Start] + "[" + ph + "]" + text[h.End..];
            acceptedStart = h.Start;
        }
        return (text, findings);
    }

    private static void RecordFailure(string message)
    {
        _consecutiveFailures = Interlocked.Increment(ref _consecutiveFailures);
        _lastFailureUtc = DateTime.UtcNow;
        _lastError = message;
        if (_consecutiveFailures == 1 || _consecutiveFailures % 25 == 0)
            Console.Error.WriteLine($"claimcheck presidio: pass skipped ({_consecutiveFailures} consecutive) — {message}");
    }
}
