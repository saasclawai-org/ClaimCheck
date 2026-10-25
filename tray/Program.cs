using System.Drawing;
using System.Drawing.Drawing2D;
using ClaimCheck;
using Microsoft.AspNetCore.Builder;

// ClaimCheckTray — Windows tray host for the ClaimCheck scrub proxy.
// Runs the proxy in-process (no console window), shows a tray icon with live
// status, and exposes which protections are enabled. Exit stops the proxy.

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(args));
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly string[] _args;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private WebApplication? _app;
    private StatusForm? _statusForm;
    private volatile bool _running;
    private int _port = 8766;

    public TrayApp(string[] args)
    {
        _args = args;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) _port = p;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Status…", null, (_, _) => ShowStatus());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart proxy", null, (_, _) => _ = Restart());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitTray());

        _icon = new NotifyIcon
        {
            Icon = Icons.Off,
            Text = "ClaimCheck: starting…",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowStatus();

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += async (_, _) => await Poll();
        _timer.Start();

        _ = Start();
    }

    private async Task Start()
    {
        try
        {
            _app = await Proxy.StartAsync(_args);
            _running = true;
        }
        catch (Exception e)
        {
            _running = false;
            _icon.Icon = Icons.Off;
            _icon.Text = "ClaimCheck: failed to start — " + e.Message;
        }
    }

    private async Task Restart()
    {
        try
        {
            if (_app is not null) await _app.StopAsync();
        }
        catch { /* stopping a dead proxy is fine */ }
        _running = false;
        await Start();
    }

    private async Task Poll()
    {
        var ok = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{_port}/health");
            ok = resp.IsSuccessStatusCode;
        }
        catch { ok = false; }

        _running = ok;
        _icon.Icon = ok ? Icons.Ok : Icons.Off;
        if (ok)
        {
            var total = Proxy.TotalRequests;
            var scrubbed = Proxy.ScrubbedRequests;
            _icon.Text = $"ClaimCheck proxy: running on 127.0.0.1:{_port} · {total} proxied · {scrubbed} scrubbed";
        }
        else if (_app is null)
        {
            _icon.Text = "ClaimCheck: proxy failed to start (see Status…)";
        }
        else
        {
            _icon.Text = $"ClaimCheck: proxy not answering on 127.0.0.1:{_port}";
        }
        _statusForm?.RefreshStatus();
    }

    private void ShowStatus()
    {
        if (_statusForm is null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm(_port);
            _statusForm.Show();
        }
        else
        {
            _statusForm.Activate();
        }
        _statusForm.RefreshStatus();
    }

    private async void ExitTray()
    {
        try
        {
            _timer.Stop();
            _icon.Visible = false;
            if (_app is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _app.StopAsync(cts.Token);
            }
        }
        catch { /* best effort */ }
        Application.Exit();
    }

    protected override void ExitThreadCore() { } // tray Exit drives shutdown, not the context
}

internal static class Icons
{
    public static readonly Icon Ok = Make(Colors: false);
    public static readonly Icon Off = Make(Colors: true);

    private static Icon Make(bool Colors)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var shield = new SolidBrush(Colors ? Color.Firebrick : Color.MediumSeaGreen);
            Point[] shape =
            {
                new(16, 2), new(28, 7), new(28, 15),
                new(26, 22), new(16, 30), new(6, 22), new(4, 15), new(4, 7),
            };
            g.FillPolygon(shield, shape);
            using var pen = new Pen(Color.White, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (Colors) // off: red X
            {
                g.DrawLine(pen, 11, 11, 21, 21);
                g.DrawLine(pen, 21, 11, 11, 21);
            }
            else // running: check
            {
                g.DrawLine(pen, 10, 16, 14, 21);
                g.DrawLine(pen, 14, 21, 22, 10);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

internal sealed class StatusForm : Form
{
    private readonly int _port;
    private readonly TextBox _text;

    public StatusForm(int port)
    {
        _port = port;
        Text = "ClaimCheck — status";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 340);

        _text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9.5f),
            BackColor = Color.White,
        };
        var refresh = new Button { Text = "Refresh", Dock = DockStyle.Bottom };
        refresh.Click += (_, _) => RefreshStatus();
        Controls.Add(_text);
        Controls.Add(refresh);
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var info = Status.Collect(_port);
        _text.Text = Render(info);
    }

    private static string Render(Status.Info i)
    {
        var lines = new List<string> { $"ClaimCheck {i.Version}", "" };
        lines.Add(i.ProxyRunning
            ? $"proxy       RUNNING  127.0.0.1:{i.ProxyPort} -> {i.Upstream}"
            : $"proxy       NOT RUNNING on 127.0.0.1:{i.ProxyPort}");
        if (i.TotalRequests is { } t)
            lines.Add($"traffic     {t} proxied · {i.ScrubbedRequests} scrubbed · last {i.LastRequestUtc ?? "n/a"}");
        if (i.Vault is not null)
            lines.Add($"vault       {i.Vault} ({i.VaultEntries} entries)");
        if (i.StartedUtc is not null)
            lines.Add($"up since    {i.StartedUtc}");
        if (i.FailClosed is { } fc)
            lines.Add($"fail-closed {(fc ? "ON" : "OFF")}  ·  NER {i.NerMode}");
        else
            lines.Add($"NER         {i.NerMode}");
        lines.Add($"vault dir   {i.VaultSummary}");
        lines.Add("");
        lines.Add("MCP wiring:");
        if (i.McpWiring.Length == 0) lines.Add("  none detected");
        else foreach (var w in i.McpWiring) lines.Add("  " + w);
        return string.Join(Environment.NewLine, lines);
    }
}
