using ClaimCheck.Dashboard;

var lanesPath = GetArg(args, "--lanes") ?? Environment.GetEnvironmentVariable("CC_DASHBOARD_LANES")
    ?? Path.Combine(AppContext.BaseDirectory, "dashboard-lanes.json");
var dbPath = GetArg(args, "--db") ?? Environment.GetEnvironmentVariable("CC_DASHBOARD_DB")
    ?? Path.Combine(AppContext.BaseDirectory, "dashboard.db");
var urls = GetArg(args, "--urls") ?? "http://127.0.0.1:8770";

Render.HtmxAvailable = File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "htmx.min.js"));
var db = new DashboardDb(dbPath);
var registry = new LaneRegistry(lanesPath);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.UseUrls(urls);
builder.Services.AddSingleton(db);
builder.Services.AddSingleton(registry);
builder.Services.AddHostedService(sp => new IngestService(
    sp.GetRequiredService<DashboardDb>(), sp.GetRequiredService<LaneRegistry>()));
var app = builder.Build();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Cache-Control"] = "no-store";
    await next();
});
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/", () => Results.Content(Render.Overview(db, registry), "text/html; charset=utf-8"));
app.MapGet("/partials/overview", () => Results.Content(Render.OverviewGrid(db, registry), "text/html; charset=utf-8"));
app.MapGet("/lane/{name}", (string name, int? skip) =>
    Results.Content(Render.LanePage(db, registry, name, skip ?? 0), "text/html; charset=utf-8"));
app.MapGet("/lane/{name}/audit", (string name, string? q, long? after, long? before) =>
    Results.Content(Render.AuditPage(db, registry, name, q, after, before), "text/html; charset=utf-8"));
app.MapGet("/lane/{name}/line", (string name, long offset) =>
    Results.Content(Render.LineDetail(db, registry, name, offset), "text/html; charset=utf-8"));
app.MapGet("/request/{id:long}", (long id) =>
    Results.Content(Render.RequestDetail(db, registry, id), "text/html; charset=utf-8"));
app.MapGet("/analytics", () => Results.Content(Render.AnalyticsPage(db, registry), "text/html; charset=utf-8"));
app.MapGet("/vaults", () => Results.Content(Render.VaultsPage(db), "text/html; charset=utf-8"));

Console.Error.WriteLine($"dashboard: starting on {urls} · lanes={lanesPath} · db={dbPath}");
app.Run();

static string? GetArg(string[] a, string n)
{
    var i = Array.IndexOf(a, n);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}
