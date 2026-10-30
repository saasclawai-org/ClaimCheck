# Configuration reference

ClaimCheck is zero-config out of the box: the deterministic pattern tier (FEIN, SSN,
emails, cards, keys, tokens, PEM blocks, …) is always on, and every optional tier is
opt-in via environment variables. Nothing phones home; the only network traffic the
tool itself makes is the optional NER model download and the optional Presidio backend.

**Environment variables are read once at process start.** Restart the proxy service
(or re-open the MCP session) after changing any of them.

## Scrub core

| Variable | Default | Purpose |
|---|---|---|
| `SANITIZE_DATES` | off (v0.9.2) | Dates are operational context (deadlines, notice dates) — ticketing them made the model unable to answer "which deadline is earlier?" Set `=1` when your workflow treats dates as sensitive. Existing `DATE_*` tickets in old vaults still restore. |
| `SANITIZE_FAIL_CLOSED` | **on** | On failures the gate refuses (clean 502s / hard errors) rather than passing content through. `=0` disables — don't. |
| `SANITIZE_ROOTS` | empty = unrestricted | Path allowlist for file reads and restores; violations throw `path outside SANITIZE_ROOTS allowlist`. Separator is platform-native (`:` on Unix, `;` on Windows). This is the fleet-lane lockdown knob. |
| `SANITIZE_VAULT_PASSWORD` | unset | Vault at-rest protection: unset = DPAPI (Windows, tied to login) or plaintext + 0600 (Linux). Set = portable AES-GCM password mode (PBKDF2-SHA256 210k). Forgotten password = mappings unrecoverable. |

## NER tier (opt-in, off by default)

Local English NER model (ONNX) that catches free-text names/orgs/locations the regex
tier can't see. Off by default — the regex tier covers all labeled values; NER adds
free-text detection at linear inference cost.

| Variable | Default | Purpose |
|---|---|---|
| `SANITIZE_NER` | off | `=1` (or `on`/`true`) enables. Downloads a ~110 MB model on first use to `~/.claimcheck/models/`. |
| `SANITIZE_NER_TYPES` | all four | Comma-separated whitelist of PERSON/ORG/LOC/MISC — e.g. `ORG,LOC` lets person-names pass through untouched. Short model forms (`PER`) accepted. Unset = all four; an entry naming nothing valid scrubs nothing (fail-closed). Also governs known-value re-ticketing for those types. |
| `SANITIZE_NER_THRESHOLD` | 0.5 | Global confidence floor (clamped 0.05–0.99). |
| `SANITIZE_NER_THRESHOLD_<TYPE>` | global | Per-type floor, e.g. `SANITIZE_NER_THRESHOLD_PERSON=0.9`. |
| `SANITIZE_NER_MODEL` | `Xenova/bert-base-NER` | Any HuggingFace token-classification ONNX repo. Model licenses vary — check the card. |
| `SANITIZE_NER_MAX_CHARS` | 200,000 | NER is skipped above this input length (regex tier only). |
| `SANITIZE_NER_DEBUG` | off | `=1` logs per-token/per-entity decisions to stderr. |

## Gazetteer (opt-in, off by default)

| Variable | Default | Purpose |
|---|---|---|
| `SANITIZE_GAZETTEER` | off | `=1` loads the 250k-name gazetteer for name detection. Opt-in: it shipped disabled after corruption-class false positives (repro in code). |

## Presidio backend (opt-in, off by default)

Bring-your-own detector: ClaimCheck calls a Presidio analyzer service as an additional,
tier-separated detection backend — findings are labeled by entity type and never
probabilistically restored. Fail-soft: if the service is down, the tier is skipped.

| Variable | Default | Purpose |
|---|---|---|
| `SANITIZE_PRESIDIO_URL` | unset = off | e.g. `http://127.0.0.1:3000` (the presidio-analyzer container's default port). |
| `SANITIZE_PRESIDIO_ENTITIES` | all | Comma-separated entity-type filter for Presidio findings. |
| `SANITIZE_PRESIDIO_SCORE` | 0.4 | Confidence floor (clamped 0–1). |
| `SANITIZE_PRESIDIO_MAX_CHARS` | 50,000 | Presidio skipped above this input length. |

## Output modes

| Variable | Default | Purpose |
|---|---|---|
| `SANITIZE_FAKE_DATA` | off | `=1` realistic mode: instead of opaque tickets the model sees deterministic per-type fakes (SSN 9XX-00-XXXX, FEIN 00-XXXXXXX, `@claimcheck.invalid`, Luhn-valid cards) — restore swaps fakes back to real values. |
| `SANITIZE_PROXY_LOG` | off | `=1` audit log: post-scrub bodies appended as JSON lines — tickets only, never raw values. |

## Environment (load-bearing, not optional)

- `HOME` — resolves the vault directory (`~/.sanitize-mcp/vaults`) and the model cache
  (`~/.claimcheck/models`). Since v0.9.2 resolution is fail-closed: a process without a
  resolvable home refuses to start rather than writing vaults into its working directory.
  Systemd units must set `Environment=HOME=/home/USER` explicitly.
- `CC_BIN` — test override: binary the Python suites invoke (default `dotnet bin/Release/net9.0/ClaimCheck.dll`).

## CLI flags

```
ClaimCheck status
ClaimCheck vault-list [--vault <path>] [--summary]
ClaimCheck sanitize-text <text>          # or read from stdin
ClaimCheck sanitize-file <path> [--content]
ClaimCheck import-clients <file>
ClaimCheck restore <file|-> [--bare] [--vault <path>]
ClaimCheck --proxy --upstream <base-url> [--port 8766] [--host 127.0.0.1] [--vault <path>]
```

Proxy defaults: loopback-only bind, port 8766, vault at `~/.sanitize-mcp/vaults/proxy.json`.
Only OpenAI-compatible `chat/completions` POSTs are scrubbed/restored; every other path is
forwarded verbatim.
