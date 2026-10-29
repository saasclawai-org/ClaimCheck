#!/usr/bin/env bash
# claimcheck-upgrade.sh — one-command ClaimCheck upgrade for the Raspberry Pi appliance.
# Stops the proxy service (if running), downloads the new ARM64 build, installs it, restarts.
#
# Usage: sudo claimcheck-upgrade.sh [version]
#   e.g. sudo claimcheck-upgrade.sh 0.8.7
#
# Safe to re-run any time a new release ships. Skips service handling if the
# unit isn't installed yet (fresh appliance: run the runbook's Step 5 after).
set -euo pipefail

VER="${1:-0.8.7}"
UNIT=claimcheck-proxy.service
URL="https://github.com/saasclawai-org/ClaimCheck/releases/download/v${VER}/ClaimCheck-v${VER}-linux-arm64.tar.gz"

WAS=0
if systemctl is-active --quiet "$UNIT" 2>/dev/null; then
  WAS=1
  echo "==> stopping $UNIT ..."
  systemctl stop "$UNIT"
fi

echo "==> downloading ClaimCheck v${VER} (linux-arm64) ..."
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
