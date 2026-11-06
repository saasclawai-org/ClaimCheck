# PII types reference

Every entity type ClaimCheck can ticket, which tier produces it, whether it's
on by default, and the per-lane knobs that include or exclude it.

## The detection tiers at a glance

| Tier | Default | Enabled by | Detects |
|---|---|---|---|
| 1. Deterministic regex | **always on** | — | credentials, financial IDs, contact data (the guarantee tier) |
| 2. Local NER (ONNX) | off | `SANITIZE_NER=1` | PERSON, ORG, LOC, MISC |
| 3. Gazetteer bigram | off | `SANITIZE_GAZETTEER=1` | PERSON ("First Last", both tokens in the 250K name set) |
| 4. Presidio backend | off | `SANITIZE_PRESIDIO_URL` | whatever the analyzer service provides |

Always-on deterministic extras: `ORG_ENTITY` + `REVERSE_NAME` recognizers,
`ENCODED` blob inspection, the client-master gazetteer (`CLIENT`,
`CLIENT_FEIN`), and the injection-pattern tier (see
[INJECTION-TIER.md](INJECTION-TIER.md)).

## Tier 1 — deterministic regex (always on)

The guarantee tier: pattern + validator, stable tickets, full restore
support.

| Type | Catches | Notes |
|---|---|---|
| FEIN | `12-3456789` | `00-` prefix reserved for realistic-mode fakes |
| SSN | `123-45-6789` | invalid-area guards (000/666/9xx areas, 00 group, 0000 group) |
| EMAIL | `user@host.tld` | `claimcheck.invalid` reserved for fakes |
| CARD | 13–19 digit card numbers | keyword-anchored row (no Luhn gate — test cards fail checksum, but the keyword makes intent unambiguous) + bare row with Luhn |
| IBAN | country-prefixed IBAN | matched **before** CARD — a long IBAN can Luhn-pass and fragment (2026-10-26 field leak) |
| PHONE | NANP-shaped numbers | `010` exchange reserved for realistic-mode fakes |
| IMEI | 15 digits | Luhn |
| DOB | label-anchored dates of birth | |
| PASSPORT | passport + 6–9 alphanumerics | |
| DL | driver's-license label + 5–12 alphanumerics containing a digit | v0.9.15: word boundary + digit lookahead — "DLLMITRE" / "Driver's License Numbers" no longer match |
| ROUTING | routing/aba + 9 digits | |
| ACCOUNT | (bank) acct/account + 4–17 digits | |
| INSURANCE_ID | member/policy/insurance + `AA-1234567` | |
| MEDICAL_LICENSE | license label + `XX-XX-123456` | |
| VIN | 17 chars, I/O/Q excluded | |
| ADDRESS | street-address shape ending in state + ZIP | |
| CVV / EXPIRY | card security code / MM/YY | label-anchored |
| PASSWORD | password/pwd/secret/token/cookie/credentials/keys/auth + `:` or `=` | value-only masking — the label stays visible and the same value mints the same ticket across files |
| URL_CREDS | `scheme://user:pass@host` | creds segment only; scheme/host stay readable |
| AWS_KEY | `AKIA`/`ASIA` + 16 | |
| GH_TOKEN | `gh[pousr]_` tokens | |
| API_KEY | modern prefixes (`github_pat_`, `whsec_`, `sess_`, `npm_`, `glpat_`, `shpat_`, `pypi-`, `sk-`) | |
| SLACK_TOKEN | `xox[baprs]-` | |
| JWT | three-segment `eyJ…` tokens | |
| ID_LABEL | customer/client/invoice/policy/case/member/patient/mrn/record/serial/sn/device/biometric/fingerprint/voiceprint/claim/order/file/ref/project/contract/vendor/supplier/employee + 4–17 digits | v0.9.15: `ticket` keyword removed — ticket numbers pass raw |
| ENCODED | base64 (≥48) / hex (≥96) blobs | whole blob masked when the **decoded** content matches any pattern |
| PRIVATE_KEY | PEM private-key blocks (`-----BEGIN … END-----`, multi-line) | whole block as one ticket — direct masking, since the DER binary defeats decoded-content scanning |
| DATE | full numeric + named-month dates | **off by default** (v0.9.2 — dates are operational context in notice workflows); `SANITIZE_DATES=1` opts in; old DATE tickets in existing vaults still restore |

**Exclude any of these per lane:** `SANITIZE_EXEMPT_TYPES=PHONE,IP` —
comma-separated, case-insensitive type names. Recognizers are skipped
entirely: values pass raw, no tickets mint, nothing enters the vault (in
multitenant/no-restore lanes that means the model can speak them — a
deliberate, opt-in trade-off from a real support-QA deployment). Applies to
the proxy and the CLI/MCP scrub paths alike.

**Extend this tier:** a root-scoped `.sanitize-patterns.json` can add
custom patterns per project.

