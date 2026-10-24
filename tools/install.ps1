<#
ClaimCheck install/update script (Windows)

  Install or update to the latest release in one shot:
      irm https://raw.githubusercontent.com/saasclawai-org/ClaimCheck/main/tools/install.ps1 | iex

  Optional install location (default: %USERPROFILE%\claimcheck):
      & .\install.ps1 -InstallDir "D:\tools\claimcheck"

  Optional: enable the local NER tier (free-text names/orgs/locations;
  downloads a ~109MB model on first use):
      irm ... | iex   with   -EnableNER  (when running the downloaded file)

- Downloads the latest GitHub release (win-x64 zip)
- Backs up any existing ClaimCheck.exe to ClaimCheck.exe.bak
- Auto-wires the Cline MCP config (VS Code stable + Insiders) if present,
  preserving your other MCP servers. If VS Code is running, it skips the
  config write (Cline rewrites that file from memory) and prints the block.
- The exe is only locked while Cline is running it: if the copy fails,
  close VS Code and re-run.
#>
param(
    [string]$InstallDir = "$env:USERPROFILE\claimcheck",
    [switch]$EnableNER
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

    if (Test-Path $target) {
        Copy-Item $target "$target.bak" -Force
        Write-Host "Backed up previous exe -> ClaimCheck.exe.bak"
    }

    try {
        Copy-Item (Join-Path $tmp "ClaimCheck.exe") $target -Force
    } catch {
        Write-Host ""
        Write-Host "ClaimCheck.exe is locked. Close VS Code (Cline may be running the server) and re-run this script." -ForegroundColor Yellow
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
