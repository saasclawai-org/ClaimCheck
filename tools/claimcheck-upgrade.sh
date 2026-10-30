#!/usr/bin/env bash
# claimcheck-upgrade.sh — one-command ClaimCheck upgrade for Linux (x64 + ARM64/Pi).
# Detects the arch, stops active proxy lanes, downloads + checksum-verifies the matching
# build, snapshots the old binary for rollback, installs, restarts only what was running,
# and health-checks the result.
#
# Usage: claimcheck-upgrade.sh [version|latest]     (default: latest)
#
# Layout config (optional) — a claimcheck-upgrade.conf next to this script, or point
# CC_UPGRADE_CONF at one. Two lines cover every known layout:
#   Pi / appliance:   (no conf — defaults below)
#   Predator/multi:   BIN_PATH=/home/norman/claimcheck/ClaimCheck
#                     UNIT_PATTERN='claimcheck-proxy@*'
# Defaults: BIN_PATH=/usr/local/bin/ClaimCheck, UNIT_PATTERN=claimcheck-proxy.service
#
# Safe to re-run any time a new release ships. Skips service handling if the unit
# pattern matches nothing (fresh appliance: run the runbook's Step 5 after).
# Rollback: cp "$BIN_PATH.prev" "$BIN_PATH" && restart the lanes.
set -euo pipefail

REPO=saasclawai-org/ClaimCheck
SELF_DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
CONF="${CC_UPGRADE_CONF:-$SELF_DIR/claimcheck-upgrade.conf}"
[ -f "$CONF" ] && . "$CONF"
BIN_PATH="${BIN_PATH:-/usr/local/bin/ClaimCheck}"
UNIT_PATTERN="${UNIT_PATTERN:-claimcheck-proxy.service}"

VER="${1:-latest}"

case "$(uname -m)" in
  x86_64)        ARCH=linux-x64 ;;
  aarch64|arm64) ARCH=linux-arm64 ;;
  *) echo "error: unsupported arch '$(uname -m)' (need x86_64 or aarch64)" >&2; exit 1 ;;
esac

if [ "$VER" = "latest" ]; then
  VER="$(curl -fsSL https://api.github.com/repos/$REPO/releases/latest \
    | grep -o '"tag_name": *"[^"]*"' | head -1 | cut -d'"' -f4 | sed 's/^v//')"
  [ -n "$VER" ] || { echo "error: could not determine latest release" >&2; exit 1; }
fi
VER="${VER#v}"

ASSET="ClaimCheck-v${VER}-${ARCH}.tar.gz"
BASE="https://github.com/$REPO/releases/download/v${VER}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# sudo only where it's actually needed: system-unit restarts and non-home install paths
SUDO=""
[ "$(id -u)" -ne 0 ] && SUDO=sudo

echo "==> downloading ClaimCheck v${VER} (${ARCH}) ..."
curl -fsSL "$BASE/$ASSET" -o "$TMP/$ASSET"
curl -fsSL "$BASE/SHA256SUMS.txt" -o "$TMP/SHA256SUMS.txt"
( cd "$TMP" && grep " $ASSET\$" SHA256SUMS.txt | sha256sum -c - )

echo "==> unpacking + smoke check ..."
tar xzf "$TMP/$ASSET" -C "$TMP"
GOT="$("$TMP/ClaimCheck" version)"
[ "$GOT" = "ClaimCheck $VER" ] || { echo "error: downloaded binary reports '$GOT', wanted 'ClaimCheck $VER'" >&2; exit 1; }

# which lanes are running right now — restart exactly those, nothing else
mapfile -t ACTIVE < <(systemctl list-units --no-legend --type=service --plain "$UNIT_PATTERN" 2>/dev/null \
  | awk '$3 == "active" { print $1 }' || true)

if [ "${#ACTIVE[@]}" -gt 0 ]; then
  echo "==> stopping active lanes: ${ACTIVE[*]}"
  $SUDO systemctl stop "${ACTIVE[@]}"
fi

if [ -f "$BIN_PATH" ]; then
  cp -f "$BIN_PATH" "$BIN_PATH.prev"
  echo "==> previous build saved as $BIN_PATH.prev"
fi

echo "==> installing to $BIN_PATH ..."
mkdir -p "$(dirname "$BIN_PATH")"
install -m 755 "$TMP/ClaimCheck" "$BIN_PATH"
# when root installs into a user's home (predator layout), keep the file owned by the
# directory's owner instead of leaving a root-owned binary in $HOME
if [ "$(id -u)" -eq 0 ]; then
  chown --reference="$(dirname "$BIN_PATH")" "$BIN_PATH" 2>/dev/null || true
fi

if [ "${#ACTIVE[@]}" -gt 0 ]; then
  echo "==> restarting: ${ACTIVE[*]}"
  $SUDO systemctl start "${ACTIVE[@]}"
  for u in "${ACTIVE[@]}"; do
    if systemctl is-active --quiet "$u"; then
      echo "    $u active ✓"
    else
      echo "    WARNING: $u not active after restart — check: journalctl -u $u -n 20" >&2
    fi
  done
fi

echo "==> upgraded:"
"$BIN_PATH" version
