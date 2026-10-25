using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaimCheck;

/// <summary>
/// `ClaimCheck status` — local visibility: is the scrub proxy running (and on which
/// port/upstream), NER on/off, vault size, and which MCP hosts have ClaimCheck wired.
/// Machine-readable with `status --json`.
/// </summary>
public static class Status
{
    public static string Version =>
        typeof(Status).Assembly.GetName().Version is { } v ? v.ToString(3) : "0.0.0";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(900) };

    public static int Run(string[] args)
    {
        var port = 8766;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) port = p;
        var info = Collect(port);
        if (args.Contains("--json"))
        {
            Console.Out.Write(JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            return info.ProxyRunning ? 0 : 3; // 3 = proxy not detected (script-friendly)
        }
        WriteHuman(info);
        return info.ProxyRunning ? 0 : 3;
    }

    public sealed record Info(
        string Version,
        bool ProxyRunning,
        int ProxyPort,
        string? Upstream,
        string? Vault,
        long? VaultEntries,
        long? TotalRequests,
        long? ScrubbedRequests,
        string? LastRequestUtc,
        string? StartedUtc,
        bool? NerEnabled,
        bool? NerModelCached,
        bool? FailClosed,
        bool ProxyVersionOld,
        string NerMode,
        string VaultSummary,
        string[] McpWiring,
        bool? NerFailed,
        string? NerFailReason);

    public static Info Collect(int port)
    {
        ProxyStatusDto? live = null;
        var running = Probe(port, out live);

        var (nerMode, nerCached) = NerStatus();
        return new Info(
            Version,
            running,
            port,
            live?.Upstream,
            live?.Vault,
            live?.VaultEntries,
            live?.TotalRequests,
            live?.ScrubbedRequests,
            live?.LastRequestUtc,
            live?.StartedUtc,
            live?.Ner ?? Ner.Enabled,
            nerCached,
            live?.FailClosed,
            running && live is null,
            nerMode,
            VaultSummary(),
            McpWiring(),
            live?.NerFailed, live?.NerFailReason);
    }

    private static bool Probe(int port, out ProxyStatusDto? status)
    {
        status = null;
        try
        {
            using var resp = Http.GetAsync($"http://127.0.0.1:{port}/health").GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return false;
            // /health returns "ok" on every build; try the richer /status (0.3.0+).
            try
            {
                using var s = Http.GetAsync($"http://127.0.0.1:{port}/status").GetAwaiter().GetResult();
                if (s.IsSuccessStatusCode)
                    status = JsonSerializer.Deserialize<ProxyStatusDto>(s.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
            catch { /* old build without /status */ }
            return true;
        }
        catch { return false; }
    }

    private static (string Mode, bool Cached) NerStatus()
    {
        if (!Ner.Enabled) return ("off (SANITIZE_NER=0 to disable)", false);
        var modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claimcheck", "models",
            string.Join('_', Ner.ModelRepo.Split('/')));
        var cached = Directory.Exists(modelDir) && Directory.EnumerateFiles(modelDir).Any();
        return (cached ? "on (model cached)" : "on (model downloads on first use, ~110 MB)", cached);
    }

    private static string VaultSummary()
    {
        var dir = Sanitizer.VaultDir;
        if (!Directory.Exists(dir)) return $"{dir} — empty";
        long files = 0, entries = 0, encrypted = 0, plain = 0, locked = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            files++;
            var d = VaultCrypto.Describe(f);
            entries += d.Entries ?? 0;
            if (d.Error is not null) locked++;
            else if (d.Protection == "plaintext") plain++;
            else encrypted++;
        }
        var parts = new List<string>();
        if (encrypted > 0) parts.Add($"{encrypted} encrypted");
        if (plain > 0) parts.Add($"{plain} plaintext");
        if (locked > 0) parts.Add($"{locked} locked");
        var prot = parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : "";
        return $"{dir} — {files} vault{(files == 1 ? "" : "s")}{prot}, {entries} entr{(entries == 1 ? "y" : "ies")}";
    }

    private static string[] McpWiring()
    {
        var found = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var (name, path) in new[]
        {
            ("Cline (VS Code)", Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json")),
            ("Cline (Insiders)", Path.Combine(appData, "Code - Insiders", "User", "globalStorage", "saoudrizwan.claude-dev-insiders", "settings", "cline_mcp_settings.json")),
        })
        {
            var st = CheckMcpJson(path);
            if (st is not null) found.Add($"{name}: {st}");
        }
        var openclaw = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "openclaw.json");
        var st2 = CheckMcpJson(openclaw, ["mcp", "servers"], "claimcheck");
        if (st2 is not null) found.Add($"OpenClaw: {st2}");
        return found.ToArray();
    }

    private static string? CheckMcpJson(string path, string[]? pathSegments = null, string key = "claimcheck")
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement node = doc.RootElement;
            if (pathSegments is not null)
            {
                foreach (var seg in pathSegments)
                {
                    if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(seg, out node))
                        return null;
                }
            }
            else if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("mcpServers", out node))
                return null;
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out var entry))
                return null;
            var disabled = entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("disabled", out var d)
                && d.ValueKind == JsonValueKind.True;
            return disabled ? "wired but disabled" : "wired";
        }
        catch { return null; }
    }

    private static void WriteHuman(Info i)
    {
        Console.Out.WriteLine($"ClaimCheck {i.Version}");
        Console.Out.WriteLine();
        if (i.ProxyRunning)
        {
            Console.Out.WriteLine($"proxy      RUNNING on 127.0.0.1:{i.ProxyPort}");
            if (i.Upstream is not null) Console.Out.WriteLine($"           -> {i.Upstream}");
            if (i.TotalRequests is { } t)
                Console.Out.WriteLine($"           {t} request{(t == 1 ? "" : "s")} proxied · {i.ScrubbedRequests} scrubbed · last {i.LastRequestUtc ?? "n/a"}");
            if (i.Vault is not null)
                Console.Out.WriteLine($"           vault {i.Vault} ({i.VaultEntries} entries)");
            if (i.StartedUtc is not null) Console.Out.WriteLine($"           up since {i.StartedUtc}");
            if (i.FailClosed is { } fc) Console.Out.WriteLine($"           fail-closed: {(fc ? "on" : "OFF (SANITIZE_FAIL_CLOSED=0)")}");
            if (i.ProxyVersionOld) Console.Out.WriteLine("           (running an older build — no /status detail; update to see counters)");
        }
        else
        {
            Console.Out.WriteLine($"proxy      not detected on 127.0.0.1:{i.ProxyPort}");
            Console.Out.WriteLine("           start with: ClaimCheck --proxy --upstream <base-url> [--port 8766]");
            Console.Out.WriteLine("           Windows: install.ps1 -WithProxy registers it at logon (tray icon).");
        }
        Console.Out.WriteLine($"scrub NER  {i.NerMode}");
        if (i.NerFailed == true)
        {
            Console.Out.WriteLine($"           NER FAILED: {i.NerFailReason}");
            Console.Out.WriteLine("           -> fix the cause, then restart the proxy/tray to retry");
        }
        if (i.FailClosed is { } fc2 && !i.ProxyRunning) Console.Out.WriteLine($"fail-closed: {(fc2 ? "on" : "OFF")}");
        Console.Out.WriteLine($"vault dir  {i.VaultSummary}");
        Console.Out.WriteLine("MCP wiring " + (i.McpWiring.Length == 0
            ? "none detected"
            : string.Join(" · ", i.McpWiring)));
    }

    internal sealed record ProxyStatusDto(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("upstream")] string Upstream,
        [property: JsonPropertyName("vault")] string Vault,
        [property: JsonPropertyName("vaultEntries")] long VaultEntries,
        [property: JsonPropertyName("totalRequests")] long TotalRequests,
        [property: JsonPropertyName("scrubbedRequests")] long ScrubbedRequests,
        [property: JsonPropertyName("lastRequestUtc")] string? LastRequestUtc,
        [property: JsonPropertyName("startedUtc")] string? StartedUtc,
        [property: JsonPropertyName("ner")] bool Ner,
        [property: JsonPropertyName("failClosed")] bool FailClosed,
        [property: JsonPropertyName("nerFailed")] bool? NerFailed,
        [property: JsonPropertyName("nerFailReason")] string? NerFailReason);
}
