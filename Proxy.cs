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

    // v0.9.6 multitenant lanes (SANITIZE_TENANT_HEADER, e.g. "X-ClaimCheck-Tenant"):
    // every chat-completions request carries a tenant id; each tenant gets its own vault
    // (tenant-<sha1-12>.json) and responses are NEVER restored — tickets stay tickets, so
    // tenant PII never reassembles server-side. Missing/invalid header fails closed (400).
    private static string? _tenantHeader;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, VaultData> _tenantVaults = new();
    private static HttpClient _http = null!;
    private static string _upstream = "";
    private static long _totalRequests;
    private static long _scrubbedRequests;
    private static DateTime? _lastRequestUtc;

    // Upstream URL path prefix (e.g. "api/coding/paas/v4"); used to de-duplicate client paths
    // whose base URL already carries it (OpenClaw builds differ across versions).
    private static string? _upstreamPrefix;
    private static List<(string Type, Regex Rx, Func<string, bool>?)> _patterns = new();

    // v0.9.7 fail-closed pass-through: body-carrying methods (POST/PUT/PATCH) to any path
    // other than the scrubbed chat/completions endpoint are REFUSED (400) by default — a
    // client pointed at the proxy can never silently leak raw content through an endpoint
    // the proxy doesn't understand (the /v1/responses field report: requests crossed
    // unsanitized while the caller believed they were protected). SANITIZE_PASSTHROUGH
    // re-opens lanes deliberately: comma-separated case-insensitive path SUFFIXES
    // (e.g. "v1/embeddings,v1/models") or "*" to opt everything back in (legacy behavior).
    // GET/HEAD/DELETE and the /health + /status endpoints always pass — they carry no
    // scrub-able content.
    private static readonly string[] PassthroughAllowlist =
        (Environment.GetEnvironmentVariable("SANITIZE_PASSTHROUGH") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static readonly bool PassthroughAll = PassthroughAllowlist.Contains("*");
    private static readonly string[] BodyMethods = { "POST", "PUT", "PATCH" };

    // v0.9.0 audit log — SANITIZE_PROXY_LOG: "1" keeps the legacy stderr request
    // summary; any other non-empty value is treated as a file path and every
    // forwarded request body (post-scrub — tickets only, never values) is
    // appended there as one JSON line. Grep-able answer to "what did the model see?".
    private static readonly string? AuditPath = ResolveAuditPath();
    private static readonly object AuditLock = new();

    // v0.9.12 output policy — SANITIZE_SUPPRESS_CODE=1 masks fenced code blocks in
    // assistant content (support lanes: the bot stays in its lane); coding lanes
    // leave it off (default). SANITIZE_SUPPRESS_CODE_MESSAGE overrides the
    // replacement text shown in place of the masked block.
    public static bool SuppressCode { get; } = Environment.GetEnvironmentVariable("SANITIZE_SUPPRESS_CODE") == "1";
    public static string SuppressCodeMessage { get; } =
        Environment.GetEnvironmentVariable("SANITIZE_SUPPRESS_CODE_MESSAGE") is { } scm && scm.Length > 0
            ? scm
            : "[code removed — this support channel doesn't generate code. For technical implementations, please contact our team.]";

    /// <summary>Per-stream fence state for streaming suppression: ``` blocks are
    /// line-anchored, so content is buffered per line and fence state persists
    /// across chunk boundaries (fences split mid-chunk are handled by holding the
    /// partial line).</summary>
    private sealed class CodeSuppressState { public bool InFence; public string LineBuf = ""; }

    private static string ApplyCodeSuppress(string text, CodeSuppressState st, bool flush)
    {
        if (!SuppressCode) return text;
        st.LineBuf += text;
        var outBuf = new StringBuilder();
        int idx;
        while ((idx = st.LineBuf.IndexOf('\n')) >= 0)
        {
            var line = st.LineBuf[..idx];
            st.LineBuf = st.LineBuf[(idx + 1)..];
            EmitSuppressLine(line, st, outBuf);
        }
        if (flush && st.LineBuf.Length > 0)
        {
            EmitSuppressLine(st.LineBuf, st, outBuf);
            st.LineBuf = "";
        }
        return outBuf.ToString();
    }

    private static void EmitSuppressLine(string line, CodeSuppressState st, StringBuilder outBuf)
    {
        if (!st.InFence)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
            {
                st.InFence = true;
                var prefix = line[..line.IndexOf(trimmed, StringComparison.Ordinal)];
                if (prefix.Length > 0) outBuf.Append(prefix);
                outBuf.Append(SuppressCodeMessage);
            }
            else outBuf.Append(line).Append('\n');
        }
        else
        {
            var close = line.IndexOf("```", StringComparison.Ordinal);
            if (close >= 0)
            {
                st.InFence = false;
                var trail = line[(close + 3)..];
                if (!string.IsNullOrWhiteSpace(trail)) outBuf.Append(trail).Append('\n');
            }
        }
    }

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
        WebApplication app;
        try { app = await StartAsync(args); }
        catch (VaultCryptoException e)
        {
            // unreadable vault at startup — clean fatal for the console/service path;
            // the tray catches StartAsync throws itself and shows the message in its tooltip
            Console.Error.WriteLine($"claimcheck-proxy: FATAL — {e.Message}");
            return 1;
        }
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
            // v0.9.0: read via VaultCrypto like every other vault reader (external audit
            // 2026-10-30 finding). The proxy SAVES encrypted (DPAPI on Windows default,
            // password mode with SANITIZE_VAULT_PASSWORD) but startup read with raw
            // File.ReadAllText — fine on plaintext Linux, yet on Windows every restart
            // threw in the catch, silently started EMPTY, and the next scrubbed request's
            // SaveVault overwrote the real mapping (the exact path VaultCryptoException's
            // contract forbids). Unreadable vault of any kind => refuse to start; unlike
            // LoadVault's plaintext-corruption legacy fallback, malformed plaintext is
            // fatal here too because the proxy re-saves on every scrub and would destroy
            // whatever is left in the file.
            if (!VaultCrypto.TryReadJson(_vaultFile.FullName, out var vjson, out var verr))
                throw new VaultCryptoException(
                    $"proxy vault {_vaultFile.FullName} cannot be read ({verr}). " +
                    "Refusing to start with an empty vault — the next scrubbed request would overwrite the real mappings. " +
                    $"Set {VaultCrypto.PasswordEnvVar} correctly, decrypt on the machine/user that encrypted it, or move the file aside.");
            try { _vault = JsonSerializer.Deserialize<VaultData>(vjson) ?? new VaultData(); }
            catch (JsonException e)
            {
                throw new VaultCryptoException(
                    $"proxy vault {_vaultFile.FullName} is not valid vault JSON ({e.Message}). " +
                    "Refusing to start with an empty vault — the next scrubbed request would overwrite the real mappings. " +
                    "Move the file aside if truly abandoned.");
            }
        }
        _patterns = Sanitizer.BuiltIn();

        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        { Timeout = Timeout.InfiniteTimeSpan };

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // stdio/stream discipline: nothing writes to stdout
        builder.WebHost.UseUrls($"http://{host}:{port}");
        var app = builder.Build();

        app.Map("/health", () => Results.Text("ok"));
        app.Map("/status", () =>
        {
            CustomClients.Load();
            return Results.Json(new
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
            suppressCode = SuppressCode,
            tenantHeader = _tenantHeader ?? "",
            passthroughAllowlist = PassthroughAll ? "*" : string.Join(",", PassthroughAllowlist),
            exemptTypes = string.Join(",", Sanitizer.ExemptTypes),
            nerFailed = Ner.Failed,
            nerFailReason = Ner.FailReason,
            nerProvider = Ner.Provider,
            nerTypes = string.Join("/", Ner.Types),
            presidio = Presidio.Enabled ? ("on -> " + Presidio.Url) : "off",
            presidioFailures = Presidio.ConsecutiveFailures,
            presidioLastFailure = Presidio.LastFailure,
            startedUtc = StartedUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
            customClients = CustomClients.Count,
            clientFeins = CustomClients.FeinCount,
            });
        });
        app.Map("/{**path}", async (HttpContext ctx, string? path) =>
        {
            path ??= "";
            if (HttpMethods.IsPost(ctx.Request.Method) && path.EndsWith("chat/completions"))
                await HandleChat(ctx, path);
            else if (HttpMethods.IsPost(ctx.Request.Method) && path.EndsWith("responses"))
                await HandleResponses(ctx, path);
            else if (BodyMethods.Contains(ctx.Request.Method, StringComparer.OrdinalIgnoreCase)
                     && !PassthroughAll
                     && !PassthroughAllowlist.Any(suffix =>
                         path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                // Fail closed (v0.9.7): refuse body-carrying requests to endpoints the
                // proxy cannot scrub, rather than forwarding raw content invisibly.
                ctx.Response.StatusCode = 400;
                LogReq(ctx, path, 400, 0);
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync(
                    $"claimcheck: {ctx.Request.Method} /{path} is not a scrubbed endpoint — refusing to forward unsanitized content. " +
                    "Only */chat/completions and */responses are sanitized. Send this traffic directly to the upstream, or add the path to SANITIZE_PASSTHROUGH (comma-separated path suffixes, or * for all) to opt in knowingly.");
            }
            else
                await PassThrough(ctx, path);
        });

        _tenantHeader = Environment.GetEnvironmentVariable("SANITIZE_TENANT_HEADER");
        Console.Error.WriteLine(_tenantHeader is null
            ? $"sanitize proxy: http://{host}:{port} -> {_upstream} (scrub outbound, restore inbound; vault: {_vaultFile.FullName})"
            : $"sanitize proxy: http://{host}:{port} -> {_upstream} (MULTITENANT: tenant header '{_tenantHeader}', per-tenant vaults, restore DISABLED)");
        Presidio.WarmUp();
        await app.StartAsync();
        return app;
    }

    // OpenAI deprecated `max_tokens` and new models (gpt-5.6+/6.x, o-series) HARD-REJECT it
    // (400 unsupported_parameter: "use max_completion_tokens instead") while clients like
    // Cline's OpenAI-compatible provider still send max_tokens. The proxy rebuilds the body
    // anyway, so normalize for OpenAI-family upstreams (anything with "openai" in the host —
    // api.openai.com, *.openai.azure.com). SANITIZE_OPENAI_MAXTOKENS=1 forces the rename on
    // other upstream shapes; =0 disables even auto-detection.
    private static bool IsOpenAiUpstream()
    {
        var env = Environment.GetEnvironmentVariable("SANITIZE_OPENAI_MAXTOKENS");
        if (env == "1") return true;
        if (env == "0") return false;
        return Uri.TryCreate(_upstream, UriKind.Absolute, out var u) &&
               u.Host.Contains("openai", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyOpenAiCompat(JsonNode? root)
    {
        if (root is not JsonObject obj || obj["max_tokens"] is null) return;
        if (!IsOpenAiUpstream()) return;
        if (obj["max_completion_tokens"] is null)
            obj["max_completion_tokens"] = obj["max_tokens"]!.DeepClone();
        obj.Remove("max_tokens");
    }

    // v0.9.19: same story for the Responses API — it uses max_output_tokens; clients
    // translating from the chat shape may send max_tokens, which api.openai.com
    // hard-rejects on 6.x. Same upstream detection as the chat shim.
    private static void ApplyResponsesCompat(JsonNode? root)
    {
        if (root is not JsonObject obj || obj["max_tokens"] is null) return;
        if (!IsOpenAiUpstream()) return;
        if (obj["max_output_tokens"] is null)
            obj["max_output_tokens"] = obj["max_tokens"]!.DeepClone();
        obj.Remove("max_tokens");
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

        VaultData vault = _vault;
        FileInfo vaultFile = _vaultFile;
        if (_tenantHeader is not null)
        {
            // tenant resolution happens BEFORE the fail-closed scrub gate: a missing/invalid
            // tenant id is a client error (400), not a scrub failure (503). An unreadable
            // encrypted tenant vault still throws -> 503, per the never-scrub-on-empty-vault rule.
            (vault, vaultFile) = ResolveTenant(ctx, path, sw);
            if (vault is null)
            {
                await ctx.Response.WriteAsync((string?)ctx.Items["ccError"] ?? "claimcheck: invalid tenant");
                return;
            }
        }

        try
        {
            if (root is not null)
            {
                ScrubMessages(root, vault);
                Interlocked.Increment(ref _scrubbedRequests);
                Sanitizer.SaveVault(vaultFile, vault);
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
        ApplyOpenAiCompat(root);
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

        if (_tenantHeader is not null)
        {
            // multitenant no-restore lane: responses cross back untouched — tickets stay
            // tickets; no tenant's PII ever reassembles on this server.
            if (media.Contains("event-stream"))
                await (await resp.Content.ReadAsStreamAsync(ctx.RequestAborted))
                    .CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            else
            {
                var passthrough = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
                await ctx.Response.WriteAsync(passthrough, ctx.RequestAborted);
            }
            return;
        }
        if (media.Contains("event-stream"))
            await RestoreSse(await resp.Content.ReadAsStreamAsync(ctx.RequestAborted), ctx.Response, ctx.RequestAborted, _vault);
        else
        {
            var text = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
            var restored = RestoreText(text, _vault);
            if (SuppressCode)
            {
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(restored);
                    if (node?["choices"] is System.Text.Json.Nodes.JsonArray choices)
                        for (var i = 0; i < choices.Count; i++)
                        {
                            var msg = choices[i]?["message"];
                            if (msg?["content"] is System.Text.Json.Nodes.JsonValue cv &&
                                cv.TryGetValue<string>(out var ctext))
                                msg["content"] = ApplyCodeSuppress(ctext, new CodeSuppressState(), flush: true);
                        }
                    restored = node.ToJsonString();
                }
                catch { /* suppression is best-effort */ }
            }
            await ctx.Response.WriteAsync(restored, ctx.RequestAborted);
        }
    }

    // ===== v0.9.19: OpenAI Responses API (/v1/responses) =====
    // GPT-6.x only accept function tools on /v1/responses (chat/completions hard-rejects
    // tools on 6.x — verified live 2026-11-09). Same contract as HandleChat: scrub the
    // outbound text fields, restore tickets inbound, fail closed on scrub errors;
    // multitenant lanes pass responses back unrestored.
    private static async Task HandleResponses(HttpContext ctx, string path)
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

        VaultData vault = _vault;
        FileInfo vaultFile = _vaultFile;
        if (_tenantHeader is not null)
        {
            (vault, vaultFile) = ResolveTenant(ctx, path, sw);
            if (vault is null)
            {
                await ctx.Response.WriteAsync((string?)ctx.Items["ccError"] ?? "claimcheck: invalid tenant");
                return;
            }
        }

        try
        {
            if (root is not null)
            {
                ScrubResponses(root, vault);
                Interlocked.Increment(ref _scrubbedRequests);
                Sanitizer.SaveVault(vaultFile, vault);
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
        var upPath = new Uri(_upstream).AbsolutePath.TrimEnd('/');
        if (upPath.Length > 0 && rest.StartsWith("v1/"))
            rest = rest[3..];
        rest = StripUpstreamPrefix(rest);
        using var req = new HttpRequestMessage(HttpMethod.Post, _upstream + rest.TrimStart('/'));
        if (ctx.Request.Headers.TryGetValue("Authorization", out var auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth.ToArray());
        if (ctx.Request.Headers.TryGetValue("Accept", out var accept))
            req.Headers.TryAddWithoutValidation("Accept", accept.ToArray());
        ApplyResponsesCompat(root);
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

        if (_tenantHeader is not null)
        {
            // multitenant no-restore lane: responses cross back untouched — tickets stay
            // tickets; no tenant's PII ever reassembles on this server.
            if (media.Contains("event-stream"))
                await (await resp.Content.ReadAsStreamAsync(ctx.RequestAborted))
                    .CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            else
                await ctx.Response.WriteAsync(await resp.Content.ReadAsStringAsync(ctx.RequestAborted), ctx.RequestAborted);
            return;
        }
        if (media.Contains("event-stream"))
            await RestoreResponsesSse(await resp.Content.ReadAsStreamAsync(ctx.RequestAborted), ctx.Response, ctx.RequestAborted, _vault);
        else
        {
            // JSON-safe restore: rewrite only the response object's text fields (a vault
            // value containing quotes would corrupt whole-serialized-text replacement).
            var text = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
            JsonNode? node = null;
            try { node = JsonNode.Parse(text); }
            catch { }
            if (node is null)
            {
                await ctx.Response.WriteAsync(RestoreText(text, _vault), ctx.RequestAborted);
                return;
            }
            RestoreResponsesObject(node, _vault);
            await ctx.Response.WriteAsync(node.ToJsonString(), ctx.RequestAborted);
        }
    }

    // -------- /v1/responses outbound scrub --------
    // Text lives in `instructions`, `input` (a string, or an item array: message content
    // text, function_call arguments, function_call_output output) — nowhere else. Tool
    // DEFINITIONS stay dev-authored (same policy as the chat path); reasoning items are
    // opaque/encrypted and must never be touched.
    private static int ScrubResponses(JsonNode root, VaultData vault)
    {
        var count = 0;
        if (root["instructions"] is JsonValue iv && iv.TryGetValue<string>(out var ins))
            count += ScrubResponsesField(root, "instructions", ins, vault);
        if (root["input"] is JsonValue sv && sv.TryGetValue<string>(out var s))
            count += ScrubResponsesField(root, "input", s, vault);
        else if (root["input"] is JsonArray items)
            foreach (var item in items)
                count += ScrubResponsesItem(item, vault);
        return count;
    }

    private static int ScrubResponsesField(JsonNode parent, string field, string text, VaultData vault)
    {
        var (scrubbed, n) = ScrubOne(text, vault);
        if (n > 0) parent[field] = scrubbed;
        return n;
    }

    private static int ScrubResponsesItem(JsonNode? item, VaultData vault)
    {
        if (item is not JsonObject obj) return 0;
        var type = obj["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) && t.Length > 0 ? t : "message";
        switch (type)
        {
            case "message":
            {
                var count = 0;
                if (obj["content"] is JsonValue cv && cv.TryGetValue<string>(out var ctext))
                    count += ScrubResponsesField(obj, "content", ctext, vault);
                else if (obj["content"] is JsonArray parts)
                    foreach (var part in parts)
                        if (part?["text"] is JsonValue tv2 && tv2.TryGetValue<string>(out var txt))
                            count += ScrubResponsesField(part!, "text", txt, vault);
                return count;
            }
            case "function_call":
                return obj["arguments"] is JsonValue av && av.TryGetValue<string>(out var args)
                    ? ScrubResponsesField(obj, "arguments", args, vault)
                    : 0;
            case "function_call_output":
            {
                if (obj["output"] is JsonValue ov && ov.TryGetValue<string>(out var output))
                    return ScrubResponsesField(obj, "output", output, vault);
                if (obj["output"] is JsonArray oparts)
                {
                    var n = 0;
                    foreach (var part in oparts)
                        if (part?["text"] is JsonValue tv3 && tv3.TryGetValue<string>(out var txt3))
                            n += ScrubResponsesField(part!, "text", txt3, vault);
                    return n;
                }
                return 0;
            }
            default:
                return 0; // reasoning + unknown item types: untouched
        }
    }

    // -------- /v1/responses inbound restore --------

    // JSON-safe restore of a complete Responses API object (non-streaming body or the
    // embedded "response" of response.completed): only text-bearing fields are rewritten,
    // so vault values containing quotes/newlines re-serialize cleanly.
    private static void RestoreResponsesObject(JsonNode node, VaultData vault)
    {
        if (node["instructions"] is JsonValue iv && iv.TryGetValue<string>(out var ins))
            node["instructions"] = RestoreText(ins, vault);
        if (node["output"] is JsonArray items)
            foreach (var item in items)
                RestoreResponsesItem(item, vault);
    }

    private static void RestoreResponsesItem(JsonNode? item, VaultData vault)
    {
        if (item is not JsonObject obj) return;
        var type = obj["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "message";
        if (type == "message")
        {
            if (obj["content"] is JsonArray parts)
                foreach (var part in parts)
                    if (part?["text"] is JsonValue pv && pv.TryGetValue<string>(out var txt))
                        part["text"] = RestoreText(txt, vault);
        }
        else if (type == "function_call")
        {
            if (obj["arguments"] is JsonValue av && av.TryGetValue<string>(out var args))
                obj["arguments"] = RestoreText(args, vault);
        }
    }

    // Streaming restore for the Responses API event stream. Text rides `delta` fields
    // (output_text / function_call_arguments / reasoning summaries) — same carry +
    // partial-placeholder hold as the chat path, keyed per item_id and kind. `*.done`
    // events and response.completed carry complete text, restored in place. Code
    // suppression is not applied here (coding lanes keep it off).
    private static async Task RestoreResponsesSse(Stream upstream, HttpResponse response, CancellationToken ct, VaultData vault)
    {
        response.ContentType = "text/event-stream";
        await response.StartAsync(ct);
        using var reader = new StreamReader(upstream);
        var carries = new Dictionary<string, (StringBuilder SB, bool IsArgs, string ItemId)>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) { await response.WriteAsync("\n", ct); continue; }
            if (!line.StartsWith("data:"))
            {
                await response.WriteAsync(line + "\n", ct);
                continue;
            }
            var payload = line[5..].TrimStart();
            JsonNode? node;
            try { node = JsonNode.Parse(payload); }
            catch
            {
                // [DONE] or a non-JSON line: flush held tails, then pass through
                await FlushResponsesCarries(carries, response, ct, vault);
                await response.WriteAsync(line + "\n", ct);
                continue;
            }
            if (node is null) { await response.WriteAsync(line + "\n", ct); continue; }

            var etype = node["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "";
            switch (etype)
            {
                case "response.output_text.delta":
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                case "response.function_call_arguments.delta":
                {
                    var itemId = node["item_id"] is JsonValue iv && iv.TryGetValue<string>(out var iid) && iid.Length > 0 ? iid : "top";
                    var isArgs = etype.EndsWith("arguments.delta", StringComparison.Ordinal);
                    var key = itemId + ":" + (isArgs ? 'a' : 't');
                    if (!carries.TryGetValue(key, out var c))
                        carries[key] = c = (new StringBuilder(), isArgs, itemId);
                    if (node["delta"] is JsonValue dv && dv.TryGetValue<string>(out var chunk) && chunk.Length > 0)
                    {
                        c.SB.Append(chunk);
                        node["delta"] = DrainRestored(c.SB, vault);
                    }
                    break;
                }
                case "response.output_text.done":
                    if (node["text"] is JsonValue tx && tx.TryGetValue<string>(out var full))
                        node["text"] = RestoreText(full, vault);
                    break;
                case "response.function_call_arguments.done":
                    if (node["arguments"] is JsonValue av && av.TryGetValue<string>(out var fa))
                        node["arguments"] = RestoreText(fa, vault);
                    break;
                case "response.content_part.done":
                    if (node["part"]?["text"] is JsonValue pv && pv.TryGetValue<string>(out var ptxt))
                        node["part"]!["text"] = RestoreText(ptxt, vault);
                    break;
                case "response.output_item.done":
                    if (node["item"] is JsonNode itemNode)
                        RestoreResponsesItem(itemNode, vault);
                    break;
                case "response.completed":
                    if (node["response"] is JsonNode respNode)
                        RestoreResponsesObject(respNode, vault);
                    break;
            }
            await response.WriteAsync("data: " + node.ToJsonString() + "\n\n", ct);
        }
        await FlushResponsesCarries(carries, response, ct, vault);
    }

    // End-of-stream / [DONE]: emit anything still held. A trailing open-bracket fragment
    // is a literal at this point, not the head of a ticket — hold-back only makes sense
    // mid-stream. Complete tickets inside the tail are restored.
    private static async Task FlushResponsesCarries(
        Dictionary<string, (StringBuilder SB, bool IsArgs, string ItemId)> carries,
        HttpResponse response, CancellationToken ct, VaultData vault)
    {
        foreach (var kv in carries.ToArray())
        {
            var text = RestoreText(kv.Value.SB.ToString(), vault);
            carries.Remove(kv.Key);
            if (text.Length == 0) continue;
            var ev = new JsonObject
            {
                ["type"] = kv.Value.IsArgs ? "response.function_call_arguments.delta" : "response.output_text.delta",
                ["item_id"] = kv.Value.ItemId,
                ["delta"] = text,
            };
            await response.WriteAsync("data: " + ev.ToJsonString() + "\n\n", ct);
        }
    }

    /// <summary>Multitenant lane: read + validate the tenant header, route to that tenant's
    /// vault (cached, file-backed). Returns (null, null) after writing a 400 when the
    /// tenant id is missing/invalid — an unattributed request has no vault, and guessing
    /// one would risk cross-tenant leaks. A vault file that exists but cannot be read
    /// throws — the caller's fail-closed handler turns that into 503 rather than ever
    /// scrubbing with an empty vault.</summary>
    private static (VaultData? Vault, FileInfo? File) ResolveTenant(HttpContext ctx, string path,
        System.Diagnostics.Stopwatch sw)
    {
        if (!ctx.Request.Headers.TryGetValue(_tenantHeader!, out var tv) || string.IsNullOrWhiteSpace(tv))
        {
            ctx.Response.StatusCode = 400;
            LogReq(ctx, path, 400, sw.ElapsedMilliseconds);
            ctx.Items["ccError"] = $"claimcheck: missing {_tenantHeader} header (multitenant lane)";
            return (null, null);
        }
        var tid = tv.ToString().Trim();
        if (tid.Length is < 1 or > 64 || tid.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_')))
        {
            ctx.Response.StatusCode = 400;
            LogReq(ctx, path, 400, sw.ElapsedMilliseconds);
            ctx.Items["ccError"] = "claimcheck: invalid tenant id (A-Z a-z 0-9 - _ only, <= 64 chars)";
            return (null, null);
        }
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(tid))).ToLowerInvariant()[..12];
        var file = new FileInfo(Path.Combine(Sanitizer.VaultDir, $"tenant-{key}.json"));
        var vault = _tenantVaults.GetOrAdd(key, _ => LoadTenantVault(file, tid));
        return (vault, file);
    }

    private static VaultData LoadTenantVault(FileInfo file, string tid)
    {
        Directory.CreateDirectory(Sanitizer.VaultDir);
        if (!file.Exists) return new VaultData();
        if (!VaultCrypto.TryReadJson(file.FullName, out var vjson, out var verr))
            throw new VaultCryptoException(
                $"tenant vault {file.FullName} (tenant {tid[..4]}…) cannot be read ({verr}). Refusing to scrub with an empty vault.");
        try { return JsonSerializer.Deserialize<VaultData>(vjson) ?? new VaultData(); }
        catch (Exception e)
        {
            throw new VaultCryptoException($"tenant vault {file.FullName} is not valid vault JSON ({e.Message}).");
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
        var (cfText, cfFindings) = CustomClients.ScrubFeins(scrubbed, vault);
        if (cfFindings.Count > 0) { scrubbed = cfText; n += cfFindings.Count; }
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
        // v0.9.12: fence-suppression state per content stream (only used when SuppressCode)
        var suppressStates = new Dictionary<string, CodeSuppressState>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) { await response.WriteAsync("\n", ct); continue; }
            if (!line.StartsWith("data:")) { await response.WriteAsync(line + "\n", ct); continue; }

            var payload = line[5..].TrimStart();
            if (payload == "[DONE]")
            {
                if (SuppressCode)
                {
                    foreach (var kv in carries.ToArray())
                    {
                        var flushed = DrainRestored(kv.Value, vault);
                        if (suppressStates.TryGetValue(kv.Key, out var st))
                            flushed = ApplyCodeSuppress(flushed, st, flush: true);
                        if (flushed.Length > 0)
                        {
                            // flushed text rides a proper SSE data event — raw lines break SSE parsers
                            var ev = new JsonObject { ["choices"] = new JsonArray { new JsonObject { ["delta"] = new JsonObject { ["content"] = flushed } } } };
                            await response.WriteAsync("data: " + ev.ToJsonString() + "\n\n", ct);
                        }
                    }
                    carries.Clear();
                    await response.WriteAsync("data: [DONE]\n\n", ct);
                    continue;
                }
                var tail = new StringBuilder();
                foreach (var c in carries.Values) tail.Append(c.ToString());
                carries.Clear();
                var flushedAll = DrainRestored(tail, vault);
                if (flushedAll.Length > 0) await response.WriteAsync(flushedAll, ct);
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
                    if (!suppressStates.TryGetValue("c" + i, out var csState))
                        suppressStates["c" + i] = csState = new CodeSuppressState();
                    RestoreCarried(delta, "content", carries, "c" + i, vault, csState);
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

    private static void RestoreCarried(JsonNode parent, string field, Dictionary<string, StringBuilder> carries, string key, VaultData vault, CodeSuppressState? suppress = null)
    {
        if (parent[field] is not JsonValue fv || !fv.TryGetValue<string>(out var chunk) || chunk.Length == 0) return;
        var sb = carries.TryGetValue(key, out var existing) ? existing : (carries[key] = new StringBuilder());
        sb.Append(chunk);
        var drained = DrainRestored(sb, vault);
        // v0.9.12: content-only fence suppression (reasoning + tool args never suppressed)
        if (suppress is not null) drained = ApplyCodeSuppress(drained, suppress, flush: false);
        // v0.8.4: when the whole chunk is a held-back partial token, the delta must
        // carry nothing — leaving the original chunk in place leaked the raw fragment
        // to the client and double-emitted it once the carry completed.
        parent[field] = drained;
    }
}
