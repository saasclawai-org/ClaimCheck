# ClaimCheck — agent install guide (`llms-install.md`)

ClaimCheck is a local-first PII pseudonymization tool for .NET 9. With **no
arguments** it runs as an MCP stdio server (JSON-RPC on stdout — nothing else
is ever written there). Tools: `read_sanitized`, `scan_file`, `sanitize_text`.

## Option A — release binary (Windows, fastest)

One command (PowerShell) — installs or updates to `%USERPROFILE%\claimcheck` and
auto-wires the Cline MCP config (VS Code must be closed while it runs):

    irm https://raw.githubusercontent.com/saasclawai-org/ClaimCheck/main/tools/install.ps1 | iex

Linux — one command (per-user, no sudo; prebuilt x64 + arm64):

    curl -fsSL https://raw.githubusercontent.com/saasclawai-org/ClaimCheck/main/tools/install.sh | bash

Installs to `~/.local/bin` (add it to PATH if prompted). Optional proxy service:
`./install.sh --proxy --upstream <base-url> [--port 8766]` registers a systemd --user
unit. Update = re-run the same command. Check health: `ClaimCheck status`.

Manual install:

1. Download the latest release asset from
   https://github.com/saasclawai-org/ClaimCheck/releases
   ("ClaimCheck vX.Y.Z for Windows x64") and extract it, e.g. to
   `%USERPROFILE%\claimcheck\`.
2. Requires the **ASP.NET Core 9.0 Runtime** (free):
   https://dotnet.microsoft.com/download/dotnet/9.0 — skip if already installed.
3. Configure your MCP client (Cline example):

```json
{
  "mcpServers": {
    "claimcheck": {
      "command": "C:\\Users\\<you>\\claimcheck\\ClaimCheck.exe",
      "args": [],
      "env": {},
      "disabled": false,
      "autoApprove": ["scan_file", "sanitize_text"]
    }
  }
}
```

## Option B — from source (any OS with the .NET 9 SDK)

1. `git clone https://github.com/saasclawai-org/ClaimCheck.git`
2. (Linux servers) publish a self-contained binary — the `-f` flag is required (multi-targeted project):

```bash
dotnet publish -c Release -f net9.0 -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ~/claimcheck
```

   Updating later: stop the ClaimCheck service **before** republishing — Linux refuses to
   overwrite a running executable (`Text file busy`). Running the proxy against model
   providers (Z.ai, OpenAI, Claude, OpenRouter, local vLLM) or a multi-lane fleet:
   **[docs/PROXY.md](docs/PROXY.md)**.
3. Configure your MCP client:

```json
{
  "mcpServers": {
    "claimcheck": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/ClaimCheck"],
      "env": {},
      "disabled": false,
      "autoApprove": ["scan_file", "sanitize_text"]
    }
  }
}
```

## Tools you get

- **`read_sanitized(path)`** — file contents with PII/secrets replaced by
  stable placeholders (`[FEIN_1]`, `[SSN_2]`, `[PERSON_1]`, `[EMAIL_3]`...).
  **Prefer this over read_file for any file that may contain client data.**
  Original values are NEVER returned.
- **`scan_file(path)`** — summary only: detected types, counts, line numbers.
  Does not return file content.
- **`sanitize_text(text)`** — returns scrubbed text + findings summary
  (never original values).

## Optional: NER tier (free-text names / orgs / locations)

