#!/usr/bin/env bash
# claimcheck-upgrade.sh — one-command ClaimCheck upgrade for Linux (x64 + ARM64/Pi).
# Detects the arch, stops the proxy service (if running), downloads the matching build,
# installs it, restarts.
#
# Usage: sudo claimcheck-upgrade.sh [version]
#   e.g. sudo claimcheck-upgrade.sh 0.8.8
#
# Safe to re-run any time a new release ships. Skips service handling if the
# unit isn't installed yet (fresh appliance: run the runbook's Step 5 after).
set -euo pipefail

VER="${1:-0.8.8}"
UNIT=claimcheck-proxy.service

case "$(uname -m)" in
  x86_64)        ARCH=linux-x64 ;;
  aarch64|arm64) ARCH=linux-arm64 ;;
  *) echo "error: unsupported arch '$(uname -m)' (need x86_64 or aarch64)" >&2; exit 1 ;;
esac

if [ "$VER" = "latest" ]; then
  VER="$(curl -fsSL https://api.github.com/repos/saasclawai-org/ClaimCheck/releases/latest \
    | grep -o '"tag_name": *"[^"]*"' | head -1 | cut -d'"' -f4 | sed 's/^v//')"
  [ -n "$VER" ] || { echo "error: could not determine latest release" >&2; exit 1; }
fi

URL="https://github.com/saasclawai-org/ClaimCheck/releases/download/v${VER}/ClaimCheck-v${VER}-${ARCH}.tar.gz"

WAS=0
if systemctl is-active --quiet "$UNIT" 2>/dev/null; then
  WAS=1
  echo "==> stopping $UNIT ..."
  systemctl stop "$UNIT"
fi

echo "==> downloading ClaimCheck v${VER} (${ARCH}) ..."
curl -L --fail -o /tmp/cc-upgrade.tar.gz "$URL"
rm -rf /tmp/cc-upgrade && mkdir -p /tmp/cc-upgrade
tar xzf /tmp/cc-upgrade.tar.gz -C /tmp/cc-upgrade

echo "==> installing to /usr/local/bin/ClaimCheck ..."
install -m 755 /tmp/cc-upgrade/ClaimCheck /usr/local/bin/ClaimCheck
rm -rf /tmp/cc-upgrade /tmp/cc-upgrade.tar.gz

if [ "$WAS" = 1 ]; then
  echo "==> restarting $UNIT ..."
  systemctl start "$UNIT"
fi

echo "==> upgraded:"
/usr/local/bin/ClaimCheck version
