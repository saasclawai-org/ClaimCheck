using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ClaimCheck;

public static class Proxy
{
    private static readonly Regex PlaceholderRx = new(@"\[[A-Z][A-Z_]*_\d+\]");
    private static readonly Regex PartialTailRx = new(@"\[[A-Z][A-Z_0-9]{0,24}$");

    /// <summary>Fail-closed: if the scrub pass throws, hold the request (503) instead of
    /// forwarding unscrubbed. Default on; SANITIZE_FAIL_CLOSED=0 to disable.</summary>
    public static bool FailClosed { get; } = Environment.GetEnvironmentVariable("SANITIZE_FAIL_CLOSED") != "0";

    private static VaultData _vault = new();
    private static FileInfo _vaultFile = null!;
    private static HttpClient _http = null!;
    private static string _upstream = "";
    private static List<(string Type, Regex Rx, Func<string, bool>?)> _patterns = new();

    private static string? GetArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static async Task<int> Run(string[] args)
    {
        _upstream = GetArg(args, "--upstream") ?? throw new ArgumentException("--upstream <base-url> required");
        if (!_upstream.EndsWith('/')) _upstream += '/';
        var port = int.TryParse(GetArg(args, "--port"), out var p) ? p : 8766;

        Directory.CreateDirectory(Sanitizer.VaultDir);
        _vaultFile = new FileInfo(Path.Combine(Sanitizer.VaultDir, "proxy.json"));
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
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        app.Map("/health", () => Results.Text("ok"));
        app.Map("/{**path}", async (HttpContext ctx, string? path) =>
        {
            path ??= "";
            if (HttpMethods.IsPost(ctx.Request.Method) && path.EndsWith("chat/completions"))
                await HandleChat(ctx, path);
            else
                await PassThrough(ctx, path);
        });

        Console.Error.WriteLine($"sanitize proxy: http://127.0.0.1:{port} -> {_upstream} (scrub outbound, restore inbound; vault: {_vaultFile.FullName})");
        await app.RunAsync();
        return 0;
    }

    private static async Task HandleChat(HttpContext ctx, string path)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("invalid JSON body"); return; }

        try
        {
            if (root is not null)
            {
                ScrubMessages(root, _vault);
                Sanitizer.SaveVault(_vaultFile, _vault);
            }
        }
        catch (Exception e)
        {
            if (!FailClosed) throw;
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsync("claimcheck: scrub failed (fail-closed): " + e.Message);
            return;
        }

        var rest = path.StartsWith("v1/") ? path[3..] : path;
        using var req = new HttpRequestMessage(HttpMethod.Post, _upstream + rest);
        if (ctx.Request.Headers.TryGetValue("Authorization", out var auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth.ToArray());
        if (ctx.Request.Headers.TryGetValue("Accept", out var accept))
            req.Headers.TryAddWithoutValidation("Accept", accept.ToArray());
        req.Content = new StringContent(root?.ToJsonString() ?? body, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)resp.StatusCode;
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
        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), _upstream + path + ctx.Request.QueryString);
        foreach (var h in ctx.Request.Headers)
            if (!h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                req.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            req.Content = new StreamContent(ctx.Request.Body);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)resp.StatusCode;
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
        if (Ner.Enabled)
        {
            var (nerText, nerFindings) = Ner.Scrub(scrubbed, vault);
            if (nerFindings.Count > 0) { scrubbed = nerText; n += nerFindings.Count; }
        }
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
        if (!PlaceholderRx.IsMatch(text)) return text;
        foreach (var (ph, val) in vault.Map)
            text = text.Replace("[" + ph + "]", val);
        return text;
    }

    private static string DrainRestored(StringBuilder sb, VaultData vault)
    {
        var text = sb.ToString();
        var restored = RestoreText(text, vault);
        var hold = PartialTailRx.Match(restored);
        var emitLen = hold.Success ? restored.Length - hold.Length : restored.Length;
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
        if (drained.Length > 0) parent[field] = drained;
    }
}
