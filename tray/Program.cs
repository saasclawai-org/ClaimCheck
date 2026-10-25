using System.Diagnostics;
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
        TrayConfig.Load();
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
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _nerItem;
    private System.Windows.Forms.Timer? _updateTimer;
    private volatile bool _updateAvailable;

    public TrayApp(string[] args)
    {
        _args = args;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) _port = p;

        _updateItem = new ToolStripMenuItem("Check for updates now…", null, async (_, _) => await UpdateClicked());
        _nerItem = new ToolStripMenuItem("NER scrubbing (proxy)", null, (_, _) => NerToggled())
        { CheckOnClick = true, Checked = TrayConfig.NerEnabled };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Status…", null, (_, _) => ShowStatus());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open vault folder", null, (_, _) => OpenVault());
        menu.Items.Add(_nerItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart proxy", null, (_, _) => _ = Restart());
        menu.Items.Add(_updateItem);
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

        _ = WarmUpdateCheck();
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
        if (_updateAvailable) _icon.Text += " · update available";
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

    private async Task UpdateClicked()
    {
        if (UpdateChecker.Checking) return;
        if (UpdateChecker.Latest is not null) { DoUpdate(); return; }
        _updateItem.Text = "Checking for updates…";
        await UpdateChecker.CheckAsync();
        RefreshUpdateUi();
    }

    private void RefreshUpdateUi()
    {
        if (UpdateChecker.Latest is { } v)
        {
            _updateAvailable = true;
            _updateItem.Text = $"Download and install v{v}…";
        }
        else
        {
            _updateAvailable = false;
            _updateItem.Text = $"Up to date (v{Status.Version})" +
                (UpdateChecker.LastCheck is { } t ? $" — checked {t:HH:mm}" : "");
        }
    }

    private async Task WarmUpdateCheck()
    {
        await Task.Delay(20_000);
        await UpdateChecker.CheckAsync();
        RefreshUpdateUi();
        _updateTimer = new System.Windows.Forms.Timer { Interval = 4 * 60 * 60 * 1000 };
        _updateTimer.Tick += async (_, _) =>
        {
            await UpdateChecker.CheckAsync();
            RefreshUpdateUi();
        };
        _updateTimer.Start();
    }

    private void DoUpdate()
    {
        var v = UpdateChecker.Latest!;
        if (MessageBox.Show($"Update ClaimCheck to v{v}?\n\nThe tray and proxy will stop and restart on the new build.",
                "ClaimCheck update", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            return;
        try
        {
            var wrapper = UpdateChecker.StageUpdate();
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{wrapper}\"")
            { UseShellExecute = true, CreateNoWindow = true });
        }
        catch (Exception e)
        {
            MessageBox.Show("Update failed to start: " + e.Message, "ClaimCheck update",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _icon.Text = "ClaimCheck: updating…";
        Task.Delay(1500).ContinueWith(_ => ExitTray());
    }

    private void NerToggled()
    {
        var on = _nerItem.Checked;
        if (on && !NerModelCached())
        {
            if (MessageBox.Show(
                    "Enable NER scrubbing?\n\nFree-text person/org/location detection runs locally; the first use downloads a ~110 MB model (one time).\n\nNote: this governs the scrub PROXY only — the Cline MCP server has its own NER setting.",
                    "ClaimCheck NER", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            {
                _nerItem.Checked = false;
                return;
            }
        }
        TrayConfig.SetNer(on);
    }

    private static bool NerModelCached()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claimcheck", "models", string.Join('_', ClaimCheck.Ner.ModelRepo.Split('/')));
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any();
        }
        catch { return false; }
    }

    private void OpenVault()
    {
        try
        {
            Directory.CreateDirectory(Sanitizer.VaultDir);
            Process.Start(new ProcessStartInfo(Sanitizer.VaultDir) { UseShellExecute = true });
        }
        catch { /* best effort */ }
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
    public static readonly Icon Ok = Load("claimcheck-ok.ico");
    public static readonly Icon Off = Load("claimcheck-off.ico");

    private static Icon Load(string name)
    {
        try
        {
            var asm = typeof(Icons).Assembly;
            var res = asm.GetManifestResourceNames()
                .First(r => r.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            using var s = asm.GetManifestResourceStream(res);
            return new Icon(s);
        }
        catch
        {
            return SystemIcons.Shield; // fallback if embedded resources are missing
        }
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
