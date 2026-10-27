# ClaimCheck vs Presidio

[Presidio](https://microsoft.github.io/presidio/) (Microsoft, MIT) is the most widely
deployed open-source PII detection framework. This page documents how ClaimCheck
relates to it — including the part where they stack.

## TL;DR

**Presidio is a detection toolkit for building custom pipelines. ClaimCheck is a
finished enforcement layer for AI-agent traffic, with its own deterministic detection
tier — and since v0.8.0 it can additionally use a live Presidio service as a
detection backend.** They answer different questions and can run together.

## A developer's PII problem is four problems

### 1. Detection — knowing what's sensitive

Presidio is strong on classic document PII (names, SSNs, phones, addresses) across
many countries. What leaks from developer and agent work is a different class:
JWTs, PEM blocks, connection strings, `.env` assignments, base64 blobs, and
label-anchored credentials (`DB_PASSWORD=…`, `client_secret=…`). Core Presidio
doesn't ship that class — you'd write custom recognizers for every format that
actually leaks from code and configs. ClaimCheck's deterministic tier is built
around it: 26 recognizers including label-anchored passwords/secrets, JWTs,
connection strings, PEM blocks, and base64/hex blob decoding.

### 2. Enforcement position — being in the path of every byte that leaves

Presidio is a library you *call*. The dominant failure mode of every PII tool is the
human path around it: deadline pressure, paste into chat, forget the wrapper.
Presidio ships no AI-wire enforcement — no chat-completions scrubbing, no SSE-safe
restore, no fail-closed posture. ClaimCheck's proxy *is* that enforcement point:
every model-bound request is scrubbed before the provider sees it, and responses are
restored on the way back — including placeholders split across streaming chunk
boundaries. Scrub failure holds the request (fail-closed) instead of forwarding raw.

### 3. Workflow preservation — the model needs the shape of the data

Presidio's anonymizer destroys by default: redact, replace, hash. Output like
`FEIN: <REDACTED>` means the model can't reason about *this client's* FEIN — the
task degrades, and the developer concludes "PII tools break my work" and turns them
off. ClaimCheck masks to stable, typed placeholders (`[FEIN_1]`, `[PERSON_2]`) so
the model keeps working over redacted data.

### 4. Reversibility + session integrity

ClaimCheck's vault gives the operator local, one-command restore (`ClaimCheck
restore`) and per-session referential integrity: `[FEIN_1]` always means the same
client within a session, and the same value maps to the same ticket across restarts
in the same vault. Presidio's reversible path is DIY — encrypt with your own key and
manage ciphertext blobs; there are no stable, readable tokens and no vault.

See [PLACEHOLDERS.md](PLACEHOLDERS.md) for the full ticket semantics.

## What Presidio does well — the honest list

- **Breadth**: 30+ entity types across many countries (IN_PAN, SG_NRIC, ES_NIF, …)
- **Languages**: multi-language NER via spaCy, transformers, Stanza engines
- **Maturity**: Microsoft-backed, widely deployed, battle-tested at scale
- **Modules**: image and DICOM redaction, analyzer service with monitoring
- **Integration depth**: custom operators and lambdas in-process for Python teams

For a Python shop with data-science resources building a bespoke compliance
pipeline, it's a solid foundation. That's a different job than ClaimCheck's.

## Stackable, not rivals: the Presidio detection backend

Since v0.8.0, ClaimCheck can use a live Presidio analyzer as an **optional,
tier-separated detection backend**. Set `SANITIZE_PRESIDIO_URL` (e.g. the official
`presidio-analyzer` container on :3000) and entities Presidio detects —
multi-language NER, crypto wallets, country IDs — are masked through the same vault
with stable placeholders and restore. Tunables: `SANITIZE_PRESIDIO_SCORE` (default
0.4) and `SANITIZE_PRESIDIO_ENTITIES` (allowlist; `DATE_TIME`/`TIME` are excluded by
default — NER date flags on benign prose are a known false-positive class).

Presidio findings live in the **probabilistic tier**: labeled by entity type, never
part of the validator-backed guarantee tier, and the pass is skipped fail-soft when
the service is unreachable. The deterministic tiers are unaffected.

See [PROXY.md](PROXY.md) for the proxy deployment guide and the coverage rule
(only `POST …/chat/completions` traffic is scrubbed and restored).

## Summary

| | Presidio | ClaimCheck |
|---|---|---|
| Shape | Python detection library | Single self-contained binary (proxy + MCP + CLI) |
| Detection | Broad, multi-country, multi-language (NER + patterns) | Deterministic, validator-backed, dev-secret-first (26 types) + gazetteer; optional NER |
| Enforcement position | None — you build it | Chat-completions wire proxy, fail-closed, SSE-safe restore |
| Reversibility | DIY (encrypt operator / custom) | Vault + stable typed tokens + `restore` |
| Multi-tenant vaults | — | Per-lane encrypted vaults |
| Ops | Python env + NLP models (+ container) | One binary, no dependencies |
| Together | — | ClaimCheck proxies; Presidio extends detection |
