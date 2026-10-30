using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaimCheck;

public static class Proxy
{
    private static readonly Regex PlaceholderRx = new(@"\[[A-Z][A-Z_]*_\d+\]");
    // v0.8.9: partial-placeholder hold must also match a BARE '[' — vLLM tokenizers
    // stream placeholders as single characters ([, S, S, N, _, 1, ]) and the old
    // \[[A-Z]...\ requirement let the leading bracket escape the hold, leaking the
    // ticket across fragments (found 2026-10-29 via proliant-vl qwen3-coder lane).
    private static readonly Regex PartialTailRx = new(@"\[[A-Z_0-9]{0,24}$");

    /// <summary>Fail-closed: if the scrub pass throws, hold the request (503) instead of
    /// forwarding unscrubbed. Default on; SANITIZE_FAIL_CLOSED=0 to disable.</summary>
    public static bool FailClosed { get; } = Environment.GetEnvironmentVariable("SANITIZE_FAIL_CLOSED") != "0";

    /// <summary>SANITIZE_PROXY_LOG=1 → one stderr line per request (method, path, status, ms).
    /// Default off — journals stay free of per-request noise unless explicitly enabled.</summary>
    private static readonly bool ReqLog = Environment.GetEnvironmentVariable("SANITIZE_PROXY_LOG") == "1";

    private static VaultData _vault = new();
    private static FileInfo _vaultFile = null!;
    private static HttpClient _http = null!;
    private static string _upstream = "";
    private static long _totalRequests;
    private static long _scrubbedRequests;
    private static DateTime? _lastRequestUtc;

    // Upstream URL path prefix (e.g. "api/coding/paas/v4"); used to de-duplicate client paths
    // whose base URL already carries it (OpenClaw builds differ across versions).
    private static string? _upstreamPrefix;
    private static List<(string Type, Regex Rx, Func<string, bool>?)> _patterns = new();

    // v0.9.0 audit log — SANITIZE_PROXY_LOG: "1" keeps the legacy stderr request
    // summary; any other non-empty value is treated as a file path and every
    // forwarded request body (post-scrub — tickets only, never values) is
    // appended there as one JSON line. Grep-able answer to "what did the model see?".
    private static readonly string? AuditPath = ResolveAuditPath();
    private static readonly object AuditLock = new();

