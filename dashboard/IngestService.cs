using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClaimCheck.Dashboard;

/// <summary>Audit JSONL line as written by the proxy (v0.9.0 format: {"ts","body"};
/// forward-compatible with the planned {lane,tenant,status,ms,model} extension).</summary>
sealed record AuditLine(
    [property: JsonPropertyName("ts")] string? Ts,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("lane")] string? Lane,
    [property: JsonPropertyName("tenant")] string? Tenant,
    [property: JsonPropertyName("status")] int? Status,
    [property: JsonPropertyName("ms")] int? Ms,
    [property: JsonPropertyName("model")] string? Model);

/// <summary>Background loop: tails each lane's audit JSONL into SQLite (2s cadence, 4MB chunks,
/// line-safe at '\n' boundaries — UTF-8 multibyte never contains 0x0A) and polls lane /status
/// (15s cadence). Purely read-only against the proxy; never touches the scrub path.</summary>
public sealed class IngestService : BackgroundService
{
    private static readonly Regex TicketRx = new(@"\[[A-Z][A-Z_]*_\d+\]", RegexOptions.Compiled);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private const long MaxChunkBytes = 4 * 1024 * 1024;

    private readonly DashboardDb _db;
    private readonly LaneRegistry _registry;
    private DateTime _lastStatusPoll = DateTime.MinValue;

    public IngestService(DashboardDb db, LaneRegistry registry) { _db = db; _registry = registry; }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        Console.Error.WriteLine($"dashboard: ingest running — lanes file {_registry.Path}");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stopping.IsCancellationRequested)
        {
            try { if (!await timer.WaitForNextTickAsync(stopping)) break; }
            catch (OperationCanceledException) { break; }
            try { await Tick(stopping); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Console.Error.WriteLine($"dashboard: tick error: {e.Message}"); }
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        _registry.ReloadIfChanged();
        var lanes = _registry.Lanes;
        if (lanes.Count > 0) _db.EnsureLaneRows(lanes);
        foreach (var lane in lanes)
            if (!string.IsNullOrWhiteSpace(lane.Audit)) TailIngest(lane);
        if ((DateTime.UtcNow - _lastStatusPoll).TotalSeconds >= 15)
        {
            _lastStatusPoll = DateTime.UtcNow;
            foreach (var lane in lanes)
                if (lane.Port is int port)
                    await PollStatus(lane, port, ct);
        }
    }

    private void TailIngest(LaneConfig lane)
    {
        var path = lane.Audit!;
        if (!File.Exists(path)) return;
        var (statePath, offset) = _db.GetIngestState(lane.Name);
        if (!string.Equals(statePath, path, StringComparison.Ordinal)) offset = 0;

        byte[] buf;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = fs.Length;
            if (len < offset) offset = 0; // truncated or rotated → start over
            if (len == offset) return;
            var toRead = (int)Math.Min(len - offset, MaxChunkBytes);
            fs.Seek(offset, SeekOrigin.Begin);
            buf = new byte[toRead];
            int read = 0;
            while (read < toRead)
            {
                var n = fs.Read(buf, read, toRead - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return;
            if (read < buf.Length) buf = buf[..read];
        }
        catch (IOException) { return; } // mid-rotation race — next tick retries

        var lastNl = Array.LastIndexOf(buf, (byte)'\n');
        if (lastNl < 0) return; // no complete line yet

        var newOffset = offset + lastNl + 1;
        var text = Encoding.UTF8.GetString(buf, 0, lastNl);
        var recs = new List<LineRec>();
        long lineStart = offset;
        foreach (var rawLine in text.Split('\n'))
        {
            var trimmed = rawLine.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                var rec = ParseLine(trimmed, lineStart, lane.Name);
                if (rec != null) recs.Add(rec);
            }
            lineStart += Encoding.UTF8.GetByteCount(rawLine) + 1;
        }
        _db.IngestBatch(lane.Name, path, newOffset, recs);
    }

    private LineRec? ParseLine(string line, long offset, string laneName)
    {
        try
        {
            var al = JsonSerializer.Deserialize<AuditLine>(line);
            if (al?.Ts is null || al.Body is null) return null;
            var model = string.IsNullOrEmpty(al.Model) ? ModelFromBody(al.Body) : al.Model;
            var tickets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match m in TicketRx.Matches(al.Body))
            {
                var inner = m.Value[1..^1];                 // SSN_1
                var idx = inner.LastIndexOf('_');
                var type = idx > 0 ? inner[..idx] : inner;  // SSN
                tickets[type] = tickets.GetValueOrDefault(type) + 1;
            }
            return new LineRec(offset, al.Ts, al.Body,
                string.IsNullOrEmpty(al.Model) ? model : al.Model,
                NullIfEmpty(al.Tenant), al.Status, al.Ms, tickets);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"dashboard: [{laneName}] skipped bad audit line @ {offset}: {e.Message}");
            return null;
        }
    }

    private static string? ModelFromBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("model", out var m) &&
                m.ValueKind == JsonValueKind.String)
                return m.GetString();
        }
        catch { /* body is post-scrub JSON but guard anyway */ }
        return null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private async Task PollStatus(LaneConfig lane, int port, CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://127.0.0.1:{port}/status", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            long? N(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            _db.UpsertLaneStatus(lane.Name, S("version"), S("upstream"), S("startedUtc"), json, now, N("vaultEntries"));
        }
        catch (Exception e)
        {
            // lane down or not listening — Overview derives liveness from lanes.last_seen age
            Console.Error.WriteLine($"dashboard: [{lane.Name}] status poll failed: {e.Message}");
        }
    }
}
