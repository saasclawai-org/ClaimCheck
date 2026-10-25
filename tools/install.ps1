<#
ClaimCheck install/update script (Windows)

  Install or update to the latest release in one shot:
      irm https://raw.githubusercontent.com/saasclawai-org/ClaimCheck/main/tools/install.ps1 | iex

  Optional install location (default: %USERPROFILE%\claimcheck):
      & .\install.ps1 -InstallDir "D:\tools\claimcheck"

  Optional: enable the local NER tier (free-text names/orgs/locations;
  downloads a ~109MB model on first use):
      irm ... | iex   with   -EnableNER  (when running the downloaded file)

  Optional: on-wire enforcement — scrub proxy auto-started at logon:
      & .\install.ps1 -WithProxy
      (then point your Cline model provider Base URL at
       http://127.0.0.1:8766/api/coding/paas/v4 — see README Mode 2)

- Downloads the latest GitHub release (win-x64 zip)
- Backs up any existing ClaimCheck.exe to ClaimCheck.exe.bak
- Update = re-run this same script (stops the logon proxy task first, then backs up)
- -WithProxy now runs the proxy as a system-tray app (green/red shield icon, live status)
- Auto-wires the Cline MCP config (VS Code stable + Insiders) if present,
  preserving your other MCP servers. If VS Code is running, it skips the
  config write (Cline rewrites that file from memory) and prints the block.
- The exe is only locked while Cline is running it: if the copy fails,
  close VS Code and re-run.
#>
param(
    [string]$InstallDir = "$env:USERPROFILE\claimcheck",
    [switch]$EnableNER,
    [switch]$WithProxy,
    [string]$ProxyUpstream = "https://api.z.ai/api/coding/paas/v4/",
    [string]$Provider = "",
    [int]$ProxyPort = 8766
)
$ErrorActionPreference = "Stop"
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}

Write-Host "ClaimCheck install/update -> $InstallDir"

# --- 1. Fetch latest release -------------------------------------------------
$rel = Invoke-RestMethod "https://api.github.com/repos/saasclawai-org/ClaimCheck/releases/latest"
$asset = $rel.assets | Where-Object { $_.name -like "*win-x64*.zip" } | Select-Object -First 1
if (-not $asset) { throw "No win-x64 zip asset found on release $($rel.tag_name)" }
Write-Host ("Latest release: {0}  ({1}, {2} MB)" -f $rel.tag_name, $asset.name, [math]::Round($asset.size / 1MB, 1))

# --- 2. Download + extract ----------------------------------------------------
$tmp = Join-Path $env:TEMP ("claimcheck-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    $zipPath = Join-Path $tmp $asset.name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -UseBasicParsing
    Expand-Archive -Path $zipPath -DestinationPath $tmp -Force

    # --- 3. Install (with backup + lock handling) -----------------------------
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $target = Join-Path $InstallDir "ClaimCheck.exe"
    $trayTarget = Join-Path $InstallDir "ClaimCheckTray.exe"

    if (Test-Path $target) {
        Copy-Item $target "$target.bak" -Force
        Write-Host "Backed up previous exe -> ClaimCheck.exe.bak"
    }
    if (Test-Path $trayTarget) {
        Copy-Item $trayTarget "$trayTarget.bak" -Force
        Write-Host "Backed up previous tray app -> ClaimCheckTray.exe.bak"
    }

    # Release the exe lock held by the logon proxy task (if any) before copying.
    $proxyTask = Get-ScheduledTask -TaskName "ClaimCheckProxy" -ErrorAction SilentlyContinue
    $taskWasInstalled = $null -ne $proxyTask
    if ($proxyTask -and $proxyTask.State -ne "Disabled") {
        Stop-ScheduledTask -TaskName "ClaimCheckProxy" -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
        Write-Host "Stopped ClaimCheckProxy task (was holding the previous exe)."
    }

    try {
        Copy-Item (Join-Path $tmp "ClaimCheck.exe") $target -Force
        $traySource = Join-Path $tmp "ClaimCheckTray.exe"
        if (Test-Path $traySource) {
            Copy-Item $traySource $trayTarget -Force
        }
    } catch {
        Write-Host ""
        Write-Host "A ClaimCheck executable is locked. Close VS Code and the ClaimCheck tray app, then re-run this script." -ForegroundColor Yellow
        exit 1
    }
    Copy-Item (Join-Path $tmp "INSTALL.txt") (Join-Path $InstallDir "INSTALL.txt") -Force -ErrorAction SilentlyContinue
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

$f = Get-Item $target
Write-Host ("Installed: {0}" -f $f.FullName)
Write-Host ("           {0} bytes  ({1})" -f $f.Length, $f.LastWriteTime)

# Add install dir to the user PATH (takes effect in NEW terminals)
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$InstallDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$userPath;$InstallDir", "User")
    Write-Host "Added $InstallDir to your user PATH (open a new terminal to use it)."
}