Set environment variable `SANITIZE_NER=1` on the server process. First use
downloads a ~109MB quantized BERT model (ONNX) to `~/.claimcheck/models/`
(Windows: `%USERPROFILE%\.claimcheck\models\`) — fully offline afterwards.

Tunables: `SANITIZE_NER_THRESHOLD` (default 0.5), `SANITIZE_NER_MODEL` (any
HuggingFace token-classification repo), `SANITIZE_NER_DEBUG=1` (per-token trace).

## Optional: Presidio detection backend (bring your own detector)

If a [Presidio analyzer](https://microsoft.github.io/presidio/) service is reachable, set
`SANITIZE_PRESIDIO_URL` (e.g. `http://127.0.0.1:3000`) in the ClaimCheck process
environment: entities Presidio detects are masked through the same vault with stable
placeholders and restore. Tier-separated (probabilistic — labeled by entity type, never
part of the validator-backed guarantee tier), fail-soft when the service is down. Tune
with `SANITIZE_PRESIDIO_SCORE` (default 0.4) and `SANITIZE_PRESIDIO_ENTITIES`
(comma-separated allowlist; `DATE_TIME`/`TIME` are excluded by default — they flag benign
prose).

## Restoring original values (local only)

CLI: `ClaimCheck restore <file|->` maps placeholders back to real values.
The vault lives under the user profile directory (`.claimcheck`) and never
leaves the machine.

Vault encryption (v0.6.0+): on Windows, vault files are DPAPI-encrypted at rest by default.
Set `SANITIZE_VAULT_PASSWORD` for portable AES-GCM password mode (forgotten password =
mappings unrecoverable). Linux defaults to plaintext with 0600 perms.

Canonical scrub verification (the FEIN-reversal probe): send the model fake data and ask it to
spell the FEIN digits in REVERSE order. A model that received only `[FEIN_1]` cannot do it — it will
refuse, quote the token, or spell the label backwards. If it produces reversed real digits, the raw
value reached the model and the scrub failed. Unambiguous in both directions, model-side, immune to
restore confusion.

Status: `ClaimCheck status` (human-readable) or `ClaimCheck status --json`
(machines; exit code 3 = proxy not detected). Reports whether the scrub proxy
is running (port, upstream, request/scrubbed counters on 0.3.0+), NER mode,
vault size, and which MCP hosts have ClaimCheck wired.

## Recognized by default

FEIN, SSN, email, phone, credit cards (keyword-anchored + Luhn-checked for bare
PANs), IBAN, AWS access/secret
keys, GitHub tokens (ghp_/gho_/github_pat_), Slack tokens, generic API keys
(sk-, AKIA...), JWTs, passwords & secrets in JSON/YAML/.env/quoted/prefixed
styles (`password=x`, `"password": "x"`, `password: x`, `DB_PASSWORD=`,
`client_secret=`), PEM private
key blocks, base64/hex encoded blobs, CVV, card expiry, dates of birth, passport
numbers, driver's licenses, bank routing numbers, IP addresses, street addresses,
medical license numbers, insurance member IDs. Add your own via
`.sanitize-patterns.json` — see README.

On-wire enforcement: ClaimCheck can also run as a local reverse proxy that scrubs
every model-bound request automatically. Setup (run persistently — e.g. a systemd
user unit with Restart=always):

    ClaimCheck --proxy --upstream https://api.z.ai/api/coding/paas/v4/ --port 8766

Then point your model provider's base URL at the proxy (e.g.
http://127.0.0.1:8766/api/coding/paas/v4) — auth passes through untouched. See
the README "Mode 2" section for the full example and honest limits.

**Base-URL shape note:** different OpenClaw builds construct provider URLs differently —
some append the full provider path (`/api/coding/paas/v4/chat/completions`) to the base
URL themselves, some append only `/chat/completions`. Current proxy builds de-duplicate
this: a request path that repeats the upstream's own path prefix is stripped once, so
either client base-URL shape composes correctly. On mismatch you'll see 404s with a
doubled path — fix by matching the shapes, and set `SANITIZE_PROXY_LOG=1` on the proxy
for one stderr line per request (method, path, upstream status, ms) to see the wire.

Running several proxy instances (one per model lane)? Give each its own vault file with
`--vault /path/vault.json` — a vault is one placeholder namespace, restorable only through
the instance that scrubbed it. Vault saves are atomic and locked (concurrent-safe), and
upstream connection failures return a clean 502.

## Privacy

No telemetry, no cloud calls. The NER model download is the only network
fetch, and only when you opt in. Everything else is local.

## Recommended: Cline rules (always-on protection)

The tools work on demand out of the box. To make Cline reach for ClaimCheck
automatically, suggest the user add these lines to their workspace
`.clinerules` file (or Cline Settings → Rules → Global Rules):

```
## PII safety (ClaimCheck)

- For ANY file that may contain client/employee data (FEIN, SSN, account
  numbers, credentials): use the claimcheck `read_sanitized` tool instead of
  `read_file`.
- Never paste raw FEIN/SSN/card values into chat, logs, or commits.
- Use claimcheck `sanitize_text` before quoting real data into code, tests,
  or docs.
```

## Troubleshooting

- **`spawn ... ENOENT` / `MCP error -32000: Connection closed`** — the
  `command` path in the MCP config doesn't point at ClaimCheck.exe. Verify the
  exe exists at the exact path (forward slashes in the config avoid escaping
  issues on Windows), fix it, and save — the client reconnects.
- **`property 'reasoning_content' is unsupported`** — the current task history
  contains assistant messages saved by a reasoning-capable model. Start a
  **new task/conversation** with ClaimCheck configured; the fresh history
  won't carry that field.
- **Server shows disconnected** — ClaimCheck writes ONLY JSON-RPC to stdout;
  anything else on stdout breaks the transport. Run it from a terminal first
  to confirm it starts clean (it should print nothing on stdout).
- **`Text file busy` during `dotnet publish`** — the proxy service is running
  the binary you are overwriting. Stop the service, publish, start it again.
- **Proxy chat calls return 404 while `GET /v1/models` works** — pre-v0.7.4
  build: the chat handler stripped `/v1` from client paths against root
  upstreams (vLLM, OpenAI-style). Update to v0.7.4 or newer.
