# Presidio detection backend (optional sidecar)

ClaimCheck's built-in recognizers are deterministic — regex + validators, no ML,
no downloads. For free-text entity detection beyond that (person/org/location
names, crypto wallets, international ID formats), ClaimCheck can call a local
[Microsoft Presidio](https://microsoft.github.io/presidio/) **analyzer** as an
optional detection sidecar. Nothing about ClaimCheck's guarantees changes:
Presidio findings mint through the same vault, get the same stable
placeholders, and restore exactly like built-in tickets.

Why have both? [docs/COMPARISON.md](COMPARISON.md) — the four-problems framing.

## When to enable it

- You're scrubbing **prose** (letters, chat transcripts, tickets), not just
  config files and logs — names and places matter there.
- You need entity types the deterministic tiers don't cover: `CRYPTO` wallet
  addresses, `PERSON`/`LOCATION`/`ORGANIZATION` via Presidio's NLP models,
  country-specific ID formats.
- You're okay running one extra local container.

If you only scrub `.env` files, connection strings, and source code — **skip
it**. The built-in tiers already cover those deterministically.

## Quick start (Docker)

Run the analyzer container:

```bash
docker run -d --name presidio-analyzer -p 127.0.0.1:3000:3000 \
  mcr.microsoft.com/presidio-analyzer:latest
```

The first request after start is slow — the container loads its NLP models
then (hundreds of MB of RAM; give it a couple of minutes). Verify it's up:

```bash
curl -s http://127.0.0.1:3000/analyze \
  -H 'Content-Type: application/json' \
  -d '{"text":"wire 0x71C7656EC7ab88b098defB751B7401B5f6d8976F to the vendor","language":"en","score_threshold":0.4}'
```

Point ClaimCheck at it — set the env var on the **ClaimCheck process**
(proxy systemd unit, tray environment, or plain shell):

```bash
export SANITIZE_PRESIDIO_URL=http://127.0.0.1:3000
```

That's the whole integration. The proxy fires a fire-and-forget **warm-up**
request at startup so the container's model load happens before your first
real request, not during it.

> **ARM boards (Raspberry Pi, etc.):** the MCR image above is amd64-only. The
> community `ghcr.io/data-privacy-stack/presidio-analyzer:latest` image is
> multi-arch and runs on-device (silicon-verified 2026-10-28 on a Raspberry Pi):
> `-p 127.0.0.1:5001:3000` + `SANITIZE_PRESIDIO_URL=http://127.0.0.1:5001`.
> Expect a narrower default recognizer set (CRYPTO wallets weren't detected in
> that test) and non-stock entity names (`ORG_ENTITY`); probe with a direct
> `/analyze` call at `score_threshold: 0` to see what your image recognizes.
> Cover the gap deterministically with a `.sanitize-patterns.json` pattern in
> the project root — built-in tier, no container needed:
>
> ```json
> [
>   { "name": "CRYPTO_WALLET", "regex": "\\b0x[a-fA-F0-9]{40}\\b" }
> ]
> ```

## Tunables

| Env var | Default | Meaning |
|---|---|---|
| `SANITIZE_PRESIDIO_URL` | unset | Analyzer base URL (`/analyze` appended automatically). Unset = feature off. |
| `SANITIZE_PRESIDIO_SCORE` | `0.4` | Minimum confidence (0–1]. Raise to cut false positives, lower to catch more. |
| `SANITIZE_PRESIDIO_ENTITIES` | unset | Comma-separated allowlist, e.g. `CRYPTO,PERSON,LOCATION`. Overrides the default exclusions. |
| `SANITIZE_PRESIDIO_MAX_CHARS` | `50000` | Payloads above this size skip the Presidio pass — regex tiers always run in full. |

## Semantics — what Presidio does and doesn't change

- **Tier-separated.** Findings land in the *probabilistic* tier: labeled by
  entity type (`[CRYPTO_1]`, `[PERSON_2]`), masked through the vault, fully
  restorable — but never part of the validator-backed guarantee tier the
  deterministic recognizers provide.
- **Runs last.** The pass runs after the deterministic tiers, on the
  already-masked text — built-in hits are never double-masked.
- **Fail-soft.** Analyzer down, slow, or malformed → the pass is skipped and
  the request completes normally. Consecutive-failure count shows in
  `ClaimCheck status`.
- **Size-capped.** Above `SANITIZE_PRESIDIO_MAX_CHARS` the pass is skipped
  (huge Cline contexts were the latency report that motivated the cap); regex
  tiers still run at full strength.

## Default exclusions (learned the hard way)

Without an explicit allowlist, these Presidio entity types are dropped — each
earned its exclusion with real false positives:

- **`DATE_TIME`, `TIME`** — flag benign prose ("May and June are good months")
  at 0.85 confidence.
- **`URL`** (v0.8.5+) — fragments code identifiers: `sendmail` →
  `[URL_3]ndmail`, `SMTP` → `[URL_5]TP`. Real cred-bearing URLs are already
  covered by ClaimCheck's own URL-credentials row.

Set `SANITIZE_PRESIDIO_ENTITIES` to re-enable any of them deliberately.

Per-span fail-soft too: a Presidio span that crosses a newline is skipped
(structured payloads — numbered lists, code, tables — produce garbage
multiline spans), and spans containing an existing placeholder are skipped.

## Verify it's working

```bash
ClaimCheck status          # Presidio section: enabled/disabled, failure counter
python3 test_presidio.py   # stub-analyzer regression suite (crypto masking,
                           # DATE_TIME exclusion, tier separation, fail-soft)
```

Live probe: sanitize a line containing a crypto wallet address — it should
come back as `[CRYPTO_1]`, and `restore` should bring it home from the vault.
