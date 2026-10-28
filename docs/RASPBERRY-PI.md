# Raspberry Pi & ARM64 single-board computers

ClaimCheck ships a **linux-arm64** build — one self-contained binary, no .NET
install, no account, no network calls. All three modes work on a Pi: CLI,
MCP server, on-wire proxy.

> **Status: silicon-verified.** First real-hardware test 2026-10-28 — Raspberry Pi,
> 64-bit OS: startup, gazetteer load (250,551 names), FEIN + email scrub, vault
> mint — all green. The arm64 build is cross-compiled from the same sources as
> the Windows release, with ARM64 natives bundled (including ONNX Runtime).
> Broke it on your board? Open an Issue.

## Requirements

- Pi 3/4/5 (or Zero 2 W — core modes only) on a **64-bit OS** (Raspberry Pi OS
  64-bit, Ubuntu arm64, DietPi arm64, …). The binary is arm64; a 32-bit OS
  refuses it with `exec format error`.
- `libssl3` — present on every current distro (`sudo apt install libssl3` if not).
- RAM: core modes (CLI / MCP / proxy) are happy in a few hundred MB. The optional
  NER tier wants 2 GB+ headroom.

## Install

```bash
curl -L -o claimcheck.tar.gz \
  https://github.com/saasclawai-org/ClaimCheck/releases/download/v0.8.5/ClaimCheck-v0.8.5-linux-arm64.tar.gz
tar xzf claimcheck.tar.gz
chmod +x ClaimCheck
sudo mv ClaimCheck /usr/local/bin/

# smoke test — should return JSON with [FEIN_1] and [EMAIL_1]
ClaimCheck sanitize-text "FEIN 12-3456789, contact jane@example.com"
```

Newer releases: same command, bump the version in the URL (asset names carry
the version).

## Vault encryption on the Pi

DPAPI is Windows-only. On Linux, set a password for portable AES-GCM encryption
at rest:

```bash
export SANITIZE_VAULT_PASSWORD='your-long-random-passphrase'
```

Without it the vault is plaintext JSON with `0600` perms — fine for a quick
test, not for an always-on appliance.

## The Pi use case: a LAN scrub proxy

ClaimCheck's strongest mode needs no agent cooperation: run the **proxy** on the
Pi and point your editor/agent's OpenAI-compatible Base URL at it. Everything on
the wire is scrubbed; the model server (local or cloud) sees placeholders only;
values are restored on the way back. A Pi idles at ~3 watts doing this all day.

`systemd` unit (`/etc/systemd/system/claimcheck-proxy.service`):

```ini
[Unit]
Description=ClaimCheck scrub proxy
After=network-online.target
Wants=network-online.target

[Service]
Environment=SANITIZE_VAULT_PASSWORD=change-me-long-random
ExecStart=/usr/local/bin/ClaimCheck --proxy --upstream http://YOUR-MODEL-BOX:8000/v1 --port 8766
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload && sudo systemctl enable --now claimcheck-proxy
ClaimCheck status
```

Point clients at `http://PI-IP:8766/v1`.

## What's fast and what's slow on a Pi

| Feature | On a Pi |
|---|---|
| CLI scrub / restore | instant — regex + validators, no ML |
| MCP server (`read_sanitized` / `scan_file` / `sanitize_text`) | great |
| Proxy core scrub | great — adds milliseconds |
| NER tier (opt-in, `SANITIZE_NER=1`) | works, but BERT on Pi CPU is slow — fine for agent-initiated reads, rough as an always-on proxy layer; the 50K-char cap bounds worst cases |
| Presidio sidecar | the analyzer container is amd64-only — run it on another box and set `SANITIZE_PRESIDIO_URL`; the proxy is fail-soft without it |

## Troubleshooting

- `exec format error` → 32-bit OS; reinstall 64-bit Raspberry Pi OS/Ubuntu.
- Crypto or startup aborts → `sudo apt install libssl3` (the .NET runtime links
  OpenSSL for parts of its crypto stack).
- MCP client on a different machine → the MCP server is stdio-based and runs
  next to the client; for remote machines use the proxy instead.
