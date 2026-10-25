#!/usr/bin/env bash
# ClaimCheck install/update script (Linux)
#
#   Install or update to the latest release in one shot:
#       curl -fsSL https://raw.githubusercontent.com/saasclawai-org/ClaimCheck/main/tools/install.sh | bash
#
#   Options (when running the downloaded file):
#       ./install.sh --proxy --upstream https://api.provider.com/v1/ [--port 8766]
#           Also registers a systemd --user service for the scrub proxy.
#       ./install.sh --dir ~/.local/bin
#
# - Per-user install (default: ~/.local/bin), no sudo
# - Detects arch: linux-x64 (x86_64) / linux-arm64 (aarch64)
# - Update = re-run this script (backs up the previous binary)
# - Vault and status are identical to the Windows build: `ClaimCheck status`
set -euo pipefail

REPO="saasclawai-org/ClaimCheck"
INSTALL_DIR="${HOME}/.local/bin"
PROXY=0
PORT=8766
UPSTREAM=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --proxy) PROXY=1; shift ;;
    --port) PORT="$2"; shift 2 ;;
    --upstream) UPSTREAM="$2"; shift 2 ;;
    --dir) INSTALL_DIR="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 1 ;;
  esac
done

say() { printf '%s\n' "$*"; }

fetch() { # fetch <url> <outfile>
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$1" -o "$2"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO "$2" "$1"
  else
    say "error: need curl or wget" >&2; exit 1
  fi
}

case "$(uname -m)" in
  x86_64) ARCH="linux-x64" ;;
  aarch64 | arm64) ARCH="linux-arm64" ;;
  *) say "error: unsupported arch '$(uname -m)' (need x86_64 or aarch64)" >&2; exit 1 ;;
esac

say "ClaimCheck install/update -> $INSTALL_DIR ($ARCH)"

# --- 1. latest release -------------------------------------------------------
api="$(fetch "https://api.github.com/repos/${REPO}/releases/latest" /dev/stdout)"
TAG="$(printf '%s' "$api" | grep -o '"tag_name": *"[^"]*"' | head -1 | cut -d'"' -f4)"
[[ -n "$TAG" ]] || { say "error: could not determine latest release" >&2; exit 1; }
say "Latest release: $TAG"

# --- 2. download + extract ---------------------------------------------------
ASSET="ClaimCheck-${TAG}-${ARCH}.tar.gz"
URL="${CLAIMCHECK_DOWNLOAD_URL:-https://github.com/${REPO}/releases/download/${TAG}/${ASSET}}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
say "Downloading $ASSET ..."
fetch "$URL" "${TMP}/asset.tar.gz"
tar -xzf "${TMP}/asset.tar.gz" -C "$TMP"
[[ -f "${TMP}/ClaimCheck" ]] || { say "error: archive missing ClaimCheck binary" >&2; exit 1; }

# --- 3. install (with backup) ------------------------------------------------
mkdir -p "$INSTALL_DIR"
TARGET="${INSTALL_DIR}/ClaimCheck"
if [[ -f "$TARGET" ]]; then
  cp "$TARGET" "${TARGET}.bak"
  say "Backed up previous binary -> ClaimCheck.bak"
fi
install -m 0755 "${TMP}/ClaimCheck" "$TARGET"
say "Installed: $TARGET"

case ":${PATH}:" in
  *":${INSTALL_DIR}:"*) ;;
  *) say "NOTE: ${INSTALL_DIR} is not on your PATH. Add it, e.g.:"
     say "      echo 'export PATH=\"\$HOME/.local/bin:\$PATH\"' >> ~/.bashrc   # or ~/.zshrc" ;;
esac

# --- 4. optional systemd --user service for the scrub proxy ------------------
if [[ "$PROXY" -eq 1 ]]; then
  [[ -n "$UPSTREAM" ]] || { say "error: --proxy needs --upstream <base-url>" >&2; exit 1; }
  UNIT_DIR="${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user"
  mkdir -p "$UNIT_DIR"
  cat > "${UNIT_DIR}/claimcheck-proxy.service" <<EOF
[Unit]
Description=ClaimCheck scrub proxy (PII on-wire)
After=network-online.target

[Service]
ExecStart=${TARGET} --proxy --upstream ${UPSTREAM} --port ${PORT}
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
EOF
  if command -v systemctl >/dev/null 2>&1; then
    systemctl --user daemon-reload
    systemctl --user enable --now claimcheck-proxy.service
    say ""
    say "Proxy registered + started (systemd --user, listens on 127.0.0.1:${PORT})."
    say "Point your client's Base URL at: http://127.0.0.1:${PORT}/ (mirror the upstream path)"
    say "Check:  ClaimCheck status   |   journalctl --user -u claimcheck-proxy -f"
  else
    say "systemd not available - run the proxy manually:"
    say "  ${TARGET} --proxy --upstream ${UPSTREAM} --port ${PORT} &"
  fi
fi

say ""
say "Done. Try: ClaimCheck status"
