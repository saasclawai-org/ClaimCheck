using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaimCheck.Dashboard;

/// <summary>Server-rendered HTML. Read-only console: everything shown is post-scrub (tickets only);
/// no vault value is ever readable here because the app holds no vault keys at all.</summary>
public static class Render
{
    public const string Version = "0.1.0";
    public static bool HtmxAvailable;

    private static readonly Regex TicketRx = new(@"\[[A-Z][A-Z_]*_\d+\]", RegexOptions.Compiled);

    // ---------- primitives ----------

    public static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string Highlight(string? s)
    {
        var e = Esc(s);
        return TicketRx.Replace(e, m => $"<mark>{m.Value}</mark>");
    }

    public static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Human age for ISO-O timestamps and the proxy /status format "yyyy-MM-dd HH:mm:ss 'UTC'".</summary>
    public static string Age(string? ts)
    {
        if (string.IsNullOrEmpty(ts)) return "—";
        DateTime? t = null;
        if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            t = dto.UtcDateTime;
        else if (DateTime.TryParseExact(ts, "yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal, out var dt2))
            t = dt2;
        if (t is null) return Esc(ts);
        var d = DateTime.UtcNow - t.Value;
        if (d.TotalSeconds < 0) d = TimeSpan.Zero;
        if (d.TotalSeconds < 90) return $"{(int)d.TotalSeconds}s ago";
        if (d.TotalMinutes < 90) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 36) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    public static string ShortTs(string? ts)
    {
        if (string.IsNullOrEmpty(ts)) return "—";
        if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.UtcDateTime.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return Esc(ts);
    }

    /// <summary>Simple SVG bar chart, zero JS.</summary>
    public static string Bars(IReadOnlyList<(string Label, long Value)> data, int w = 560, int h = 64,
        string fill = "#5aa9ff")
    {
        if (data.Count == 0) return "<div class=muted>no data yet</div>";
        var max = Math.Max(1, data.Max(d => d.Value));
        var bw = (double)w / data.Count;
        var sb = new StringBuilder();
        sb.Append($"<svg class=bars viewBox=\"0 0 {w} {h + 4}\" preserveAspectRatio=\"none\" role=\"img\">");
        for (var i = 0; i < data.Count; i++)
        {
            var (label, value) = data[i];
            if (value <= 0) continue;
            var bh = Math.Max(1.0, (double)value / max * h);
            var x = i * bw;
            var rw = Math.Max(bw - 1.0, 0.5);
            sb.Append($"<rect x=\"{x:0.##}\" y=\"{h - bh:0.##}\" width=\"{rw:0.##}\" height=\"{bh:0.##}\" fill=\"{fill}\"><title>{Esc(label)}: {N(value)}</title></rect>");
        }
        sb.Append($"<line x1=\"0\" y1=\"{h}\" x2=\"{w}\" y2=\"{h}\" stroke=\"#2a3140\"/>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    // ---------- layout ----------

    public static string Page(string title, string active, string body, bool autoRefresh = false)
    {
        string Item(string href, string label, string key) =>
            $"<a href=\"{href}\"{(active == key ? " class=active" : "")}>{label}</a>";
        var nav = Item("/", "Overview", "overview") + Item("/analytics", "Analytics", "analytics") +
                  Item("/vaults", "Vaults", "vaults");
        var htmx = HtmxAvailable ? "<script src=\"/htmx.min.js\" defer></script>" : "";
        var reload = autoRefresh && !HtmxAvailable
            ? "<script>setTimeout(function(){location.reload()},30000)</script>"
            : "";
        return $"""
<!doctype html>
<html lang=en><head><meta charset=utf-8>
<meta name=viewport content="width=device-width,initial-scale=1">
<meta name=robots content="noindex,nofollow">
<link rel=icon href="data:,">
<title>{Esc(title)} · ClaimCheck Dashboard</title>
<link rel=stylesheet href=/app.css>{htmx}</head>
<body>
<nav><span class=brand>🛡️ ClaimCheck <b>Dashboard</b></span><span class=links>{nav}</span><span class=right>read-only · localhost · v{Version}</span></nav>
<main>{body}</main>
<footer>tickets only — this console never displays vault values · audit data is post-scrub</footer>
{reload}</body></html>
""";
    }

    public static string NotFound(string msg) =>
        Page("Not found", "", $"<div class=\"banner warn\">{Esc(msg)}</div><p><a href=/>← Overview</a>");

    // ---------- posture ----------

    private sealed record Posture(bool Ok, string? Version, string? Upstream, string? Vault, long? VaultEntries,
        long? TotalRequests, long? ScrubbedRequests, string? LastRequestUtc, bool? Ner, bool? NerFailed,
        string? NerFailReason, string? NerProvider, string? NerTypes, string? Presidio, long? PresidioFailures,
        string? PresidioLastFailure, bool? FailClosed, bool? SuppressCode, string? TenantHeader, string? Passthrough,
        string? ExemptTypes, string? StartedUtc, long? CustomClients, long? ClientFeins);

    private static readonly Posture BadPosture = new(false, null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    private static Posture? ParsePosture(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            bool? B(string n) => !r.TryGetProperty(n, out var v) ? null
                : v.ValueKind == JsonValueKind.True ? true
                : v.ValueKind == JsonValueKind.False ? false : null;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? N(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
            return new Posture(true, S("version"), S("upstream"), S("vault"), N("vaultEntries"),
                N("totalRequests"), N("scrubbedRequests"), S("lastRequestUtc"), B("ner"), B("nerFailed"),
                S("nerFailReason"), S("nerProvider"), S("nerTypes"), S("presidio"), N("presidioFailures"),
                S("presidioLastFailure"), B("failClosed"), B("suppressCode"), S("tenantHeader"),
                S("passthroughAllowlist"), S("exemptTypes"), S("startedUtc"), N("customClients"), N("clientFeins"));
        }
        catch { return BadPosture; }
    }

    private static string Badge(string text, string cls) => $"<span class=\"badge {cls}\">{Esc(text)}</span>";

    private static string PostureBadges(LaneRow? lane, LaneConfig cfg)
    {
        var p = ParsePosture(lane?.PostureJson);
        var b = new StringBuilder();
        if (p == null) b.Append(Badge("no status yet", "muted"));
        else if (!p.Ok) b.Append(Badge("bad posture json", "bad"));
        else
        {
            b.Append(p.FailClosed == true ? Badge("fail-closed", "ok")
                : p.FailClosed == false ? Badge("FAIL-OPEN", "bad")
                : Badge("fail-closed ?", "muted"));
            if (p.NerFailed == true) b.Append(Badge("NER FAILED", "bad"));
            else if (p.Ner == true) b.Append(Badge("NER" + (string.IsNullOrEmpty(p.NerProvider) ? "" : " · " + p.NerProvider), "ok"));
            else b.Append(Badge("NER off", "warn"));
            if (p.Presidio != null && p.Presidio.StartsWith("on", StringComparison.Ordinal))
            {
                b.Append(Badge("presidio", "ok"));
                if ((p.PresidioFailures ?? 0) > 0) b.Append(Badge($"presidio fails {p.PresidioFailures}", "bad"));
            }
            else b.Append(Badge("presidio off", "warn"));
            if (p.SuppressCode == true) b.Append(Badge("code-suppress", "ok"));
            if (!string.IsNullOrEmpty(p.TenantHeader)) b.Append(Badge("multitenant", "ok"));
            var pt = p.Passthrough ?? "";
            if (pt == "*") b.Append(Badge("PASSTHROUGH *", "bad"));
            else if (pt.Length > 0) b.Append(Badge("passthrough: " + pt, "warn"));
            else b.Append(Badge("no passthrough", "ok"));
        }
        b.Append(string.IsNullOrWhiteSpace(cfg.Audit) ? Badge("no audit file", "warn") : Badge("audit on", "ok"));
        return b.ToString();
    }

    // ---------- overview ----------

    public static string Overview(DashboardDb db, LaneRegistry registry)
    {
        var cfgs = registry.Lanes;
        string head;
        if (cfgs.Count == 0)
            head = $"<div class=\"banner warn\">No lanes configured — create <code>{Esc(registry.Path)}</code> " +
                   "(see <code>dashboard-lanes.example.json</code>). Picked up automatically, no restart needed.</div>";
        else head = Problems(db, cfgs);
        var grid = HtmxAvailable
            ? $"<div id=grid hx-get=\"/partials/overview\" hx-trigger=\"every 15s\" hx-swap=innerHTML>{Grid(db, cfgs)}</div>"
            : $"<div id=grid>{Grid(db, cfgs)}</div>";
        return Page("Overview", "overview", head + grid, autoRefresh: true);
    }

    public static string OverviewGrid(DashboardDb db, LaneRegistry registry) => Grid(db, registry.Lanes);

    private static string Problems(DashboardDb db, List<LaneConfig> cfgs)
    {
        var lanes = db.GetLanes().ToDictionary(l => l.Name);
        var issues = new List<string>();
        foreach (var cfg in cfgs)
        {
            lanes.TryGetValue(cfg.Name, out var lane);
            var p = ParsePosture(lane?.PostureJson);
            if (cfg.Port != null)
            {
                var alive = lane?.LastSeen != null &&
                            DateTimeOffset.TryParse(lane.LastSeen, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ls) &&
                            (DateTime.UtcNow - ls.UtcDateTime).TotalSeconds < 45;
                if (!alive) issues.Add($"<b>{Esc(cfg.Name)}</b> down / no status");
            }
            if (p is { Ok: true })
            {
                if (p.FailClosed == false) issues.Add($"{Esc(cfg.Name)} is <b>FAIL-OPEN</b>");
                if ((p.PresidioFailures ?? 0) > 0) issues.Add($"{Esc(cfg.Name)}: presidio failures {p.PresidioFailures}");
                if (p.Passthrough == "*") issues.Add($"{Esc(cfg.Name)}: wildcard passthrough");
                if (p.NerFailed == true) issues.Add($"{Esc(cfg.Name)}: NER failed");
            }
        }
        return issues.Count == 0 ? "" : $"<div class=\"banner bad\">⚠ {string.Join(" · ", issues)}</div>";
    }

    private static string Grid(DashboardDb db, List<LaneConfig> cfgs)
    {
        var lanes = db.GetLanes().ToDictionary(l => l.Name);
        var sb = new StringBuilder("<div class=cards>");
        foreach (var cfg in cfgs)
        {
            lanes.TryGetValue(cfg.Name, out var lane);
            sb.Append(Card(cfg, lane, db));
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Card(LaneConfig cfg, LaneRow? lane, DashboardDb db)
    {
        var (reqToday, _, tkToday) = db.Totals(cfg.Name, 1);
        var p = ParsePosture(lane?.PostureJson);
        var dot = "mute";
        if (cfg.Port != null)
        {
            dot = "down";
            if (lane?.LastSeen != null &&
                DateTimeOffset.TryParse(lane.LastSeen, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ls) &&
                (DateTime.UtcNow - ls.UtcDateTime).TotalSeconds < 45)
                dot = "up";
        }
        return $"""
<a class=card href="/lane/{Esc(cfg.Name)}">
  <div class=chead><span class="dot {dot}"></span><b>{Esc(cfg.Name)}</b><span class="muted">{Esc(p?.Version ?? "")}</span></div>
  <div class="muted small">{Esc(p?.Upstream ?? "upstream unknown (no status yet)")}</div>
  <div class=stats>today: {N(reqToday)} req · {N(tkToday)} tickets</div>
  <div class=stats>vault: {(p?.VaultEntries != null ? N(p.VaultEntries.Value) : "—")} · last req: {Age(p?.LastRequestUtc)}</div>
  <div class=badges>{PostureBadges(lane, cfg)}</div>
</a>
""";
    }

    // ---------- lane page ----------

    public static string LanePage(DashboardDb db, LaneRegistry registry, string name, int skip)
    {
        var cfg = registry.Find(name);
        var lane = db.GetLanes().FirstOrDefault(l => l.Name == name);
        if (cfg == null && lane == null) return NotFound($"lane '{name}' is not in the registry");
        var warnings = cfg == null
            ? "<div class=\"banner warn\">Lane removed from registry — showing last known state.</div>"
            : "";

        var sb = new StringBuilder();
        sb.Append(warnings);
        sb.Append($"<h1>{Esc(name)} <span class=\"muted\">v{Esc(lane?.Version ?? "?")}</span></h1>");
        sb.Append($"<p class=muted>{Esc(lane?.Upstream ?? cfg?.Audit ?? "")}");
        if (lane?.LastSeen != null) sb.Append($" · last status {Age(lane.LastSeen)}");
        sb.Append($" · <a href=\"/lane/{Esc(name)}/audit\">audit browser →</a></p>");

        sb.Append("<h2>Posture</h2>");
        sb.Append(lane == null ? "<div class=muted>no status received yet</div>" : PostureTable(lane));

        // 48h request chart
        var hourly = db.Hourly(name, 48).ToDictionary(r => r.Hour, r => r.Requests);
        var buckets = new List<(string, long)>();
        for (var i = 47; i >= 0; i--)
        {
            var hour = DateTime.UtcNow.AddHours(-i).ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture);
            buckets.Add((hour[11..], hourly.GetValueOrDefault(hour)));
        }
        sb.Append($"<h2>Requests — last 48h (UTC)</h2>{Bars(buckets)}");

        // 14d daily table
        var daily = db.Daily(name, 14);
        if (daily.Count > 0)
        {
            sb.Append("<h2>Tickets by day — last 14d</h2>");
            sb.Append(DailyTable(daily));
        }

        // ticket chips
        var types = db.TicketTypes(name);
        if (types.Count > 0)
        {
            sb.Append("<h2>Ticket types (all time)</h2><p>");
            foreach (var t in types.Take(14)) sb.Append($"<span class=chip>{Esc(t.Type)} × {N(t.Count)}</span> ");
            sb.Append("</p>");
        }

        // recent requests
        sb.Append("<h2>Recent requests</h2>");
        var rows = db.RecentRequests(name, 26, Math.Max(0, skip));
        if (rows.Count == 0) sb.Append("<div class=muted>nothing ingested yet</div>");
        else
        {
            sb.Append("<table><tr><th>when</th><th>model</th><th>tenant</th><th>status</th><th>ms</th><th>tickets</th><th>size</th><th></th></tr>");
            foreach (var r in rows) sb.Append(RequestRowHtml(r, name));
            sb.Append("</table>");
            var nav = new StringBuilder("<p class=pager>");
            if (skip > 0) nav.Append($"<a href=\"/lane/{Esc(name)}?skip={Math.Max(0, skip - 25)}\">← newer</a> ");
            if (rows.Count >= 26) nav.Append($"<a href=\"/lane/{Esc(name)}?skip={skip + 25}\">older →</a>");
            nav.Append("</p>");
            sb.Append(nav);
        }
        return Page(name, "", sb.ToString());
    }

    private static string PostureTable(LaneRow lane)
    {
        var p = ParsePosture(lane.PostureJson);
        if (p == null) return "<div class=muted>no status received yet</div>";
        if (!p.Ok) return "<div class=muted>posture json unparseable</div>";
        var presidio = Esc(p.Presidio ?? "—");
        if ((p.PresidioFailures ?? 0) > 0)
            presidio += $" <b>failures: {p.PresidioFailures}</b> (last {Age(p.PresidioLastFailure)})";
        var ner = (p.Ner == true ? "on" : "off");
        if (!string.IsNullOrEmpty(p.NerProvider)) ner += " · " + Esc(p.NerProvider);
        if (!string.IsNullOrEmpty(p.NerTypes)) ner += " · " + Esc(p.NerTypes);
        if (p.NerFailed == true) ner += " <b>FAILED</b>" + (p.NerFailReason != null ? " — " + Esc(p.NerFailReason) : "");
        var rows = new List<(string, string)>
        {
            ("version", Esc(p.Version ?? "—")),
            ("port", lane.Port?.ToString() ?? "—"),
            ("upstream", Esc(p.Upstream ?? "—")),
            ("started", $"{Age(p.StartedUtc)} <span class=muted>{Esc(p.StartedUtc ?? "")}</span>"),
            ("vault file", $"<code>{Esc(p.Vault ?? "—")}</code>"),
            ("vault entries", N(p.VaultEntries ?? 0)),
            ("last request", Age(p.LastRequestUtc)),
            ("requests (process lifetime)", $"{N(p.TotalRequests ?? 0)} total · {N(p.ScrubbedRequests ?? 0)} scrubbed"),
            ("fail closed", p.FailClosed == true ? "yes" : p.FailClosed == false ? "<b>NO — FAIL-OPEN</b>" : "?"),
            ("NER", ner),
            ("presidio", presidio),
            ("code suppress", p.SuppressCode == true ? "on" : "off"),
            ("tenant header", Esc(string.IsNullOrEmpty(p.TenantHeader) ? "—" : p.TenantHeader)),
            ("passthrough", Esc(string.IsNullOrEmpty(p.Passthrough) ? "(none)" : p.Passthrough)),
            ("exempt types", Esc(string.IsNullOrEmpty(p.ExemptTypes) ? "(none)" : p.ExemptTypes)),
            ("custom clients", $"{N(p.CustomClients ?? 0)} · FEINs {N(p.ClientFeins ?? 0)}"),
        };
        var sb = new StringBuilder("<table class=posture>");
        foreach (var (k, v) in rows) sb.Append($"<tr><td class=k>{Esc(k)}</td><td>{v}</td></tr>");
        sb.Append("</table>");
        return sb.ToString();
    }

    private static readonly (string Col, string Head)[] DailyCols =
    {
        ("Requests", "req"), ("TicketsTotal", "tickets"), ("Ssn", "ssn"), ("Fein", "fein"), ("Email", "email"),
        ("Phone", "phone"), ("CreditCard", "cc"), ("CryptoWallet", "crypto"), ("ApiKey", "api-key"),
        ("Person", "person"), ("Org", "org"), ("Other", "other"),
    };

    private static string DailyTable(List<(string Day, HourRollup R)> daily)
    {
        var sb = new StringBuilder("<table class=daily><tr><th>day (UTC)</th>");
        foreach (var (_, head) in DailyCols) sb.Append($"<th>{head}</th>");
        sb.Append("</tr>");
        foreach (var (day, r) in daily)
        {
            sb.Append($"<tr><td>{Esc(day)}</td>");
            foreach (var (col, _) in DailyCols)
            {
                var v = col switch
                {
                    "Requests" => r.Requests, "TicketsTotal" => r.TicketsTotal, "Ssn" => r.Ssn,
                    "Fein" => r.Fein, "Email" => r.Email, "Phone" => r.Phone, "CreditCard" => r.CreditCard,
                    "CryptoWallet" => r.CryptoWallet, "ApiKey" => r.ApiKey, "Person" => r.Person,
                    "Org" => r.Org, _ => r.Other,
                };
                sb.Append($"<td class=num>{(v == 0 ? "<span class=muted>·</span>" : N(v))}</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    private static string RequestRowHtml(RequestRow r, string laneName)
    {
        var status = r.Status == null ? "—" : $"<span class=\"{(r.Status >= 400 ? "st-bad" : "st-ok")}\">{r.Status}</span>";
        return $"<tr><td>{ShortTs(r.Ts)}</td><td>{Esc(r.Model ?? "—")}</td><td>{Esc(r.Tenant ?? "—")}</td>" +
               $"<td>{status}</td><td class=num>{(r.Ms == null ? "—" : r.Ms.ToString())}</td>" +
               $"<td class=num>{(r.TicketCount > 0 ? N(r.TicketCount) : "<span class=muted>·</span>")}</td>" +
               $"<td class=num>{N(r.BodyLen)}b</td>" +
               $"<td><a href=\"/request/{r.Id}\">view</a></td></tr>";
    }

    // ---------- audit browser ----------

    private static readonly string[] QuickTypes =
        { "SSN_", "FEIN_", "EMAIL_", "PHONE_", "CREDIT_CARD_", "CRYPTO_WALLET_", "API_KEY_", "PERSON_", "ORG_ENTITY_" };

    public static string AuditPage(DashboardDb db, LaneRegistry registry, string name, string? q, long? after, long? before)
    {
        var cfg = registry.Find(name);
        if (cfg == null) return NotFound($"lane '{name}' is not in the registry");
        var path = cfg.Audit;
        var sb = new StringBuilder();
        sb.Append($"<h1>Audit — {Esc(name)}</h1>");
        sb.Append($"<p class=muted>post-scrub request bodies, tickets only · file: <code>{Esc(path ?? "(none configured)")}</code></p>");

        if (string.IsNullOrWhiteSpace(path))
        {
            sb.Append("<div class=\"banner warn\">This lane has no SANITIZE_PROXY_LOG audit file configured in the lane registry.</div>");
            return Page($"Audit · {name}", "", sb.ToString());
        }
        if (!File.Exists(path))
        {
            sb.Append("<div class=\"banner warn\">Audit file does not exist (yet). Either the lane has seen no traffic since audit was enabled, or the path is wrong.</div>");
            return Page($"Audit · {name}", "", sb.ToString());
        }

        // search form + quick type chips
        var encQ = Esc(q ?? "");
        sb.Append($"<form class=search method=get action=\"/lane/{Esc(name)}/audit\">");
        sb.Append($"<input type=text name=q value=\"{encQ}\" placeholder=\"substring search — e.g. FEIN_, X-ClaimCheck, model name\">");
        sb.Append("<button>Search</button> ");
        if (!string.IsNullOrEmpty(q)) sb.Append($"<a class=muted href=\"/lane/{Esc(name)}/audit\">clear</a>");
        sb.Append("</form><p class=chips>");
        foreach (var t in QuickTypes) sb.Append($"<a class=chip href=\"/lane/{Esc(name)}/audit?q={Uri.EscapeDataString("[" + t)}\">{Esc(t.TrimEnd('_'))}</a> ");
        sb.Append("</p>");

        List<AuditHit> hits;
        string pager;
        if (!string.IsNullOrEmpty(q))
        {
            var (h, nextAfter, eof) = AuditFile.Search(path, after ?? 0,
                line => line.Contains(q, StringComparison.OrdinalIgnoreCase), 25);
            hits = h;
            pager = nextAfter == null
                ? $"<p class=pager>{(hits.Count == 0 ? "no matches" : "end of file")}</p>"
                : $"<p class=pager><a href=\"/lane/{Esc(name)}/audit?q={Uri.EscapeDataString(q)}&after={nextAfter}\">more matches →</a>{(eof ? " (eof)" : "")}</p>";
        }
        else
        {
            var (h, nextBefore) = AuditFile.Tail(path, before, 25);
            hits = h;
            pager = nextBefore == null
                ? "<p class=pager>start of file</p>"
                : $"<p class=pager><a href=\"/lane/{Esc(name)}/audit?before={nextBefore}\">older →</a> <a class=muted href=\"/lane/{Esc(name)}/audit\">newest</a></p>";
        }
        if (hits.Count == 0 && string.IsNullOrEmpty(q)) sb.Append("<div class=muted>file is empty</div>");
        foreach (var hit in hits) sb.Append(HitCard(hit, name));
        sb.Append(pager);
        return Page($"Audit · {name}", "", sb.ToString());
    }

    private static (string? Ts, string? Model, string? Tenant, int? Status, int? Ms, string Body) ParseHit(string line)
    {
        try
        {
            var al = JsonSerializer.Deserialize<AuditLine>(line);
            return (al?.Ts, string.IsNullOrEmpty(al?.Model) ? null : al!.Model, al?.Tenant, al?.Status, al?.Ms,
                string.IsNullOrEmpty(al?.Body) ? line : al!.Body);
        }
        catch { return (null, null, null, null, null, line); }
    }

    private static string HitCard(AuditHit hit, string laneName)
    {
        var (ts, model, tenant, status, ms, body) = ParseHit(hit.Line);
        var ticketCount = TicketRx.Matches(body).Count;
        var meta = new StringBuilder(Esc(ts ?? "?"));
        if (!string.IsNullOrEmpty(model)) meta.Append(" · ").Append(Esc(model));
        if (!string.IsNullOrEmpty(tenant)) meta.Append(" · tenant ").Append(Esc(tenant));
        if (status != null) meta.Append($" · <span class=\"{(status >= 400 ? "st-bad" : "st-ok")}\">{status}</span>");
        if (ms != null) meta.Append($" · {ms}ms");
        meta.Append($" · {ticketCount} ticket{(ticketCount == 1 ? "" : "s")}");
        var preview = body.Length > 160 ? body[..160] + "…" : body;
        return $"""
<div class=req>
  <div class=meta>{meta} · <a href="/lane/{Esc(laneName)}/line?offset={hit.Offset}">view</a></div>
  <details><summary>{Highlight(preview)}</summary><pre class=body>{Highlight(body)}</pre></details>
</div>
""";
    }

    // ---------- request / line detail ----------

    public static string RequestDetail(DashboardDb db, LaneRegistry registry, long id)
    {
        var row = db.GetRequest(id);
        if (row == null) return NotFound($"request {id} not found");
        var tickets = db.TicketsFor(id);
        var line = AuditFile.LineAt(row.SrcFile, row.SrcOffset);
        string? body = null;
        string? ts = null, model = null, tenant = null;
        int? status = row.Status, ms = row.Ms;
        if (line != null)
        {
            (ts, model, tenant, status, ms, body) = ParseHit(line);
        }
        var meta = new List<(string, string)>
        {
            ("lane", Esc(row.Lane)),
            ("when", $"{ShortTs(ts ?? row.Ts)} <span class=muted>({Esc(ts ?? row.Ts)})</span>"),
            ("model", Esc(model ?? row.Model ?? "—")),
            ("tenant", Esc(tenant ?? row.Tenant ?? "—")),
            ("status", status == null ? "—" : Esc(status.ToString()!)),
            ("ms", ms?.ToString() ?? "—"),
            ("tickets", tickets.Count == 0
                ? "<span class=muted>none</span>"
                : string.Join(" ", tickets.Select(t => $"<span class=chip>{Esc(t.Type)} × {N(t.Count)}</span>"))),
            ("body size", $"{N(row.BodyLen)} bytes"),
            ("source", $"<code>{Esc(row.SrcFile)}</code> @ offset {N(row.SrcOffset)}"),
        };
        var title = $"Request {id} · {row.Lane}";
        return Page(title, "", DetailView(title, meta, body));
    }

    public static string LineDetail(DashboardDb db, LaneRegistry registry, string name, long offset)
    {
        var cfg = registry.Find(name);
        var path = cfg?.Audit;
        var line = path == null ? null : AuditFile.LineAt(path, offset);
        if (line == null) return NotFound("line no longer available — file rotated or offset beyond end");
        var (ts, model, tenant, status, ms, body) = ParseHit(line);
        var meta = new List<(string, string)>
        {
            ("lane", Esc(name)),
            ("when", $"{ShortTs(ts)} <span class=muted>({Esc(ts ?? "?")})</span>"),
            ("model", Esc(model ?? "—")),
            ("tenant", Esc(tenant ?? "—")),
            ("status", status?.ToString() ?? "—"),
            ("ms", ms?.ToString() ?? "—"),
            ("source", $"<code>{Esc(path)}</code> @ offset {N(offset)}"),
        };
        var title = $"Line @ {offset} · {name}";
        return Page(title, "", DetailView(title, meta, body));
    }

    private static string DetailView(string title, List<(string, string)> meta, string? body)
    {
        var sb = new StringBuilder($"<h1>{Esc(title)}</h1><table class=posture>");
        foreach (var (k, v) in meta) sb.Append($"<tr><td class=k>{Esc(k)}</td><td>{v}</td></tr>");
        sb.Append("</table><h2>Body (post-scrub — tickets only)</h2>");
        sb.Append(body == null
            ? "<div class=\"banner warn\">Original line no longer available — the audit file rotated since ingest. Metadata above is from the rollup DB.</div>"
            : $"<pre class=body>{Highlight(PrettyBody(body))}</pre>");
        sb.Append("<p><a href=/>← Overview</a></p>");
        return sb.ToString();
    }

    private static string PrettyBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return body; }
    }

    // ---------- analytics ----------

    public static string AnalyticsPage(DashboardDb db, LaneRegistry registry)
    {
        var sb = new StringBuilder();
        var (req1, err1, tk1) = db.Totals(null, 1);
        var (req7, err7, tk7) = db.Totals(null, 7);
        sb.Append("<h1>Analytics</h1>");
        sb.Append("<div class=cards>" +
                  $"<div class=card><div class=chead><b>Today (UTC)</b></div><div class=stats>{N(req1)} requests · {N(tk1)} tickets</div><div class=stats>{N(err1)} errors (≥400)</div></div>" +
                  $"<div class=card><div class=chead><b>Last 7 days</b></div><div class=stats>{N(req7)} requests · {N(tk7)} tickets</div><div class=stats>{N(err7)} errors</div></div>" +
                  "</div>");

        // per-lane totals
        var lanes = db.GetLanes();
        if (lanes.Count > 0)
        {
            sb.Append("<h2>By lane</h2><table><tr><th>lane</th><th>today</th><th>7d</th><th>all time</th><th>tickets (all time)</th></tr>");
            foreach (var l in lanes)
            {
                var (r1, _, t1) = db.Totals(l.Name, 1);
                var (r7, _, _) = db.Totals(l.Name, 7);
                var (rAll, _, tAll) = db.Totals(l.Name, 36500);
                sb.Append($"<tr><td><a href=\"/lane/{Esc(l.Name)}\">{Esc(l.Name)}</a></td><td class=num>{N(r1)}</td>" +
                          $"<td class=num>{N(r7)}</td><td class=num>{N(rAll)}</td><td class=num>{N(tAll)}</td></tr>");
            }
            sb.Append("</table>");
        }

        // ticket types (all time)
        var types = db.TicketTypes(null);
        if (types.Count > 0)
        {
            var max = types.Max(t => t.Count);
            sb.Append("<h2>Ticket types (all time)</h2><table><tr><th>type</th><th>count</th><th></th></tr>");
            foreach (var t in types)
            {
                var pct = (double)t.Count / max * 100;
                sb.Append($"<tr><td>{Esc(t.Type)}</td><td class=num>{N(t.Count)}</td><td class=barcell><div class=bar style=\"width:{pct:0.#}%\"></div></td></tr>");
            }
            sb.Append("</table>");
        }

        var daily = db.Daily(null, 14);
        if (daily.Count > 0) { sb.Append("<h2>By day — last 14d</h2>"); sb.Append(DailyTable(daily)); }

        var models = db.ModelMix(null);
        if (models.Count > 0)
        {
            sb.Append("<h2>Model mix</h2><table><tr><th>model</th><th>requests</th></tr>");
            foreach (var m in models) sb.Append($"<tr><td>{Esc(m.Name)}</td><td class=num>{N(m.Count)}</td></tr>");
            sb.Append("</table>");
        }

        var tenants = db.TopTenants();
        sb.Append("<h2>Top tenants</h2>");
        sb.Append(tenants.Count == 0
            ? "<div class=muted>none — tenant ids arrive only on multitenant lanes with the v0.2 audit extension (or via the tenant header in future formats)</div>"
            : "<table><tr><th>tenant</th><th>requests</th></tr>" +
              string.Join("", tenants.Select(t => $"<tr><td>{Esc(t.Name)}</td><td class=num>{N(t.Count)}</td></tr>")) +
              "</table>");
        return Page("Analytics", "analytics", sb.ToString());
    }

    // ---------- vaults ----------

    public static string VaultsPage(DashboardDb db)
    {
        var lanes = db.GetLanes();
        var sb = new StringBuilder("<h1>Vaults</h1>");
        sb.Append("<p class=muted>Counts only, by design — this console holds no vault keys and never renders values. Restore stays CLI-only.</p>");
        if (lanes.Count == 0) sb.Append("<div class=muted>no lanes registered yet</div>");
        else
        {
            sb.Append("<table><tr><th>lane</th><th>entries</th><th>trend (48 samples)</th><th>custom clients</th><th>FEINs</th><th>vault file</th></tr>");
            foreach (var l in lanes)
            {
                var p = ParsePosture(l.PostureJson);
                var hist = db.VaultHistory(l.Name, 48).Select(h => (h.Ts[11..16], h.Entries)).ToList();
                sb.Append($"<tr><td><a href=\"/lane/{Esc(l.Name)}\">{Esc(l.Name)}</a></td>" +
                          $"<td class=num>{(p?.VaultEntries != null ? N(p.VaultEntries.Value) : "—")}</td>" +
                          $"<td>{Bars(hist, 160, 28, "#7bc47b")}</td>" +
                          $"<td class=num>{(p?.CustomClients != null ? N(p.CustomClients.Value) : "—")}</td>" +
                          $"<td class=num>{(p?.ClientFeins != null ? N(p.ClientFeins.Value) : "—")}</td>" +
                          $"<td><code class=small>{Esc(p?.Vault ?? "—")}</code></td></tr>");
            }
            sb.Append("</table>");
        }
        return Page("Vaults", "vaults", sb.ToString());
    }
}
