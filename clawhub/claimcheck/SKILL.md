---
name: claimcheck
description: Sanitize PII/secrets (FEIN, SSN, names, credentials) from text or files with the local ClaimCheck CLI before anything leaves the machine; reversible via the local vault.
version: 1.0.6
metadata:
  openclaw:
    emoji: "🛡️"
    homepage: https://claimcheck.saasclaw.ai
    envVars:
      - name: SANITIZE_NER
        required: false
        description: Off by default. Set to 1 to enable the NER tier (free-text names/orgs/locations; ~109MB model cached on first use).
      - name: SANITIZE_VAULT_PASSWORD
        required: false
        description: Set to a password to encrypt vault files at rest (AES-GCM). On Windows, vaults are DPAPI-encrypted by default without it.
---

# ClaimCheck — local PII pseudonymization

ClaimCheck replaces sensitive values with stable, reversible placeholders
(`[FEIN_1]`, `[SSN_2]`, `[PERSON_1]`...) and keeps the mapping in a local vault.
Nothing leaves the machine — no account, no API key, no network calls.

## Install (one-time)

Windows PowerShell (installs to %USERPROFILE%\claimcheck\ClaimCheck.exe):

    irm https://raw.githubusercontent.com/saasclawai-org/ClaimCheck/main/tools/install.ps1 | iex

Linux (per-user, no sudo; x64 + arm64 prebuilt):

    curl -fsSL https://raw.githubusercontent.com/saasclawai-org/ClaimCheck/main/tools/install.sh | bash

Proxy as a systemd --user service: download the script and run
`./install.sh --proxy --upstream <base-url> [--port 8766]`.

Repo: https://github.com/saasclawai-org/ClaimCheck

## When to use (always for client-adjacent data)

- Before quoting any file's contents into chat, tickets, docs, emails, or commits
  when it may contain client data (FEIN, SSN, account numbers, names, credentials)
- Before sharing sample data — share the sanitized sample instead
- To CHECK a file for PII without reading it: sanitize-file <path> (no --content)

## Commands

  ClaimCheck sanitize-text              # text from stdin → JSON {summary, findings, sanitized}
  ClaimCheck sanitize-text "some text"  # text as argument
  ClaimCheck sanitize-file <path>       # file → summary only (no content)
  ClaimCheck sanitize-file <path> --content
  ClaimCheck restore <file|->           # placeholders → real values (local vault only)
  ClaimCheck status                     # proxy running? NER? vault? MCP wiring (--json for machines)

On Windows the binary is %USERPROFILE%\claimcheck\ClaimCheck.exe (or your custom
InstallDir). Recognized: FEIN, SSN, email, phone, cards (Luhn), IBAN, AWS/GitHub/Slack
keys, API keys, JWTs, connection-string passwords, PEM private-key blocks, base64/hex
encoded blobs, CVV, card expiry, dates of birth, passport numbers, driver's licenses,
bank routing numbers, IP addresses, street addresses, medical license numbers,
insurance member IDs. Custom patterns: .sanitize-patterns.json (see repo README).

## Rules

1. Share ONLY the sanitized field — findings show placeholders, never values.
2. Never paste raw FEIN/SSN/card/credential values into chat, logs, or commits.
3. The vault lives in ~/.sanitize-mcp/vaults (plaintext, local). Treat the machine
   as the trust boundary.
4. NER tier (free-text names/orgs/locations): on by default; SANITIZE_NER=0 disables — model
   (~109MB) cached in ~/.claimcheck/models.
5. Not 100% — verify sanitized output before high-stakes sharing (DISCLAIMER.md in
   the repo).
