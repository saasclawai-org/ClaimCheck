using System.Text.Json;

namespace ClaimCheck.Dashboard;

/// <summary>One proxy lane as configured in dashboard-lanes.json:
/// { "name": "anthropic-prod", "port": 8801, "audit": "/home/norman/claimcheck/audit/anthropic-prod.jsonl", "note": "..." }
/// port = lane's localhost /status+/health port; audit = SANITIZE_PROXY_LOG JSONL path (null → status-only lane).</summary>
public sealed record LaneConfig(string Name, int? Port, string? Audit, string? Note);

/// <summary>Loads + watches the lane registry file. Adding a lane = one JSON entry, zero code.
/// Lane names must be URL-safe (used in paths): letters, digits, dot, dash, underscore.</summary>
public sealed class LaneRegistry
{
    private static readonly System.Text.RegularExpressions.Regex NameRx =
        new("^[A-Za-z0-9._-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly string _path;
    private List<LaneConfig> _lanes = new();
    private DateTime _mtime = DateTime.MinValue;
    private readonly object _lock = new();

    public LaneRegistry(string path) { _path = path; Reload(force: true); }

    public string Path => _path;

    public List<LaneConfig> Lanes { get { lock (_lock) return _lanes; } }

    public LaneConfig? Find(string name)
    { lock (_lock) return _lanes.FirstOrDefault(l => l.Name.Equals(name, StringComparison.Ordinal)); }

    /// <summary>Re-read if the file changed on disk. Cheap (stat only) — called every ingest tick,
    /// so adding a lane never needs a dashboard restart.</summary>
    public void ReloadIfChanged()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.LastWriteTimeUtc == _mtime) return;
            Reload();
        }
        catch { /* registry file hiccup — keep serving the last good set */ }
    }

    private void Reload(bool force = false)
    {
        var list = new List<LaneConfig>();
        try
        {
            if (File.Exists(_path))
            {
                var opts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                };
                var raw = JsonSerializer.Deserialize<List<LaneConfig>>(File.ReadAllText(_path), opts) ?? new();
                foreach (var l in raw)
                {
                    if (string.IsNullOrWhiteSpace(l.Name)) continue;
                    if (!NameRx.IsMatch(l.Name))
                    {
                        Console.Error.WriteLine($"dashboard: lane name '{l.Name}' rejected (URL-unsafe; use [A-Za-z0-9._-])");
                        continue;
                    }
                    list.Add(l with { Name = l.Name });
                }
            }
            var fi = new FileInfo(_path);
            _mtime = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"dashboard: failed to read lane registry {_path}: {e.Message}");
            if (!force) return; // keep last good set
        }
        lock (_lock) _lanes = list;
    }
}
