using System.Text;

namespace ClaimCheck.Dashboard;

public sealed record AuditHit(long Offset, string Line, int ByteLen);

/// <summary>Read-only access to lane audit JSONL files. The DB keeps metadata/rollups; the audit
/// browser reads lines straight from the files (single source of truth, no body duplication).
/// All reads use FileShare.ReadWrite so they never disturb the proxy's appends.</summary>
public static class AuditFile
{
    public static bool Exists(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static int ReadSafe(FileStream fs, byte[] buf, int count)
    {
        int got = 0;
        while (got < count)
        {
            var n = fs.Read(buf, got, count - got);
            if (n <= 0) break;
            got += n;
        }
        return got;
    }

    /// <summary>Backward window: up to maxLines complete lines ending just before endOffsetExclusive
    /// (null = end of file). NextBefore = the `before` cursor for the next-older window (null = file start).
    /// A first line cut off by the window edge is excluded; a trailing unterminated line at true EOF is included.</summary>
    public static (List<AuditHit> Hits, long? NextBefore) Tail(string path, long? endOffsetExclusive, int maxLines)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long end = Math.Min(endOffsetExclusive ?? fs.Length, fs.Length);
            if (end <= 0) return (new(), null);
            var chunk = (int)Math.Min(end, 512 * 1024);
            var buf = new byte[chunk];
            fs.Seek(end - chunk, SeekOrigin.Begin);
            var got = ReadSafe(fs, buf, chunk);
            if (got == 0) return (new(), null);
            var chunkStart = end - got;
            var atFileStart = chunkStart == 0;
            var atFileEnd = end >= fs.Length;

            // line spans: each ends right before a '\n'
            var spans = new List<(int Start, int Len)>();
            int s = 0;
            for (var i = 0; i < got; i++)
                if (buf[i] == (byte)'\n') { spans.Add((s, i - s)); s = i + 1; }
            var trailingPartial = s < got;

            var usable = new List<(int Start, int Len)>();
            for (var i = atFileStart ? 0 : 1; i < spans.Count; i++) usable.Add(spans[i]); // drop cut-off first line
            if (trailingPartial && atFileEnd) usable.Add((s, got - s));                    // true final line, no trailing \n

            var kept = usable.Count > maxLines ? usable.GetRange(usable.Count - maxLines, maxLines) : usable;
            var hits = kept.Select(k => new AuditHit(chunkStart + k.Start,
                Encoding.UTF8.GetString(buf, k.Start, k.Len).TrimEnd('\r'), k.Len)).ToList();
            long? nextBefore = hits.Count == 0
                ? (atFileStart ? null : chunkStart)
                : (hits[0].Offset == 0 ? null : hits[0].Offset);
            return (hits, nextBefore);
        }
        catch (IOException) { return (new(), null); }
    }

    /// <summary>Forward scan from `after` collecting up to maxHits matching lines.
    /// Scans at most maxScanBytes per call; NextAfter = resume cursor (null when EOF reached).</summary>
    public static (List<AuditHit> Hits, long? NextAfter, bool Eof) Search(
        string path, long after, Func<string, bool> match, int maxHits, long maxScanBytes = 32 * 1024 * 1024)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (after >= fs.Length) return (new(), null, true);
            fs.Seek(after, SeekOrigin.Begin);
            var hits = new List<AuditHit>();
            var pending = new MemoryStream();
            long pendingBase = after;
            long scanned = 0;
            var buf = new byte[256 * 1024];

            while (true)
            {
                var toRead = (int)Math.Min(buf.Length, fs.Length - fs.Position);
                if (toRead <= 0)
                {
                    var rest = pending.ToArray();
                    if (rest.Length > 0)
                    {
                        var line = Encoding.UTF8.GetString(rest).TrimEnd('\r');
                        if (match(line) && hits.Count < maxHits) hits.Add(new AuditHit(pendingBase, line, rest.Length));
                    }
                    return (hits, null, true);
                }
                var got = ReadSafe(fs, buf, toRead);
                if (got == 0) return (hits, null, true);
                pending.Write(buf, 0, got);
                scanned += got;
                var combined = pending.ToArray();
                var lastNl = Array.LastIndexOf(combined, (byte)'\n');
                if (lastNl < 0)
                {
                    if (scanned >= maxScanBytes) return (hits, pendingBase, false);
                    continue;
                }
                int start = 0;
                for (var i = 0; i <= lastNl; i++)
                {
                    if (combined[i] != (byte)'\n') continue;
                    var len = i - start;
                    var line = Encoding.UTF8.GetString(combined, start, len).TrimEnd('\r');
                    if (match(line) && hits.Count < maxHits) hits.Add(new AuditHit(pendingBase + start, line, len));
                    start = i + 1;
                }
                pending = new MemoryStream();
                pending.Write(combined, lastNl + 1, combined.Length - lastNl - 1);
                pendingBase += lastNl + 1;
                if (hits.Count >= maxHits)
                {
                    var last = hits[^1];
                    return (hits, last.Offset + last.ByteLen + 1, false);
                }
                if (scanned >= maxScanBytes) return (hits, pendingBase, false);
            }
        }
        catch (IOException) { return (new(), null, false); }
    }

    /// <summary>Single line at a byte offset (detail view). Null if the file rotated/shortened under us.</summary>
    public static string? LineAt(string path, long offset, int maxBytes = 4 * 1024 * 1024)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset < 0 || offset >= fs.Length) return null;
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[Math.Min(maxBytes, fs.Length - offset)];
            var got = ReadSafe(fs, buf, buf.Length);
            if (got == 0) return null;
            var nl = Array.IndexOf(buf, (byte)'\n');
            var len = nl >= 0 ? nl : got;
            return Encoding.UTF8.GetString(buf, 0, len).TrimEnd('\r');
        }
        catch { return null; }
    }
}
