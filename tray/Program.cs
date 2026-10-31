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
    private readonly ToolStripMenuItem _presidioItem;
    private System.Windows.Forms.Timer? _updateTimer;
    private volatile bool _updateAvailable;
    private volatile bool _updating;

    public TrayApp(string[] args)
    {
        _args = args;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) _port = p;

        _updateItem = new ToolStripMenuItem("Check for updates now…", null, async (_, _) => await UpdateClicked());
        _nerItem = new ToolStripMenuItem("NER scrubbing (proxy)", null, (_, _) => NerToggled())
        { CheckOnClick = true, Checked = TrayConfig.NerEnabled };
        _presidioItem = new ToolStripMenuItem("Presidio scrubbing (proxy)", null, (_, _) => PresidioToggled())
        { CheckOnClick = true, Checked = TrayConfig.PresidioEnabled && !string.IsNullOrWhiteSpace(TrayConfig.PresidioUrl) };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Status…", null, (_, _) => ShowStatus());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open vault folder", null, (_, _) => OpenVault());
        menu.Items.Add(_nerItem);
        menu.Items.Add(_presidioItem);
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Provider setup…", null, (_, _) => new ProviderForm(_port).Show());
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
        if (_updating)
        {
            // stable amber state while the wrapper installs — no scary red flapping
            _icon.Icon = Icons.Update;
            _icon.Text = $"ClaimCheck: updating to v{UpdateChecker.Latest ?? "(new)"} — proxy paused, tray returns on the new build";
            return;
        }
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
        // Amber state + a notification that survives the tray exit (Windows locks the
        // running exe, so the tray must disappear during the file swap; the Scheduled
        // Task relaunches the new build, which shows its own starting state).
        _updating = true;
        _updateAvailable = false;
        _icon.Icon = Icons.Update;
        _icon.Text = $"ClaimCheck: installing v{v} — proxy paused, tray returns automatically";
        _icon.ShowBalloonTip(8000, "ClaimCheck update",
            $"Installing v{v}. The proxy pauses briefly and the tray reappears on the new build.",
            ToolTipIcon.Info);
        Task.Delay(3500).ContinueWith(_ => ExitTray());
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

    private void PresidioToggled()
    {
        var on = _presidioItem.Checked;
        if (on && string.IsNullOrWhiteSpace(TrayConfig.PresidioUrl))
        {
            _presidioItem.Checked = false;
            MessageBox.Show(
                "Presidio needs a local analyzer service first.\n\n" +
                "Run the official container once:\n" +
                "  docker run -d --name presidio --restart unless-stopped -p 3000:3000 mcr.microsoft.com/presidio-analyzer:latest\n" +
                "then setx SANITIZE_PRESIDIO_URL \"http://localhost:3000\" and restart the tray.\n\n" +
                "The presidio pass stays off until then — every other tier still works.",
                "ClaimCheck Presidio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        TrayConfig.SetPresidio(on);
        if (on) ClaimCheck.Presidio.WarmUp(); // re-warm after enabling so the first request isn't cold
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

    private void ShowSettings()
    {
        using var f = new SettingsForm();
        f.ShowDialog();
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
            if (!_updating) _icon.Visible = false; // keep the balloon alive across the swap
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
    public static readonly Icon Update = Load("claimcheck-update.ico");

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
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        var refresh = new Button { Text = "Refresh", Left = 8, Top = 8, Width = 100 };
        refresh.Click += (_, _) => RefreshStatus();
        var copy = new Button { Text = "Copy client Base URL", Left = 116, Top = 8, Width = 170 };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(ClientBaseUrl(Status.Collect(_port))); }
            catch { /* clipboard can be locked */ }
        };
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(copy);
        Controls.Add(_text);
        Controls.Add(buttons);
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
        if (i.PresidioMode is { } pm)
            lines.Add(i.PresidioFailures is { } pf && pf > 0
                ? $"presidio    {pm}  ·  {pf} skip{(pf == 1 ? "" : "s")}" + (i.PresidioLastFailure is { } plf ? $" · last: {plf}" : "")
                : $"presidio    {pm}");
        else
            lines.Add("presidio    off (set SANITIZE_PRESIDIO_URL)");
        lines.Add($"vault dir   {i.VaultSummary}");
        lines.Add("");
        lines.Add("MCP wiring:");
        if (i.McpWiring.Length == 0) lines.Add("  none detected");
        else foreach (var w in i.McpWiring) lines.Add("  " + w);
        lines.Add("");
        lines.Add($"client url  {ClientBaseUrl(i)}   <- copy into your client's Base URL");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The Base URL a client should use: our port + the upstream's path
    /// (the proxy mirrors the upstream path on its own port). Provider-agnostic.</summary>
    internal static string ClientBaseUrl(Status.Info i)
    {
        var path = "";
        if (i.Upstream is not null)
        {
            try { path = new Uri(i.Upstream).AbsolutePath.TrimEnd('/'); } catch { path = ""; }
        }
        return $"http://[IP_8]:{i.ProxyPort}{path}";
    }
}

internal sealed class SettingsForm : Form
{
    private readonly CheckBox _dates;
    private readonly CheckBox _injection;
    private readonly CheckBox _audit;
    private readonly TextBox _nerTypes;
    private readonly TextBox _roots;

    public SettingsForm()
    {
        Text = "ClaimCheck — scrub settings (proxy)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 330);

        _dates = new CheckBox
        {
            Text = "Ticket dates  (SANITIZE_DATES) — off by default: dates are operational context",
            Left = 12, Top = 14, Width = 570,
            Checked = TrayConfig.KnobOn("SANITIZE_DATES"),
        };
        _injection = new CheckBox
        {
            Text = "Prompt-injection patterns  (SANITIZE_INJECTION_PATTERNS)",
            Left = 12, Top = 40, Width = 570,
            Checked = TrayConfig.KnobOn("SANITIZE_INJECTION_PATTERNS"),
        };
        _audit = new CheckBox
        {
            Text = "Audit log: post-scrub bodies as JSON lines  (SANITIZE_PROXY_LOG) — tickets only",
            Left = 12, Top = 66, Width = 570,
            Checked = TrayConfig.KnobOn("SANITIZE_PROXY_LOG"),
        };

        var nerLabel = new Label
        {
            Text = "NER types filter  (SANITIZE_NER_TYPES) — comma list, blank = all four:",
            Left = 12, Top = 100, Width = 570, AutoSize = false, Height = 20,
        };
        _nerTypes = new TextBox
        {
            Left = 12, Top = 122, Width = 300,
            Text = TrayConfig.GetKnob("SANITIZE_NER_TYPES") ?? "",
        };

        var rootsLabel = new Label
        {
            Text = "Path allowlist  (SANITIZE_ROOTS) — " + Path.PathSeparator + "-separated, blank = unrestricted:",
            Left = 12, Top = 158, Width = 570, AutoSize = false, Height = 20,
        };
        _roots = new TextBox
        {
            Left = 12, Top = 180, Width = 570,
            Text = TrayConfig.GetKnob("SANITIZE_ROOTS") ?? "",
        };

        var note = new Label
        {
            Text = "Settings apply to the next scrubbed request — no restart needed.\n" +
                   "NER and Presidio toggles live in the tray menu.",
            Left = 12, Top = 216, Width = 570, Height = 40,
            ForeColor = SystemColors.GrayText,
        };

        var save = new Button { Text = "Save", Left = 12, Top = 262, Width = 100 };
        save.Click += (_, _) => Save();
        var cancel = new Button { Text = "Cancel", Left = 120, Top = 262, Width = 100, DialogResult = DialogResult.Cancel };
        CancelButton = cancel;

        Controls.AddRange(new Control[] { _dates, _injection, _audit, nerLabel, _nerTypes, rootsLabel, _roots, note, save, cancel });
        AcceptButton = save;
    }

    private void Save()
    {
        var enablingInjection = _injection.Checked && !TrayConfig.KnobOn("SANITIZE_INJECTION_PATTERNS");
        if (enablingInjection)
        {
            if (MessageBox.Show(
                    "Enable prompt-injection patterns?\n\nFiles in this repo that quote attack strings " +
                    "(security tests, prompt-engineering docs) will be ticketed too. " +
                    "Use SANITIZE_ROOTS to scope the lane if needed.",
                    "ClaimCheck injection patterns", MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
                != DialogResult.OK)
            {
                _injection.Checked = false;
                return;
            }
        }

        TrayConfig.SetKnob("SANITIZE_DATES", _dates.Checked ? "1" : null);
        TrayConfig.SetKnob("SANITIZE_INJECTION_PATTERNS", _injection.Checked ? "1" : null);
        TrayConfig.SetKnob("SANITIZE_PROXY_LOG", _audit.Checked ? "1" : null);
        TrayConfig.SetKnob("SANITIZE_NER_TYPES", _nerTypes.Text.Trim());
        TrayConfig.SetKnob("SANITIZE_ROOTS", _roots.Text.Trim());
        Close();
    }
}

internal sealed class ProviderForm : Form
{
    public ProviderForm(int port)
    {
        Text = "ClaimCheck — provider setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 420);

        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9.5f),
            BackColor = Color.White,
            Text = Render(port),
        };
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        var copy = new Button { Text = "Copy all", Left = 8, Top = 8, Width = 100 };
        copy.Click += (_, _) => { try { Clipboard.SetText(text.Text); } catch { } };
        buttons.Controls.Add(copy);
        Controls.Add(text);
        Controls.Add(buttons);
    }

    private static string ClientUrl(int port, string upstreamPath) =>
        $"http://[IP_8]:{port}{upstreamPath.TrimEnd('/')}";

    private static string Render(int port)
    {
        var lines = new List<string>
        {
            "Point your client's Base URL at the proxy - the proxy mirrors the upstream path on its own port.",
            "",
            "provider        installer -Upstream                          client Base URL",
            "--------        ---------------------------                  ---------------",
            $"z.ai            https://api.z.ai/api/coding/paas/v4/         {ClientUrl(port, "/api/coding/paas/v4")}",
            $"OpenAI          https://api.openai.com/v1/                   {ClientUrl(port, "/v1")}",
            $"Anthropic       https://api.anthropic.com/v1/                {ClientUrl(port, "/v1")}",
            $"OpenRouter      https://openrouter.ai/api/v1/                {ClientUrl(port, "/api/v1")}",
            $"Ollama (local)  http://[IP_8]:11434/                         {ClientUrl(port, "")}",
            "",
            "Install/update with a preset (PowerShell):",
            "  install.ps1 -WithProxy -Provider zai|openai|anthropic|openrouter",
            "  or pass your own:  install.ps1 -WithProxy -Upstream https://your-provider/v1/",
            "",
            "Keep your real API key in the client - auth passes through the proxy untouched.",
            "The Status window shows the live computed URL for the currently running upstream.",
        };
        return string.Join(Environment.NewLine, lines);
    }
}
