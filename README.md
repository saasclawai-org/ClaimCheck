# ClaimCheck

**Check your PII at the door. Your LLM gets claim tickets.**

ClaimCheck is a local-first pseudonymization gate for LLM workflows. One self-contained
binary, two modes:

- **MCP server** — for Cline, Cursor, or any MCP client: the model reads files *through it*
  and receives scrubbed content with stable placeholders
- **LLM proxy** — point any OpenAI-compatible client at it: every request is scrubbed on the
  wire, every response restored before your editor sees it

The model works on `[FEIN_1]`, `[SSN_2]`, `[CLIENT_3]`-style claim tickets. You reclaim the
real values on the way out. The provider processes structure — never plaintext identity.

## Why

AI coding assistants are an exfiltration surface. Files, logs, and pasted context flow into
provider clouds as part of normal operation — occasionally without consent (see Zhipu's ZCode
incident, September 2026). Vendor promises — zero retention, no training — don't change the
basic fact: **plaintext crossed the wire.**

ClaimCheck flips it: scrub locally, so plaintext never enters the prompt at all. The provider
processes pseudonyms; your machine holds the identity map.

This is pseudonymization, not redaction. Redaction is entity-blind and one-way — the model
sees holes and the data is gone. ClaimCheck's placeholders are stable, distinguishable
(`[FEIN_1]` ≠ `[FEIN_2]`, and `[FEIN_1]` appearing in four places *means* something), and
reversible through a local vault — so tasks like cross-referencing, deduplication, and
document editing come out identical to running on raw data.

## How it works

```
 your editor (real values)
         │   read_sanitized / chat requests
         ▼
   [ ClaimCheck ]  ── scrub: identifiers → [FEIN_1], [SSN_2] …  ──▶  LLM provider
         ▲                                                                 │
         └─────────── restore: tickets → real values  ◀────────────────────┘

   vault: placeholder ⇄ original — local only, never transmitted
```

## Mode 1 — MCP server (stdio)

For Cline, Cursor, Claude Desktop, or any MCP client. Adds three tools:

| Tool | Behavior |
|---|---|
| `read_sanitized(path)` | file content with PII replaced by tickets + findings summary |
| `scan_file(path)` | findings summary only (types, counts, line numbers) — no content |
| `sanitize_text(text)` | scrub arbitrary text, returns sanitized version + summary |

Point your client at the binary (stdio transport):

```json
{
  "mcpServers": {
    "claimcheck": {
      "command": "/path/to/ClaimCheck",
      "args": []
    }
  }
}
```

Then tell your agent the rule — for Cline, a `.clinerules` file in the repo:

```
Any file that may contain personal data must be read with the ClaimCheck
read_sanitized tool, never with read_file. Never echo raw identifiers into chat.
```

## Mode 2 — LLM proxy (streamable HTTP / SSE)

A reverse proxy for OpenAI-compatible clients. Everything a request carries — file reads,
DB results, pasted logs, conversation history — is scrubbed at the wire, regardless of what
the model chose to read.

```bash
ClaimCheck --proxy --upstream https://api.example-provider.com/v1 --port 8766
```

Point your client's base URL at `http://127.0.0.1:8766/v1` and keep your real API key in the
client — auth passes through untouched. Streaming (SSE) is supported, including placeholders
split across chunk boundaries.

## What it catches

Built-in recognizers (regex + validators — deterministic, no ML, no downloads):

- Employer ID / FEIN (`XX-XXXXXXX`)
- SSN (with invalid-area guards)
- Email addresses
- US phone numbers
- Credit card numbers (Luhn-validated — random digit strings don't fire)
- IBAN
- AWS access keys
- GitHub tokens
- `sk-` / `sk_` API keys
- Slack tokens
- Encoded dumps (base64/hex) — decoded and masked before matching

Extend it with `.sanitize-patterns.json` in your project root — same contract, your domain:

```json
[{ "name": "MEMBER_ID", "regex": "\\bM[0-9]{8}\\b", "placeholder": "MEMBER" }]
```

## Tickets & the vault

Placeholders are stable per project: `[FEIN_1]` always maps back to the same value, so the
model can reason consistently across an entire session. The vault lives on your machine
(`~/.sanitize-mcp/vaults/`) and is never transmitted.

Restore locally whenever you need real values back:

```bash
ClaimCheck restore <file->        # or read from stdin with -
```

## NER tier — free-text names, orgs, locations (opt-in)

The regex tier can't see free-text names. ClaimCheck optionally runs a local English NER
model (quantized BERT ONNX, ~109 MB, downloaded once from HuggingFace) that catches
PERSON/ORG/LOC/MISC entities in prose and issues tickets for them:

```
Employer: Jane Doe, FEIN [FEIN_1]   →   Employer: [PERSON_1], FEIN [FEIN_1]
```

Enable with `SANITIZE_NER=1` in the environment of the ClaimCheck process. Tuning:
`SANITIZE_NER_THRESHOLD` (default 0.5), `SANITIZE_NER_MODEL` (any HuggingFace
token-classification ONNX repo). Model cache: `~/.claimcheck/models/`. Inference runs
locally (~1–3 s per call); subword pieces of one entity are merged into a single ticket
(the B-B quirk), and the config's label order is honored with a verified fallback.
Model licenses vary on HuggingFace — check the card of whichever model you point at.

Known quirk: subword tokenization can split one name into adjacent tickets
(`Satya Nadella` → `Sa` + `ty` + `a Nadella`, three PERSON tickets) — functionally
correct, cosmetically noisy. Merging adjacent same-type tickets is a roadmap item.

## Security model — the honest version

**Covered:** structured identifiers — the high-volume leak class. Everything above runs
locally; nothing phones home; the vault and the plaintext never leave your machine.

**Not covered (yet):** free-text names and addresses need NER — regex can't see them.
Roadmap: label-anchored heuristics, then local NER (Presidio-compatible recognizers).

**Enforcement layers, strongest first:** the proxy mode scrubs everything on the wire
(regardless of agent behavior); the MCP tools scrub what the agent chooses to read (needs a
rules file like the one above). Use the proxy when the provider is a cloud you don't trust
with plaintext; the MCP mode is enough on its own for local models.

**Known scope:** chat-completions requests are scrubbed in proxy mode; other endpoints pass
through. Binary files are refused, not scrubbed.

## Build & test

Requires .NET 9.

```bash
dotnet build -c Release
python3 test_dotnet.py       # MCP tools: scrub fidelity, no-leak, vault, restore
python3 test_proxy.py        # proxy: scrub-on-wire, restore-on-response, SSE split handling
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64
```

Cross-compiles from Linux/macOS to Windows (and vice versa) — the publish output is a single
self-contained executable.

## Roadmap

- [x] Encoded-output scrubbing — base64/hex dumps decoded and masked before matching
- [x] Fail-closed proxy posture (`SANITIZE_FAIL_CLOSED=0` to disable)
- [ ] Label-anchored heuristics (names after `Contact:`, `Owner:` …)
- [x] Local NER tier for free-text names/addresses (`SANITIZE_NER=1`, local ONNX model)
- [ ] Hashed-identifier columns (peppered HMAC) for database lookups
- [ ] Pluggable recognizer packs
- [ ] OS-keychain / DPAPI vault encryption
- [ ] OCR pass for scanned documents

## License

[MIT](LICENSE) — Copyright (c) 2026 Norman Moore
