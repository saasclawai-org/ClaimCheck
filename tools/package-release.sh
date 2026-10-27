#!/usr/bin/env bash
# Package the Windows release zip — THE ONLY sanctioned way to cut a zip.
#
# Why this exists (2026-10-27): hand-rolled zips since v0.7.4 shipped WITHOUT
# ClaimCheckTray.exe. The tray hosts the scrub proxy IN-PROCESS, and
# install.ps1 silently skips the tray copy when the zip lacks it — so every
# Windows user's proxy kept running the version from their first install no
# matter how many times they updated. INSTALL.txt promised the tray exe; the
# zip didn't have it. This script always rebuilds BOTH exes, zips all three
# files, and FAILS if any is missing.
#
# Usage: tools/package-release.sh <version>   (e.g. 0.8.2)
set -euo pipefail
cd "$(dirname "$0")/.."

VER="${1:?usage: package-release.sh <version>}"

echo "==> Publishing ClaimCheck.exe (CLI + MCP + proxy)"
dotnet publish -c Release -f net9.0-windows -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -o publish/win-x64

echo "==> Publishing ClaimCheckTray.exe (tray host — in-process proxy)"
dotnet publish tray/ClaimCheckTray.csproj -c Release -f net9.0-windows \
  -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -o publish/win-x64-tray

OUT="ClaimCheck-v${VER}-win-x64.zip"
echo "==> Zipping ${OUT}"
rm -f "$OUT"
python3 - "$VER" <<'PYEOF'
import sys, zipfile
ver = sys.argv[1]
out = f"ClaimCheck-v{ver}-win-x64.zip"
required = {
    "ClaimCheck.exe":     "publish/win-x64/ClaimCheck.exe",
    "ClaimCheckTray.exe": "publish/win-x64-tray/ClaimCheckTray.exe",
    "INSTALL.txt":        "tools/INSTALL.txt",
}
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for name, path in required.items():
        z.write(path, name)
with zipfile.ZipFile(out) as z:
    names = set(z.namelist())
    missing = set(required) - names
    assert not missing, f"ZIP MISSING FILES: {missing}"
    print("zip contents:", sorted(names))
PYEOF

ls -la "$OUT"
echo "==> Done. Upload with:"
echo "    gh release upload v${VER} ${OUT} --clobber -R saasclawai-org/ClaimCheck"
