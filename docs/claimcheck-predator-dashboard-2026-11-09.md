# ClaimCheck Predator Dashboard — Design Sketch
_2026-11-09 · read-only operator console for the predator centralized proxy deployment_

## Goal & Non-Goals

**Goal:** One web page where Norman sees what the ~10 vibe coders' proxy lanes are doing —
health, posture, volume, what tickets the models actually saw — without SSH.

**Non-goals (hard):**
- **No scrub form.** A paste-PII-here box trains the exact habit ClaimCheck exists to kill.
- **No reveal/restore.** The dashboard never holds a vault password and never decrypts anything.
  Restore stays CLI-only. This keeps the dashboard safe to expose: it can only ever show
  post-scrub, tickets-only data.
- **No hosting for others** (Norman, 2026-11-09). localhost on predator + SSH tunnel. It is an
  internal operator tool, not a product surface. (The product-side Pro dashboard, if built later,
  reuses this design but ships separately.)
- **Never in the scrub hot path.** Pure read-only sidecar.

## Architecture

Separate small ASP.NET app (`ClaimCheck.Dashboard`), own systemd unit
(`claimcheck-dashboard.service`, User=norman, port e.g. 8770, bind 127.0.0.1).
Same repo as ClaimCheck (`/dashboard` in claimcheck-src), own csproj, ships in the same
release tooling — no npm build, server-rendered HTML + htmx, zero JS pipeline.

Two data sources, both already existing:

1. **Lane `/status` JSON** (poll ~15s) — live posture: version, upstream, vaultEntries,
   totalRequests/scrubbedRequests, NER/Presidio state + failures, failClosed, suppressCode,
   tenantHeader, passthroughAllowlist, exemptTypes, customClients/clientFeins, startedUtc.
   (`/health` for a cheap up/down ping.)
2. **Audit JSONL** (`SANITIZE_PROXY_LOG=<path>` lanes) — `{"ts": "...O", "body": "<post-scrub
   forwarded request body>"}`. Historical truth: in-memory counters reset on restart, JSONL doesn't.

### Lane registry

Config file (e.g. `~/claimcheck/dashboard-lanes.json`):

```json
[
  { "name": "anthropic-prod",  "port": 8801, "audit": "/home/norman/claimcheck/audit/anthropic-prod.jsonl" },
  { "name": "openai-prod",     "port": 8802, "audit": "/home/norman/claimcheck/audit/openai-prod.jsonl" },
  { "name": "qwen-test",       "port": 8803, "audit": null, "note": "no audit file — status-only" }
]
```

Adding a lane = one JSON entry, zero dashboard code. Lanes without audit files still get
status/posture cards, just no history.

## Data model (SQLite sidecar, WAL mode)

The audit JSONL is append-only and lines can be big (full request bodies). The dashboard
ingests it once into SQLite (`dashboard.db`) and queries that; JSONL stays the archive.

```
lanes           name, port, upstream, version, started_utc, posture_json, last_seen
requests        id, lane, ts, model, tenant, status, ms, ticket_count, prompt_chars, line_ref
tickets         request_id, type, count          -- parsed via \[[A-Z][A-Z_]*_\d+\]
rollups_hour    lane, hour, requests, error_count, ssn, fein, api_key, email, phone, other, other_types_json
ingest_state    lane, path, offset, inode         -- idempotent tail; survives rotation
```

- `line_ref` = byte offset into the JSONL so the audit browser can pull the original line.
- Rollups power all charts; `requests` keeps a rolling detail window (e.g. 30 days, configurable).
- Ingest runs in-process (BackgroundService) — no cron, no extra unit. Catches rotation
  (inode change → re-open from 0, old inode archived in DB by file stamp).

### Small forward-compatible proxy change (v0.2, not v0.1)

Current audit line lacks lane/tenant/status/ms — latency and fail-closed events aren't visible
history today (status/ms only go to stderr when `SANITIZE_PROXY_LOG=1`). Extend `AuditBody` to:

```json
{ "ts": "...", "lane": "...", "tenant": "...", "status": 200, "ms": 412, "model": "...", "body": "..." }
```

(`lane` from new optional env `SANITIZE_LANE_NAME`; tenant from the multitenant header;
model/status/ms free at the forward point.) Dashboard tolerates old lines — missing fields
just render blank. Also pair this with logrotate for audit files (they grow unbounded, and
bodies are chunky).

