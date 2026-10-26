# ClaimCheck proxy — deployment guide

How to run the scrubbing proxy against real providers, single instance or a
multi-lane fleet. Assumes v0.7.4+.

## The coverage rule (read this first)

**Only `POST` requests ending in `chat/completions` are scrubbed (outbound)
and restored (inbound).** Every other path — GETs, embeddings, image endpoints,
native provider APIs — is forwarded verbatim: no scrub, no restore.

Consequence: reach every provider through an **OpenAI-compatible
`/chat/completions` surface**. Specifically:

- Anthropic's native `/v1/messages` is **not** scrubbed. Use their
  [OpenAI compatibility layer](https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk)
  (`https://api.anthropic.com/v1/`) instead.
- OpenAI's newer Responses API (`/v1/responses`) is **not** scrubbed. Keep
  clients on `/v1/chat/completions`.
- Streaming (SSE) chat responses are restored mid-stream, including
  placeholders split across chunk boundaries.

## Provider surfaces

| Provider | `--upstream` value | Client base URL |
|---|---|---|
| Local vLLM / Ollama / LM Studio | `http://127.0.0.1:8008` | `http://127.0.0.1:18008` |
| Z.ai (coding plan) | `https://api.z.ai/api/coding/paas/v4/` | `http://127.0.0.1:18009/api/coding/paas/v4` |
| OpenAI | `https://api.openai.com/v1/` | `http://127.0.0.1:18010/v1` |
| Claude (OpenAI-compat layer) | `https://api.anthropic.com/v1/` | `http://127.0.0.1:18011/v1` |
| OpenRouter (many providers, one key) | `https://openrouter.ai/api/v1/` | `http://127.0.0.1:18012/v1` |

Port convention: proxy ports simply ascend as lanes are added (18008, 18009, …) — each lane's real values live in its `lane-<name>.env` file, so the table is illustrative, not fixed.

API keys live in the client, as normal — the `Authorization` header forwards
untouched and the proxy stores no credentials. The vault stays on the proxy
machine.

## Single instance (quickstart)

```bash
ClaimCheck --proxy --upstream https://api.openai.com/v1/ --port 18770
```

Point the client's base URL at `http://127.0.0.1:18770/v1`. Both client
base-URL shapes work — base with the provider path or bare origin — because a
duplicated path prefix is stripped once before composing (see below).

## Path composition

The proxy composes the client's request path onto the upstream URL:

- If the client repeats the upstream's own path prefix, the duplicate is
  stripped once (so `…/api/coding/paas/v4` + `/api/coding/paas/v4/chat/completions`
  lands on one prefix, not two).
- A leading `/v1` on the client path is stripped **only when the upstream URL
  itself carries a path prefix** (legacy zai-shape upstreams). Root upstreams —
  vLLM, Ollama, OpenAI-style lanes — keep the client path verbatim, since they
  serve `/v1/...` themselves. (Before v0.7.4 the strip was unconditional and
  broke root upstreams; if chat POSTs 404 while `GET /v1/models` works, you are
  running a pre-0.7.4 build.)

`SANITIZE_PROXY_LOG=1` prints one stderr line per request (method, path,
upstream status, ms) — the first tool to reach for when a client and the proxy
disagree about URLs.

## Multi-lane fleet (systemd template)

One proxy process per upstream; each with its own vault namespace.

`/etc/systemd/system/claimcheck-proxy@.service`:

```ini
[Unit]
Description=ClaimCheck proxy lane %i
After=network-online.target

[Service]
User=you
EnvironmentFile=/home/you/.claimcheck/common.env
EnvironmentFile=/home/you/.claimcheck/lane-%i.env
ExecStart=/home/you/claimcheck/ClaimCheck --proxy --upstream ${UPSTREAM} --port ${PROXY_PORT} --vault /home/you/.claimcheck/vault-%i.json
Restart=on-failure
RestartSec=3
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
```

`common.env` (chmod 600 — shared secrets; plain `KEY=value` lines, no `export`,
no quotes, no `#` inside values):

```
SANITIZE_VAULT_PASSWORD=<long random passphrase>
DOTNET_BUNDLE_EXTRACT_BASE_DIR=/home/you/.claimcheck/extract
SANITIZE_PROXY_LOG=1
```

One two-line file per lane (`lane-zai.env`, `lane-openai.env`, …) with
`UPSTREAM=` and `PROXY_PORT=`, then:

```bash
sudo systemctl enable --now claimcheck-proxy@zai claimcheck-proxy@openai
```

Vaults are per-instance namespaces (`vault-zai.json`, `vault-openai.json`, …):
placeholders minted by one lane restore only through that lane. Restarts are
independent; `Restart=on-failure` self-heals.

Without `SANITIZE_VAULT_PASSWORD` the vault is **plaintext JSON on Linux/macOS**
(Windows uses DPAPI automatically). On a server, set it — then re-mint: delete
the existing vault file once and let the next request recreate it encrypted.

## Updating a deployed proxy

The single-file binary cannot be overwritten while the service is running it
(`Text file busy`). Stop → publish → start:

```bash
sudo systemctl stop claimcheck-proxy@8008
dotnet publish -c Release -f net9.0 -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o ~/claimcheck
sudo systemctl start claimcheck-proxy@8008
```

The `-f net9.0` flag is required — the project targets multiple frameworks and
publish fails without it (`NETSDK1129`).

## Verifying a lane (the reversal probe)

Send a fake identifier and ask the model to spell it backwards:

```bash
curl -s http://127.0.0.1:18008/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"model":"MODEL_ID","messages":[{"role":"user","content":"Client FEIN: 04-2105393 - spell it backwards digit by digit. If you cannot, say exactly what you received instead."}]}'
```

- **Pass**: the model says it only received `[FEIN_1]` and cannot spell digits.
  If it echoes the bracketed token, restore replaces it with the real value on
  the way back — that is the restore path working, not a leak.
- **Fail**: the model spells `3-9-0-…` — raw data crossed the wire.
- If chat POSTs 404 while `GET /v1/models` works, see *Path composition* above
  (pre-0.7.4 build or mismatched upstream path).

## Exposing a lane beyond localhost

The proxy has **no authentication of its own** — anyone who can reach the port
can use it. When exposing through a tunnel (Cloudflare Tunnel, WireGuard, …):

1. Put the hostname behind an access gate (e.g. Cloudflare Access service
   tokens) before anything else.
2. Remember the trust boundary: scrubbing happens **at the proxy**, so the leg
   from client to proxy carries plaintext. A tunnel does not change that — it
   only protects the lane from unauthenticated use.
3. If the data originates on the client machine and must not leave it, run the
   proxy **on the client** instead (upstream = the remote lane), not server-side.
