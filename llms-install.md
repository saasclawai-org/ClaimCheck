# ClaimCheck — agent install guide (`llms-install.md`)

ClaimCheck is a local-first PII pseudonymization tool for .NET 9. With **no
arguments** it runs as an MCP stdio server (JSON-RPC on stdout — nothing else
is ever written there). Tools: `read_sanitized`, `scan_file`, `sanitize_text`.

## Option A — release binary (Windows, fastest)

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
2. Configure your MCP client:

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

## Restoring original values (local only)

CLI: `ClaimCheck restore <file|->` maps placeholders back to real values.
The vault lives under the user profile directory (`.claimcheck`) and never
leaves the machine.

## Recognized by default

FEIN, SSN, email, phone, credit cards (Luhn-checked), IBAN, AWS access/secret
keys, GitHub tokens (ghp_/gho_/github_pat_), Slack tokens, generic API keys
(sk-, AKIA...). Add your own via `.sanitize-patterns.json` — see README.

## Privacy

No telemetry, no cloud calls. The NER model download is the only network
fetch, and only when you opt in. Everything else is local.