## Tier 2 — local NER (opt-in, off by default)

`SANITIZE_NER=1` plus the ONNX model (`SANITIZE_NER_MODEL`).

Types: **PERSON, ORG, LOC, MISC**.

Knobs: `SANITIZE_NER_TYPES` (type subset), `SANITIZE_NER_THRESHOLD` and
`SANITIZE_NER_THRESHOLD_<TYPE>` (global + per-type confidence),
`SANITIZE_NER_MAX_CHARS`, `SANITIZE_NER_DEBUG`.

Guards (policy, not configurable): code-shaped spans dropped and
newline-crossing spans dropped (v0.9.13); per-type noise gates on LOC and
PERSON (v0.9.15).

## Tier 3 — gazetteer bigram (opt-in, off by default)

`SANITIZE_GAZETTEER=1`. PERSON via "First Last" bigrams where both tokens
sit in the 250K SSA-given + Census-surname set. Deterministic, no network.

## Tier 4 — deterministic recognizers (always on)

- **ORG_ENTITY** — business firm names anchored on legal suffixes
  (PLLC/LLP/Inc/Corp/…). Guards: all-caps single "names" skipped (they're
  cities or acronyms, v0.9.13); state-code comma-lists skipped (v0.9.15).
- **REVERSE_NAME → PERSON** — payroll-standard "Last, First MI", both tokens
  gazetteer-validated. Guards: dateline tokens, tech brands, acronym first
  names (v0.9.13); state names/codes (v0.9.15).

## Tier 5 — client-master gazetteer (v0.9.12, deterministic)

`import-clients` / `import-feins` load the tenant's client master; matches
mint **CLIENT** and **CLIENT_FEIN** tickets annotated with the client code
(feed for the notices workflow).

## Tier 6 — Presidio backend (opt-in, off by default)

`SANITIZE_PRESIDIO_URL` points at an analyzer service; whatever that service
provides becomes ticketable. On the predator fleet that has been: PERSON,
LOCATION, NRP, PHONE_NUMBER, US_DRIVER_LICENSE, UK_NHS, MEDICAL_LICENSE,
IP_ADDRESS, MAC_ADDRESS, ORGANIZATION, CRYPTO, URL, DATE_TIME, TIME, UUID.

- **Default-excluded** (never minted unless allowlisted): `DATE_TIME`,
  `TIME`, `URL` (v0.8.x), **`UUID`** (v0.9.15 — trace/request IDs minted
  ~80 tickets/day on lane 8005; identifiers are plumbing, not secrets).
- `SANITIZE_PRESIDIO_ENTITIES` — explicit allowlist; when set, ONLY those
  entity types run (also how a default-excluded type comes back).
- `SANITIZE_PRESIDIO_SCORE` (default 0.4), `SANITIZE_PRESIDIO_MAX_CHARS`
  (default 50,000).

**v0.9.15 per-type noise gates** (policy, not configurable — exempt the
type on the lane if a gate ever bites): LOCATION = strict place allowlist
(states + codes, territories, countries, major cities; extend with
`SANITIZE_PLACES_EXTRA`); PERSON junk filters (digits / all-lowercase /
CamelCase glue / contractions / system vocabulary / non-gazetteer single
tokens / state lists); NRP filters; PHONE_NUMBER NANP structural
validation; UK_NHS modulus-11 checksum; US_DRIVER_LICENSE shape; MEDICAL_LICENSE
lowercase reject; ORGANIZATION state-code lists.

**Vault note:** NLP identity types (LOCATION, PERSON, ORG_ENTITY, ORG, LOC,
MISC, NRP) dedup case-insensitively — atlanta/Atlanta/ATLANTA share one
ticket (v0.9.14). Credential types key exactly.

## Per-lane exclusion matrix (quick answer)

| Want to… | Knob |
|---|---|
| Turn off a deterministic type | `SANITIZE_EXEMPT_TYPES=TYPE,…` |
| Turn DATE on | `SANITIZE_DATES=1` |
| Run/disable the NER tier | `SANITIZE_NER=1` / unset |
| Trim NER types or tune confidence | `SANITIZE_NER_TYPES`, `SANITIZE_NER_THRESHOLD[_TYPE]` |
| Run/disable the gazetteer tier | `SANITIZE_GAZETTEER=1` / unset |
| Run/disable the Presidio tier | `SANITIZE_PRESIDIO_URL` / unset |
| Allowlist Presidio types (incl. re-enabling UUID/DATE_TIME) | `SANITIZE_PRESIDIO_ENTITIES` |
| Tune Presidio confidence / payload cap | `SANITIZE_PRESIDIO_SCORE`, `SANITIZE_PRESIDIO_MAX_CHARS` |
| Teach LOCATION more client towns | `SANITIZE_PLACES_EXTRA=dawson county,…` |
