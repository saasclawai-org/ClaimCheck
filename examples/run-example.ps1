# ClaimCheck end-to-end round-trip: scan -> ticket -> restore
# All values in secrets-test.txt are fake. Requires ClaimCheck on PATH.

$ErrorActionPreference = "Stop"

$src      = Join-Path $PSScriptRoot "secrets-test.txt"
$ticketed = Join-Path $env:TEMP "secrets-test-ticketed.txt"

# 1. Scan - JSON summary + sanitized (ticketed) content
$scan = ClaimCheck sanitize-file $src --content | ConvertFrom-Json

Write-Host "--- findings (types + lines only, no raw values) ---"
$scan.findings | Format-Table Line, Type, Placeholder

# 2. Save the ticketed content - this copy is safe to share
$scan.content | Out-File -Encoding utf8 $ticketed
Write-Host "--- ticketed copy written to $ticketed ---"
Get-Content $ticketed

# 3. Restore - tickets back to values, from the local vault only
$restored = ClaimCheck restore $ticketed --content
Write-Host "--- restored from vault ---"
Write-Host $restored

# 4. Round-trip check: restored output must match the original exactly
if ($restored.Trim() -eq (Get-Content $src -Raw).Trim()) {
    Write-Host "`nROUND-TRIP PASS - restored output identical to original" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nROUND-TRIP FAIL - restored output differs from original" -ForegroundColor Red
    exit 1
}
