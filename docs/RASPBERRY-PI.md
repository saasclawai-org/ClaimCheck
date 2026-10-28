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
User=pi
WorkingDirectory=/home/pi
Environment=SANITIZE_VAULT_PASSWORD=change-me-long-random
# optional — on-device analyzer (see Presidio section below):
Environment=SANITIZE_PRESIDIO_URL=http://127.0.0.1:5001
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

**The proxy binds `127.0.0.1` only** — deliberate, it has no authentication. To
serve other machines on the LAN, add a forwarder:

```bash
sudo apt install -y socat
sudo tee /etc/systemd/system/claimcheck-forward.service >/dev/null <<'EOF'
[Unit]
Description=ClaimCheck LAN forwarder
After=claimcheck-proxy.service
Requires=claimcheck-proxy.service

[Service]
ExecStart=/usr/bin/socat TCP-LISTEN:8767,fork,reuseaddr TCP:127.0.0.1:8766
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF
sudo systemctl daemon-reload && sudo systemctl enable --now claimcheck-forward
```

Then point clients at `http://PI-IP:8767/v1`. **LAN only** — never port-forward
8766/8767 to the internet. Want zero LAN exposure instead? An SSH tunnel from
the client machine (`ssh -N -L 8766:127.0.0.1:8766 pi@PI-IP`, then Base URL
`http://127.0.0.1:8766/v1`) keeps the proxy loopback-only.

`User` + `WorkingDirectory` matter: they anchor the service to your home
directory so the proxy shares the same vault and `.sanitize-patterns.json` as
your CLI runs.

### Cloud providers as upstream

`YOUR-MODEL-BOX` doesn't have to be a box on the LAN — a cloud provider works
the same way. The rule: `--upstream` is the provider's API base, and clients
use the proxy plus the same path:

| Provider | `--upstream` | Client Base URL |
|---|---|---|
| Z.ai (GLM) | `https://api.z.ai/api/coding/paas/v4` | `http://raspberrypi.local:8767/api/coding/paas/v4` |
| OpenAI | `https://api.openai.com/v1` | `http://raspberrypi.local:8767/v1` |
| OpenRouter | `https://openrouter.ai/api/v1` | `http://raspberrypi.local:8767/v1` |

(`raspberrypi.local` = the default hostname via mDNS — resolves from Windows,
macOS and most Linux desktops. `docs/PROXY.md` has the full provider-by-provider
list, including Claude's compat layer and local vLLM.)

For Cline this is just the Base URL swap: the API key stays in Cline and passes
through untouched — the Pi holds no credentials. Only chat-completions requests
are scrubbed, which is exactly what Cline sends on any OpenAI-compatible
provider.

**One lane = one provider.** The proxy mirrors a single upstream; to scrub a
second cloud at the same time, run another instance on a different port with
its own `--vault` (same unit file, three changed lines: port, upstream, vault
path).

That's the flagship topology: code and context scrubbed **on the LAN**, tickets
crossing the internet, real values restored before the editor ever sees the
response.

## What's fast and what's slow on a Pi

| Feature | On a Pi |
|---|---|
| CLI scrub / restore | instant — regex + validators, no ML |
| MCP server (`read_sanitized` / `scan_file` / `sanitize_text`) | great |
| Proxy core scrub | great — adds milliseconds |
| NER tier (opt-in, `SANITIZE_NER=1`) | works, but BERT on Pi CPU is slow — fine for agent-initiated reads, rough as an always-on proxy layer; the 50K-char cap bounds worst cases |
| Presidio sidecar | **runs on-device** via the community multi-arch image (below) — verified end-to-end; or point `SANITIZE_PRESIDIO_URL` at a LAN box; fail-soft without it |

## Presidio on-device (community multi-arch image)

The official `mcr.microsoft.com/presidio-analyzer` image is amd64-only. The
community `ghcr.io/data-privacy-stack` images are multi-arch and run on the Pi:

```bash
docker run -d --name presidio-analyzer -p 127.0.0.1:5001:3000 \
  ghcr.io/data-privacy-stack/presidio-analyzer:latest
export SANITIZE_PRESIDIO_URL=http://127.0.0.1:5001
```

Silicon-verified 2026-10-28: PERSON / ORG detection flows end-to-end through the
analyzer, on-device, from a cold start. Two caveats from that same test:

- **Narrower recognizer set than the official image** — a plain CRYPTO wallet
  address passed through unmasked. Probe what your image actually recognizes:

  ```bash
  curl -s http://127.0.0.1:5001/analyze -H 'Content-Type: application/json' \
    -d '{"text":"wallet 0x71C7656EC7ab88b098defB751B7401B5f6d8976F","language":"en","score_threshold":0}'
  ```

  Or cover the gap deterministically with a `.sanitize-patterns.json` pattern in
  the project root — built-in tier, no container needed:

  ```json
  [
    { "name": "CRYPTO_WALLET", "regex": "\\b0x[a-fA-F0-9]{40}\\b" }
  ]
  ```
- **Non-stock entity names** — this build emits `ORG_ENTITY` where stock Presidio
  emits `ORGANIZATION`. Harmless: ClaimCheck tickets whatever arrives, and
  `[ORG_ENTITY_1]` restores normally.

## Troubleshooting

- `exec format error` → 32-bit OS; reinstall 64-bit Raspberry Pi OS/Ubuntu.
- Crypto or startup aborts → `sudo apt install libssl3` (the .NET runtime links
  OpenSSL for parts of its crypto stack).
- MCP client on a different machine → the MCP server is stdio-based and runs
  next to the client; for remote machines use the proxy instead.
