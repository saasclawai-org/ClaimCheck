# ClaimCheck Roadmap (locked 2026-10-24)

Open-core rule: **the free core never degrades without a license.** Pro features add
team/ops/compliance value on top; they never gate existing free behavior. Free solo
users stay free forever — they are the distribution.

Reference: `docs/competitive-analysis-2026-10-24.md` (Sunglasses, Semgrep, vulnhuntr).

---

## Shipped (v0.2.x, Oct 24 2026)

- 22 deterministic recognizers (regex + validators), NER tier (opt-in ONNX BERT, CPU)
- MCP stdio tools + CLI + OpenAI-compatible scrubbing proxy (SSE restore included)
- Vault: per-value tickets, **atomic writes + locking**, `--vault` per instance
- Proxy: path-prefix dedup (OpenClaw-version tolerant), clean composition, clean 502
  on upstream failure, `SANITIZE_PROXY_LOG` request logging, fail-closed posture
- Windows installer (`install.ps1 -WithProxy/-EnableNER`), release pipeline, staging
- Deployments: Windows/Cline, main-box OpenClaw gateway, Raspberry Pi (default wire)

---

## Phase 0 — Hardening (pre-fleet)

- [x] Vault atomic writes + locking, `--vault`, 502s, request log (03ab718)
- [x] Proxy path-prefix dedup + clean composition (6a18fd9)
- [ ] Code review pass (vault ACLs, path handling, error paths)
- [ ] Golden corpus + false-positive sweep on real files
- [ ] Clean-vs-incomplete reporting: surface unreadable inputs (PDFs, binaries,
      archives) as first-class results — never a silent PASS
- [ ] Multi-instance concurrency matrix (several `--vault` files + shared-lane load)

## Phase 1 — Fleet

- [ ] Ops setup: dedicated service user, vault perms `600`, systemd template unit
      (one proxy instance per model lane, each with its own `--vault`)
- [ ] Cloudflare route + Access policy for the fleet proxy
- [x] Fleet deployment guide (`docs/fleet-deployment.md`) — vault scoping, shared-lane vs
      lane-per-tenant, restore rights, lane verification (wire proof + /status fingerprint)
- [ ] Realistic mode (opt-in): per-type reserved-namespace fake data instead of tickets —
      SSN 900-xx-xxxx, FEIN 00-xxxxxxx, email `@claimcheck.invalid`, phone 555-01xx —
      format-valid for model workflows, self-identifying as synthetic, collision-free.
      Tickets stay the default (leak visibility + forensic clarity). Design discussion 2026-10-29.
- [ ] Pi + Windows resync to latest (Pi: git pull + rebuild + proxy restart)

## Phase 2 — Security tiers (free core — the differentiators)

- [ ] Obfuscation pre-pass: zero-width characters, homoglyphs, bidi normalization
- [ ] Injection classifier tier — `SANITIZE_INJECTION=1`: Prompt Guard 2-class ONNX
      in-process, score per message, block-or-flag posture. Honest-limits docs ship
      with the tier (detection raises the bar; never claims to close the class)
- [ ] Toolguard tier — `SANITIZE_TOOLGUARD=1`: high-precision blockable set
      (remote-fetch-to-shell, reverse shells, fork bombs, credential-file access,
      mass-destructive ops) + advisory set (`rm -rf`, `chmod`, scope-violating paths)
      + user-extensible policy file
- [ ] Semgrep generic-mode rule import (pattern packs ride the community registry)
- [ ] Semantic review phase-2: flagged-call analysis via a small local model
      (GPU execution provider optional; self-hosted lanes = zero marginal cost)

*Strategic call (2026-10-24, Norman + SaaSClaw): Phases 2 stays FREE. These tiers are
the differentiators against Sunglasses-class competitors and the distribution engine.
Pro monetizes team/ops/compliance value, not the security essentials.*

---

## Phase 3 — Pro tier (revenue)

**Licensing:** Ed25519 offline keys, JetBrains-style perpetual fallback,
Paddle/LemonSqueezy as merchant of record. License check is offline-first; no
telemetry, no accounts, no phoning home — the privacy brand is the moat.

**Pro features (planned):**

1. **Audit trail** — persistent scrub-event log (what was masked, when, which tier,
   which rule ids) with JSONL + syslog/SIEM export. The compliance evidence layer.
2. **Vault encryption at rest** — DPAPI (Windows) / Keychain (macOS) / key-file
   (Linux). Also a Phase-0 hardening item; ships free for single-user as OS-keychain
   support where available, Pro for managed/enterprise key handling.
3. **Central policy management** — push patterns/policies/blocklists to a fleet from
   one file or endpoint; drift detection across proxies.
4. **Multi-user vault identity** — per-user attribution inside shared vaults, RBAC-lite
   over restore rights.
5. **GPU semantic review pack** — fleet configuration for local-model tool-call
   analysis (phase-2 scale-out; self-hosted GPU config).
6. **Industry recognizer packs** — curated pluggable packs (finance/payroll first —
   the founder's industry is the buyer, the product stays generic).
7. **Priority support + upgrade SLA.**

**Buyers:** teams running agent fleets. Our own internal deployment is the design
partner and first pilot — dogfood Pro features on our own fleet before selling.

**Pricing anchors (Oct 24 strategy):** $19–29/seat/mo; enterprise $2–5k/yr with
support. Solo/free users are the funnel, never the target.

---

## Sequencing

Phase 0 (hardening) → Phase 1 (fleet live for colleagues) → Phase 2 (security
tiers, free) → licensing build → Phase 3 (Pro launch).

Rule of thumb for every future feature: if it protects a *single* user's traffic,
it ships free. If it gives a *team* evidence, control, or scale, it is Pro.