## Pages

1. **Overview** — fleet grid, one card per lane: up/down + version, upstream, requests
   today/7d, last request age, posture badges (fail-closed ✓/✗, NER on/off + provider,
   Presidio on/off + consecutive failures, suppressCode, multitenant, passthrough allowlist
   if non-empty, vault entries). Banner strip for problems: lane down, audit stale, presidio
   failures climbing, vault-unreadable startup (that one's fatal in the lane, so "down").
2. **Lane detail** — request rate chart (hourly rollup), ticket-type histogram over time,
   latency p50/p95 (needs v0.2 `ms`), recent requests table (ts, model, tenant, tickets, ms,
   status), error rows highlighted (fail-closed 503s, upstream failures).
3. **Audit browser** — paged, filterable (lane/tenant/date/ticket-type/text search) view of
   sanitized bodies with ticket tokens highlighted. Post-scrub by construction, but: truncate
   long messages in list view, full body on click, lazy-load. This page answers the
   compliance question "what did the model see?" in one click instead of a grep.
4. **Analytics** — tickets by type × day × lane; top tenants by volume; model mix;
   scrubbed vs total. Honest note: counts = ticket tokens in outbound bodies (includes
   known-value re-tickets); fresh-mint vs re-ticket split is NOT derivable from the audit
   line and stays out of v1 rather than being faked.
5. **Vaults** — per-lane `vaultEntries` trend (from /status polls), customClients/clientFeins
   for the custom-client lanes. **Counts only, never contents.** No reveal anywhere.

No write operations in v0.x. Lane restarts stay `sudo systemctl restart claimcheck-proxy@<lane>`.
(A per-lane restart button is a possible v2, explicit opt-in, out of scope until asked for.)

## Auth posture

- **Bind 127.0.0.1:8770 on predator. No TLS, no login page** — access via SSH tunnel
  (`ssh -L 8770:127.0.0.1:8770 predator`). Zero new attack surface; nobody remote can even
  reach it.
- Dashboard holds **no secrets**: no vault passwords, no tenant keys, no upstream API keys.
  It reads localhost `/status` + audit files, period.
- If Norman later wants it reachable without a tunnel (e.g. from his phone): put the existing
  CF Access service-token pattern in front (same as the lanes) — **CF Access auth, never a
  homemade login**. That's the only sanctioned exposure path, and it's a decision for later.
- If lane `/status` ever moves off localhost, add a shared token then; unnecessary for v1.

## Phasing

- **v0.1 — no ClaimCheck changes at all.** Lane registry + /status polling + ingest of the
  existing `{ts, body}` format → Overview, Lane detail (minus latency), Audit browser,
  Analytics, Vaults-counts. Works against today's predator audit files as-is. ~1 day.
- **v0.2 — the small proxy/audit extension** (lane/tenant/status/ms/model) + logrotate for
  audit files. Enables latency + fail-closed history. Tiny, backward-compatible.
- **v0.3 — ops polish:** Telegram webhook on lane-down / audit-stale / presidio-failure
  (opt-in), search performance pass, 30d retention compaction.
- **Later/reuse:** product-side Pro dashboard reuses rollup schema + audit browser with a
  localhost-only core build; realistic mode feeds it fake data for marketing screenshots.

## Deliverables checklist (v0.1)

- [x] `dashboard/` project in claimcheck-src (csproj + Pages + IngestService + LaneRegistry)
- [x] `dashboard-lanes.json` example + README section
- [x] systemd unit file (`claimcheck-dashboard.service`, User=norman, 127.0.0.1:8770)
- [x] build + E2E verified against mock lanes (24/24 page checks; ingest exact incl. corrupt-line skip + future-format fields)
- [x] packaged: `ClaimCheck.Dashboard-v0.1.0-linux-x64.tar.gz` (45M self-contained, unit + example + README inside) — claimcheck-src root, 2026-11-09
- [ ] predator deploy + verify against real lanes (status cards live, ingest consuming real JSONL) — HANDED TO NORMAN, no SSH from dev box
- [ ] Screenshots for the open-core Pro-tier story later (safe: tickets-only data by construction)