    private static string? ResolveAuditPath()
    {
        var v = Environment.GetEnvironmentVariable("SANITIZE_PROXY_LOG");
        if (string.IsNullOrWhiteSpace(v) || v == "1") return null;
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(v))!); } catch { }
        return v;
    }

    private static void AuditBody(string body)
    {
        if (AuditPath is null) return;
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("O"),
                body,
            });
            lock (AuditLock) File.AppendAllText(AuditPath, line + Environment.NewLine);
        }
        catch { /* auditing must never break the proxy */ }
    }

    // ---- live status snapshot (for the /status endpoint + ClaimCheckTray) ----

    public static int Port { get; private set; }
    public static DateTime StartedUtc { get; private set; }
    public static string Upstream => _upstream;
    public static string VaultPath => _vaultFile?.FullName ?? "";
    public static long TotalRequests => Interlocked.Read(ref _totalRequests);
    public static long ScrubbedRequests => Interlocked.Read(ref _scrubbedRequests);
    public static string? LastRequestUtc => _lastRequestUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    private static string? GetArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? UpstreamPathPrefix(string upstream)
    {
        if (!Uri.TryCreate(upstream, UriKind.Absolute, out var u)) return null;
        var p = u.AbsolutePath.Trim('/');
        return string.IsNullOrEmpty(p) ? null : p;
    }

    /// <summary>If the request path repeats the upstream's own path prefix, strip it once.
    /// OpenClaw builds construct provider base URLs differently across versions (some send the
    /// full provider path, some only /chat/completions on top of a base URL that already carries
    /// it) — this makes both client shapes compose to the same upstream URL.</summary>
    private static string StripUpstreamPrefix(string path)
    {
        if (_upstreamPrefix is null) return path;
        var trimmed = path.TrimStart('/');
        if (!trimmed.StartsWith(_upstreamPrefix, StringComparison.OrdinalIgnoreCase)) return path;
        var rest = trimmed[_upstreamPrefix.Length..];
        if (rest.Length > 0 && rest[0] != '/') return path; // partial segment match — leave untouched
        return "/" + rest.TrimStart('/');
    }

    private static void LogReq(HttpContext ctx, string path, int status, long ms)
    {
        if (ReqLog) Console.Error.WriteLine($"claimcheck-proxy: {ctx.Request.Method} {path} -> {status} ({ms}ms)");
    }

    public static async Task<int> Run(string[] args)
    {
        var app = await StartAsync(args);
        await app.WaitForShutdownAsync();
        return 0;
    }

    /// <summary>Builds and starts the proxy (Kestrel) without blocking. The Windows tray
    /// app hosts it in-process; Proxy.Run wraps this for console use.</summary>
    public static async Task<WebApplication> StartAsync(string[] args)
    {
        _upstream = GetArg(args, "--upstream") ?? throw new ArgumentException("--upstream <base-url> required");
        if (!_upstream.EndsWith('/')) _upstream += '/';
        _upstreamPrefix = UpstreamPathPrefix(_upstream);
        var port = int.TryParse(GetArg(args, "--port"), out var p) ? p : 8766;
        var host = GetArg(args, "--host") ?? "127.0.0.1";
        if (host is not ("127.0.0.1" or "localhost" or "::1" or "[::1]"))
            Console.Error.WriteLine($"sanitize proxy: WARNING — binding {host}:{port} exposes an UNAUTHENTICATED proxy to the network. LAN or VPN only; never the public internet.");
        Port = port;
        StartedUtc = DateTime.UtcNow;

        Directory.CreateDirectory(Sanitizer.VaultDir);
        var vaultOverride = GetArg(args, "--vault");
        if (!string.IsNullOrWhiteSpace(vaultOverride))
        {
            var vPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(vaultOverride));
            Directory.CreateDirectory(Path.GetDirectoryName(vPath)!);
            _vaultFile = new FileInfo(vPath);
        }
        else
        {
            _vaultFile = new FileInfo(Path.Combine(Sanitizer.VaultDir, "proxy.json"));
        }
        if (_vaultFile.Exists)
        {
            try { _vault = JsonSerializer.Deserialize<VaultData>(File.ReadAllText(_vaultFile.FullName)) ?? new VaultData(); }
            catch { _vault = new VaultData(); }
        }
        _patterns = Sanitizer.BuiltIn();

        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        { Timeout = Timeout.InfiniteTimeSpan };

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // stdio/stream discipline: nothing writes to stdout
        builder.WebHost.UseUrls($"http://{host}:{port}");
        var app = builder.Build();

        app.Map("/health", () => Results.Text("ok"));
        app.Map("/status", () => Results.Json(new
        {
            ok = true,
            version = Status.Version,
            port,
            upstream = _upstream,
            vault = _vaultFile.FullName,
            vaultEntries = _vault.Map.Count,
            totalRequests = TotalRequests,
            scrubbedRequests = ScrubbedRequests,
            lastRequestUtc = LastRequestUtc,
            ner = Ner.Enabled,
            failClosed = FailClosed,
            nerFailed = Ner.Failed,
            nerFailReason = Ner.FailReason,
            nerProvider = Ner.Provider,
            presidio = Presidio.Enabled ? ("on -> " + Presidio.Url) : "off",
            presidioFailures = Presidio.ConsecutiveFailures,
            presidioLastFailure = Presidio.LastFailure,
            startedUtc = StartedUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
        }));
        app.Map("/{**path}", async (HttpContext ctx, string? path) =>
        {
            path ??= "";
            if (HttpMethods.IsPost(ctx.Request.Method) && path.EndsWith("chat/completions"))
                await HandleChat(ctx, path);
            else
                await PassThrough(ctx, path);
        });

        Console.Error.WriteLine($"sanitize proxy: http://{host}:{port} -> {_upstream} (scrub outbound, restore inbound; vault: {_vaultFile.FullName})");
        Presidio.WarmUp();
        await app.StartAsync();
        return app;
    }

    private static async Task HandleChat(HttpContext ctx, string path)
    {
        Interlocked.Increment(ref _totalRequests);
        _lastRequestUtc = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch
        {
            ctx.Response.StatusCode = 400;
            LogReq(ctx, path, 400, sw.ElapsedMilliseconds);
            await ctx.Response.WriteAsync("invalid JSON body");
            return;
        }

        try
        {
            if (root is not null)
            {
                ScrubMessages(root, _vault);
                Interlocked.Increment(ref _scrubbedRequests);
                Sanitizer.SaveVault(_vaultFile, _vault);
            }
        }
        catch (Exception e)
        {
            if (!FailClosed) throw;
            ctx.Response.StatusCode = 503;
            LogReq(ctx, path, 503, sw.ElapsedMilliseconds);
            await ctx.Response.WriteAsync("claimcheck: scrub failed (fail-closed): " + e.Message);
            return;
        }

        var rest = path;
        // Strip a client-supplied /v1/ only when the upstream carries its own provider
        // prefix (legacy zai-shape upstreams). Root upstreams (vLLM/OpenAI-style lanes)
        // serve /v1/... themselves — stripping lands on /chat/completions and 404s
        // (2026-10-26 live proxy field report).
        var upPath = new Uri(_upstream).AbsolutePath.TrimEnd('/');
        if (upPath.Length > 0 && rest.StartsWith("v1/"))
            rest = rest[3..];
        rest = StripUpstreamPrefix(rest);
        using var req = new HttpRequestMessage(HttpMethod.Post, _upstream + rest.TrimStart('/'));
        if (ctx.Request.Headers.TryGetValue("Authorization", out var auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth.ToArray());
        if (ctx.Request.Headers.TryGetValue("Accept", out var accept))
            req.Headers.TryAddWithoutValidation("Accept", accept.ToArray());
        var payload = root?.ToJsonString() ?? body;
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        AuditBody(payload);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        }
        catch (Exception e) when (e is HttpRequestException or System.IO.IOException)
        {
            ctx.Response.StatusCode = 502;
            LogReq(ctx, path, 502, sw.ElapsedMilliseconds);
            await ctx.Response.WriteAsync("claimcheck: upstream unreachable: " + e.Message);
            return;
        }
        using var _respScope = resp;
        ctx.Response.StatusCode = (int)resp.StatusCode;
        LogReq(ctx, path, (int)resp.StatusCode, sw.ElapsedMilliseconds);
        var ctype = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        ctx.Response.ContentType = ctype;
        var media = resp.Content.Headers.ContentType?.MediaType ?? "";

        if (media.Contains("event-stream"))
            await RestoreSse(await resp.Content.ReadAsStreamAsync(ctx.RequestAborted), ctx.Response, ctx.RequestAborted, _vault);
        else
        {
            var text = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
            await ctx.Response.WriteAsync(RestoreText(text, _vault), ctx.RequestAborted);
        }
    }

    private static async Task PassThrough(HttpContext ctx, string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fwd = StripUpstreamPrefix(path);
        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), _upstream + fwd.TrimStart('/') + ctx.Request.QueryString);
        foreach (var h in ctx.Request.Headers)
            if (!h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                req.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            req.Content = new StreamContent(ctx.Request.Body);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        }
        catch (Exception e) when (e is HttpRequestException or System.IO.IOException)
        {
            ctx.Response.StatusCode = 502;
            LogReq(ctx, path, 502, sw.ElapsedMilliseconds);
            await ctx.Response.WriteAsync("claimcheck: upstream unreachable: " + e.Message);
            return;
        }
        using var _respScope = resp;
        ctx.Response.StatusCode = (int)resp.StatusCode;
        LogReq(ctx, path, (int)resp.StatusCode, sw.ElapsedMilliseconds);
        if (resp.Content.Headers.ContentType is not null)
            ctx.Response.ContentType = resp.Content.Headers.ContentType.ToString();
        if (resp.Content is not null)
            await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    // -------- outbound scrub --------

    private static (string Text, int Count) ScrubOne(string text, VaultData vault)
    {
        var (withPem, pemCount) = Sanitizer.ScrubPemBlocks(text, vault);
        var (withEncoded, encoded) = Sanitizer.ScrubEncodedBlobs(withPem, vault, _patterns);
        var (scrubbed, n) = ScrubString(withEncoded, vault);
        var (gzText, gzFindings) = Gazetteer.Scrub(scrubbed, vault);
        if (gzFindings.Count > 0) { scrubbed = gzText; n += gzFindings.Count; }
        var (ccText, ccFindings) = CustomClients.ScrubCustom(scrubbed, vault);
        if (ccFindings.Count > 0) { scrubbed = ccText; n += ccFindings.Count; }
        var (entText, entFindings) = EntityRecognizer.Scrub(scrubbed, vault);
        if (entFindings.Count > 0) { scrubbed = entText; n += entFindings.Count; }
        var (preText, preFindings) = Presidio.Scrub(scrubbed, vault);
        if (preFindings.Count > 0) { scrubbed = preText; n += preFindings.Count; }
        if (Ner.Enabled)
        {
            var (nerText, nerFindings) = Ner.Scrub(scrubbed, vault);
            if (nerFindings.Count > 0) { scrubbed = nerText; n += nerFindings.Count; }
        }
        var (kvText, kvCount) = Sanitizer.ScrubKnownValues(scrubbed, vault);
        if (kvCount > 0) { scrubbed = kvText; n += kvCount; }
        if (FakeData.Enabled) scrubbed = FakeData.Substitute(scrubbed, vault);
        return (scrubbed, encoded + pemCount + n);
    }

    private static int ScrubMessages(JsonNode root, VaultData vault)
    {
        var count = 0;
        if (root["messages"] is not JsonArray messages) return 0;
        foreach (var msg in messages)
        {
            if (msg is null) continue;
            if (msg["content"] is JsonValue sv && sv.TryGetValue<string>(out var s))
            {
                var (scrubbed, n) = ScrubOne(s, vault);
                if (n > 0) { msg["content"] = scrubbed; count += n; }
            }
            else if (msg["content"] is JsonArray parts)
                foreach (var part in parts)
                    if (part?["text"] is JsonValue tv && tv.TryGetValue<string>(out var ts))
                    {
                        var (scrubbed, n) = ScrubOne(ts, vault);
                        if (n > 0) { part["text"] = scrubbed; count += n; }
                    }
            if (msg["tool_calls"] is JsonArray tcs)
                foreach (var tc in tcs)
                    if (tc?["function"]?["arguments"] is JsonValue av && av.TryGetValue<string>(out var a))
                    {
                        var (scrubbed, n) = ScrubOne(a, vault);
                        if (n > 0) { tc["function"]["arguments"] = scrubbed; count += n; }
                    }
        }
        return count;
    }

    private static (string Scrubbed, int Count) ScrubString(string text, VaultData vault)
    {
        var result = Sanitizer.SanitizeLines(text.Split('\n'), _patterns, vault);
        return (string.Join('\n', result.Lines), result.Findings.Count);
    }

    // -------- inbound restore --------

    private static string RestoreText(string text, VaultData vault)
    {
        if (!PlaceholderRx.IsMatch(text) && !FakeData.Enabled) return text;
        lock (Sanitizer.VaultLock)
        {
            foreach (var (ph, val) in vault.Map)
                text = text.Replace("[" + ph + "]", val);
        }
        text = FakeData.RestoreFake(text, vault);
        return text;
    }

    private static string DrainRestored(StringBuilder sb, VaultData vault)
    {
        var text = sb.ToString();
        var restored = RestoreText(text, vault);
        var emitLen = restored.Length;
        var bm = PartialTailRx.Match(restored);
        if (bm.Success)
            emitLen = restored.Length - bm.Length;
        else if (FakeData.Enabled)
        {
            var held = FakeData.HeldFakeSuffix(restored, vault);
            if (held is not null)
                emitLen = restored.Length - held.Length;
        }
        sb.Clear();
        if (emitLen < restored.Length) sb.Append(restored[emitLen..]);
        return restored[..emitLen];
    }

    private static async Task RestoreSse(Stream upstream, HttpResponse response, CancellationToken ct, VaultData vault)
    {
        response.ContentType = "text/event-stream";
        await response.StartAsync(ct);
        using var reader = new StreamReader(upstream);
        var carries = new Dictionary<string, StringBuilder>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) { await response.WriteAsync("\n", ct); continue; }
            if (!line.StartsWith("data:")) { await response.WriteAsync(line + "\n", ct); continue; }

            var payload = line[5..].TrimStart();
            if (payload == "[DONE]")
            {
                var tail = new StringBuilder();
                foreach (var c in carries.Values) tail.Append(c.ToString());
                carries.Clear();
                var flushed = DrainRestored(tail, vault);
                if (flushed.Length > 0) await response.WriteAsync(flushed, ct);
                await response.WriteAsync("data: [DONE]\n\n", ct);
                continue;
            }

            JsonNode? node;
            try { node = JsonNode.Parse(payload); }
            catch { await response.WriteAsync(line + "\n", ct); continue; }
            if (node is null) { await response.WriteAsync(line + "\n", ct); continue; }

            if (node["choices"] is JsonArray choices)
                for (var i = 0; i < choices.Count; i++)
                {
                    var delta = choices[i]?["delta"];
                    if (delta is null) continue;
                    RestoreCarried(delta, "content", carries, "c" + i, vault);
                    // Reasoning-capable OpenAI-compatible providers (including Z.ai)
                    // may stream visible/model text through reasoning_content/reasoning.
                    // Restore placeholders there too, preserving split-token handling.
                    RestoreCarried(delta, "reasoning_content", carries, "r" + i, vault);
                    RestoreCarried(delta, "reasoning", carries, "r2" + i, vault);
                    if (delta["tool_calls"] is JsonArray tcs)
                        for (var t = 0; t < tcs.Count; t++)
                            if (tcs[t]?["function"] is not null)
                                RestoreCarried(tcs[t]!["function"]!, "arguments", carries, $"t{i}.{t}", vault);
                }

            await response.WriteAsync("data: " + node.ToJsonString() + "\n\n", ct);
        }
    }

    private static void RestoreCarried(JsonNode parent, string field, Dictionary<string, StringBuilder> carries, string key, VaultData vault)
    {
        if (parent[field] is not JsonValue fv || !fv.TryGetValue<string>(out var chunk) || chunk.Length == 0) return;
        var sb = carries.TryGetValue(key, out var existing) ? existing : (carries[key] = new StringBuilder());
        sb.Append(chunk);
        var drained = DrainRestored(sb, vault);
        // v0.8.4: when the whole chunk is a held-back partial token, the delta must
        // carry nothing — leaving the original chunk in place leaked the raw fragment
        // to the client and double-emitted it once the carry completed.
        parent[field] = drained;
    }
}
