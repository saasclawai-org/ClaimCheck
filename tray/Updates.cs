using System.Text.Json.Nodes;

/// <summary>Tray-owned settings (~/.claimcheck/tray.json). NER + Presidio toggles plus a
/// generic knob store for the env-var settings surfaced in Settings… (dates, injection
/// patterns, audit log, NER type filter, path allowlist). Applied to this process's
/// environment BEFORE the proxy starts; every scrubbed knob reads its env live per call,
/// so toggling takes effect on the next request without a proxy restart.</summary>
internal static class TrayConfig
{
    private static readonly string ConfigPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claimcheck", "tray.json");

    public static bool NerEnabled { get; private set; }
    public static bool PresidioEnabled { get; private set; } = true;
    public static string? PresidioUrl { get; private set; }
    private static readonly Dictionary<string, string> Knobs = new();

    public static string? GetKnob(string key) => Knobs.TryGetValue(key, out var v) ? v : null;
    public static bool KnobOn(string key) => GetKnob(key) is "1" or "on" or "true";

    /// <summary>Set (or clear with null/empty) a knob env var and persist. Live-read by the
    /// scrub engine, so the next request already uses the new value.</summary>
    public static void SetKnob(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) Knobs.Remove(key);
        else Knobs[key] = value;
        Apply();
        Persist();
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var node = JsonNode.Parse(File.ReadAllText(ConfigPath));
                if (node?["nerEnabled"] is JsonValue v && v.TryGetValue<bool>(out var b))
                    NerEnabled = b;
                if (node?["presidioEnabled"] is JsonValue pv && pv.TryGetValue<bool>(out var pb))
                    PresidioEnabled = pb;
                if (node?["knobs"] is JsonObject ks)
                    foreach (var (k, val) in ks)
                        if (val is JsonValue kv && kv.TryGetValue<string>(out var sv) && !string.IsNullOrWhiteSpace(sv))
                            Knobs[k] = sv;
            }
        }
        catch { /* corrupt config = defaults */ }
        PresidioUrl = Environment.GetEnvironmentVariable("SANITIZE_PRESIDIO_URL");
        Apply();
    }

    public static void SetNer(bool on)
    {
        NerEnabled = on;
        Apply();
        Persist();
    }

    public static void SetPresidio(bool on)
    {
        PresidioEnabled = on;
        Apply();
        Persist();
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigPath)!);
            var knobs = new JsonObject();
            foreach (var (k, val) in Knobs) knobs[k] = val;
            var node = new JsonObject
            {
                ["nerEnabled"] = NerEnabled,
                ["presidioEnabled"] = PresidioEnabled,
                ["knobs"] = knobs,
            };
            File.WriteAllText(ConfigPath, node.ToJsonString() + "\n");
        }
        catch { /* best effort */ }
    }

    private static void Apply()
    {
        Environment.SetEnvironmentVariable("SANITIZE_NER", NerEnabled ? "1" : "0");
        // Toggle = clear/restore the analyzer URL in THIS process; Presidio.Enabled reads
        // the env live, so flipping the item takes effect on the next request (fail-soft
        // skip while off). The URL itself stays owned by the user's environment (setx);
        // we only remember it for the lifetime of the tray.
        Environment.SetEnvironmentVariable("SANITIZE_PRESIDIO_URL",
            PresidioEnabled ? PresidioUrl : null);
        foreach (var (k, val) in Knobs)
            Environment.SetEnvironmentVariable(k, val);
    }
}

/// <summary>GitHub release check + self-update staging. Update = download the official
/// install.ps1 and run it via a wrapper that stops the proxy task first; the tray exits
/// to release the exe lock and the task registration relaunches the new build.</summary>
internal static class UpdateChecker
{
    private const string ReleasesApi = "https://api.github.com/repos/saasclawai-org/ClaimCheck/releases/latest";
    private const string InstallerUrl = "https://raw.githubusercontent.com/saasclawai-org/ClaimCheck/main/tools/install.ps1";

    public static string? Latest { get; private set; }
    public static DateTime? LastCheck { get; private set; }
    public static volatile bool Checking;

    public static async Task CheckAsync()
    {
        if (Checking) return;
        Checking = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaimCheck-Tray");
            using var resp = await http.GetAsync(ReleasesApi);
            if (!resp.IsSuccessStatusCode) return;
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            var tag = node?["tag_name"]?.GetValue<string>()?.TrimStart('v');
            if (tag is null) return;
            if (Version.TryParse(tag, out var v) && Version.TryParse(ClaimCheck.Status.Version, out var cur) && v > cur)
                Latest = tag;
            else
                Latest = null;
            LastCheck = DateTime.Now;
        }
        catch { /* offline / rate-limited: keep previous state */ }
        finally { Checking = false; }
    }

    /// <summary>Downloads the current installer and writes a wrapper that stops the proxy
    /// task, waits for the tray to exit, then installs with -WithProxy (which re-registers
    /// and starts the task on the new build). Returns the wrapper path.</summary>
    public static string StageUpdate()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claimcheck-update");
        Directory.CreateDirectory(dir);
        var ps1 = System.IO.Path.Combine(dir, "install.ps1");
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            var txt = http.GetStringAsync(InstallerUrl).GetAwaiter().GetResult();
            File.WriteAllText(ps1, txt);
        }
        var wrapper = System.IO.Path.Combine(dir, "apply-update.ps1");
        var installDir = AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        File.WriteAllText(wrapper,
            "Stop-ScheduledTask -TaskName 'ClaimCheckProxy' -ErrorAction SilentlyContinue\n" +
            "Start-Sleep -Seconds 3\n" +
            $"& '{ps1}' -InstallDir '{installDir}' -WithProxy\n");
        return wrapper;
    }
}
