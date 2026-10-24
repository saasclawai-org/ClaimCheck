<#
ClaimCheck install/update script (Windows)

  Install or update to the latest release in one shot:
      irm https://raw.githubusercontent.com/saasclawai-org/ClaimCheck/main/tools/install.ps1 | iex

  Optional install location (default: %USERPROFILE%\claimcheck):
      & .\install.ps1 -InstallDir "D:\tools\claimcheck"

- Downloads the latest GitHub release (win-x64 zip)
- Backs up any existing ClaimCheck.exe to ClaimCheck.exe.bak
- If the exe is locked, close VS Code (Cline spawns it) and re-run
- Your Cline MCP config keeps pointing at the same path; nothing else to do
#>
param(
    [string]$InstallDir = "$env:USERPROFILE\claimcheck"
)
$ErrorActionPreference = "Stop"
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}

Write-Host "ClaimCheck install/update -> $InstallDir"

$rel = Invoke-RestMethod "https://api.github.com/repos/saasclawai-org/ClaimCheck/releases/latest"
$asset = $rel.assets | Where-Object { $_.name -like "*win-x64*.zip" } | Select-Object -First 1
if (-not $asset) { throw "No win-x64 zip asset found on release $($rel.tag_name)" }
Write-Host ("Latest release: {0}  ({1}, {2} MB)" -f $rel.tag_name, $asset.name, [math]::Round($asset.size / 1MB, 1))

$tmp = Join-Path $env:TEMP ("claimcheck-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    $zipPath = Join-Path $tmp $asset.name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -UseBasicParsing
    Expand-Archive -Path $zipPath -DestinationPath $tmp -Force

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
Write-Host ("           {0} bytes, built/extracted {1}" -f $f.Length, $f.LastWriteTime)
Write-Host ""
Write-Host "Cline MCP config already points here? Then you're done - restart VS Code."
Write-Host "First-time setup: see INSTALL.txt (or llms-install.md in the repo) for the one-block config."