# Optional: on-wire enforcement — run the scrub proxy at logon
$providerUpstreams = @{
    zai        = "https://api.z.ai/api/coding/paas/v4/"
    openai     = "https://api.openai.com/v1/"
    anthropic  = "https://api.anthropic.com/v1/"
    openrouter = "https://openrouter.ai/api/v1/"
}
if ($Provider) {
    if ($providerUpstreams.ContainsKey($Provider)) { $ProxyUpstream = $providerUpstreams[$Provider] }
    else { Write-Host "Unknown -Provider '$Provider' (use: $($providerUpstreams.Keys -join ', ')) - keeping default upstream." -ForegroundColor Yellow }
}
if ($WithProxy) {
    $trayExe = $trayTarget
    $useTray = Test-Path $trayExe
    $proxyExe = if ($useTray) { $trayExe } else { $target }
    $proxyArgs = "--proxy --upstream `"$ProxyUpstream`" --port $ProxyPort"
    try {
        $action   = New-ScheduledTaskAction -Execute $proxyExe -Argument $proxyArgs
        $trigger  = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable
        Register-ScheduledTask -TaskName "ClaimCheckProxy" -Action $action -Trigger $trigger -Settings $settings -Description "ClaimCheck on-wire PII scrub proxy" -Force | Out-Null
        Start-ScheduledTask -TaskName "ClaimCheckProxy"
        Write-Host ""
        Write-Host ""
        if ($useTray) {
            Write-Host "Proxy registered + started as a TRAY APP (green/red shield in the system tray; double-click = status)." -ForegroundColor Green
        } else {
            Write-Host "Proxy registered + started (listens on 127.0.0.1:$ProxyPort, restarts at every logon)." -ForegroundColor Green
        }
        Write-Host "Point your Cline model provider Base URL at: http://127.0.0.1:$ProxyPort/api/coding/paas/v4"
        Write-Host "(keep your real API key in Cline - auth passes through the proxy untouched)"
    } catch {
        Write-Host "Scheduled task registration failed: $_" -ForegroundColor Yellow
        Write-Host "Manual fallback - put this command in your Startup folder (Win+R -> shell:startup):" -ForegroundColor Yellow
        Write-Host "  `"$target`" $proxyArgs"
    }
}

# Plain update: restore the proxy task we stopped for the swap (no -WithProxy needed).
if ($taskWasInstalled -and -not $WithProxy) {
    Start-ScheduledTask -TaskName "ClaimCheckProxy" -ErrorAction SilentlyContinue
    Write-Host "Restarted the ClaimCheckProxy task on the updated build."
}

# --- 4. Wire up Cline MCP config automatically --------------------------------
$exeForward = $target -replace '\\', '/'
$settingsTargets = @(
    "$env:APPDATA\Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json",
    "$env:APPDATA\Code - Insiders\User\globalStorage\saoudrizwan.claude-dev-insiders\settings\cline_mcp_settings.json"
)

$codeRunning = Get-Process -Name Code, "Code - Insiders" -ErrorAction SilentlyContinue
$wired = $false

foreach ($settingsPath in $settingsTargets) {
    if (-not (Test-Path $settingsPath)) { continue }

    if ($codeRunning) {
        Write-Host ""
        Write-Host "VS Code is running - skipping automatic Cline config update (Cline rewrites that file from memory while running)." -ForegroundColor Yellow
        Write-Host "Close VS Code and re-run this script, or paste the block below manually." -ForegroundColor Yellow
        break
    }

    try {
        $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
    } catch {
        Write-Host "Could not parse $settingsPath - skipping auto-config (fix the JSON by hand)." -ForegroundColor Yellow
        continue
    }

    if (-not $json.PSObject.Properties['mcpServers']) {
        $json | Add-Member -MemberType NoteProperty -Name mcpServers -Value ([pscustomobject]@{})
    }
    $envObj = [pscustomobject]@{}
    if ($EnableNER) { $envObj | Add-Member -MemberType NoteProperty -Name SANITIZE_NER -Value "1" }
    $block = [pscustomobject]@{
        command      = $exeForward
        args         = @()
        env          = $envObj
        disabled     = $false
        autoApprove  = @("scan_file", "sanitize_text")
    }
    if ($json.mcpServers.PSObject.Properties['claimcheck']) {
        $json.mcpServers.claimcheck = $block
    } else {
        $json.mcpServers | Add-Member -MemberType NoteProperty -Name claimcheck -Value $block
    }
    [System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 10))
    Write-Host "Cline config updated: $settingsPath"
    $wired = $true
}

# --- 5. Manual fallback block (printed when auto-wiring was skipped) ----------
if (-not $wired) {
    Write-Host ""
    Write-Host "Manual Cline config block (MCP Servers -> Configure), if needed:" -ForegroundColor Cyan
    $envObj = [pscustomobject]@{}
    if ($EnableNER) { $envObj | Add-Member -MemberType NoteProperty -Name SANITIZE_NER -Value "1" }
    $manual = [pscustomobject]@{
        command     = $exeForward
        args        = @()
        env         = $envObj
        disabled    = $false
        autoApprove = @("scan_file", "sanitize_text")
    }
    Write-Host ($manual | ConvertTo-Json -Depth 5)
}

Write-Host ""
Write-Host "Done. Restart VS Code so Cline picks up the new exe."
if ($EnableNER) { Write-Host "NER is on - the model (~109MB) downloads on first use, one-time." }
Write-Host "No NER? Add -EnableNER on a re-run to switch on free-text name/org/location detection."
