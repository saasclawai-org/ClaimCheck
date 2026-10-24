# Competitive tear-down: ClaimCheck vs the agent-security field (2026-10-24)

Requested by Norman after Sunglasses surfaced. Covers Sunglasses and Semgrep in depth,
the adjacent set briefly, and locks the tier strategy (injection / toolguard) and
positioning. Sources fetched 2026-10-24: sunglasses-dev/sunglasses, semgrep/semgrep.

## ClaimCheck in one line

Local-first **pseudonymization wire gate**: masks secrets/PII in model-bound traffic,
the agent *keeps working* on placeholders, and a reversible vault restores on the
response path. Self-contained .NET 9 exe — MCP stdio tools, CLI, and an
OpenAI-compatible scrubbing proxy. 22 deterministic recognizers + opt-in NER (ONNX).
MIT core, open-core roadmap (Pro: OCR, DPAPI vault, packs, audit — never degrades the
free core).

---

## Sunglasses (github.com/sunglasses-dev/sunglasses) — closest direct competitor

**What it is.** Open-source local "input firewall for AI agents", Python/PyPI, beta
(0.5.6). 1,583 patterns across 117 categories scanning text, code, PDFs, images, QR
codes, audio and video. Surfaces: CLI, Python API, an MCP server, a GitHub Action
(PR scanning), a Claude Code hook (blocks secrets/policy violations in tool calls
pre-execution), a browser scanner, and an MCP **stdio proxy** that refuses all
tools/list + tools/call until a human approves the server at an interactive terminal.
Pattern families include prompt injection (GLS-PI-\*), data-exfil mechanisms
(GLS-MECH-\*), and secrets (GLS-SD-\*).

**Their genuinely good ideas:**
- **Clean-vs-incomplete exit semantics.** Four exit codes: clean / threat / incomplete
  / error — and JSON fields `is_clean` ≠ `threat_found` ≠ `inspection_complete`.
  "What the scanner could not read" is a first-class fact. (Their example: a ZIP is
  never extracted → INCOMPLETE, never silently PASS.)
- **Snapshot-bound approval gating.** MCP proxy refuses everything until a human
  approves at a real terminal; the approval binds to a hash of the tool descriptors
  *and* the server id — two servers with identical tool lists each need their own
  approval. Changing what's behind a familiar tool list forfeits trust.
- **Precise gap documentation.** They document (with fixtures) that GLS-SD-010's
  line anchor misses indented `KEY=***` — in YAML, JSON strings, quoted values.
- **Pattern-integrity CI** over the 1,583-pattern corpus.

**Their model vs ours — the core fork:**
- Sunglasses **stops traffic**: findings + exit codes; "you decide what to pass
  onward". Credentials in tool calls/results are *withheld* — the task dies with them.
- ClaimCheck **rewrites traffic**: placeholders mint, the agent keeps working, results
  restore. Workflow-preserving vs flow-stopping. For code-generation agents this is
  the difference between an annoying gate and an invisible one.
- Enforcement point: they mediate **client↔MCP-server hops** (the tool lane) + files
  and PRs. We sit on the **model-provider hop** for any client (Cline, OpenClaw, any
  OpenAI-compatible caller). Complementary positions — they could stack with us.

**Threat level: HIGH.** Same niche (local-first, open-source, agent traffic), broader
pattern surface, frictionless pip install, already listed on mcpservers.org. Their own
honesty marketing ("a clean result is a confidence floor, not a guarantee"; "installing
it is not protection on its own") is excellent and matches our fail-closed posture
docs.

**What we steal:** clean-vs-incomplete semantics in our scan/report surfaces;
pattern-integrity CI when the pattern corpus grows; snapshot-bound approval if the
toolguard tier ever ships an MCP mediator; their "confidence floor" phrasing.

**What we do NOT do:** race them to 1,583 patterns. That is their lane; it is a
content-maintenance treadmill with diminishing returns and we have a different moat
(below).

---

## Semgrep (github.com/semgrep/semgrep) — complementary, plus one adoption lever

**What it is.** Fast static analysis for source code: rules written to *look like the
code* (pattern DSL with metavariables), 30+ languages (Bash, C#, Python, JS/TS, YAML,
Terraform, generic/ERB/Jinja…). Runs in IDE, pre-commit, CI. Ecosystem: Community
Edition (OSS engine; their own README says it "will miss many true positives" for
security use), AppSec Platform (20,000+ proprietary rules across SAST/SCA/Secrets,
cross-file dataflow, AI triage), Supply Chain (SCA with reachability). Open-core:
free engine, paid accuracy — the same shape we chose. Markets the same trust axis we
do: "code is analyzed locally; by default, code is never uploaded."

**Why it is not competition.** Semgrep is **SAST for repositories** — it scans source
trees in CI. ClaimCheck is a **runtime gate for live model traffic**. Semgrep never
sees a prompt, a tool-call argument stream, or a model response; we never scan a repo.
Different enforcement points: repo → tool hop → model hop.

**Real overlaps to be honest about:**
1. **Secrets detection domain.** Semgrep Secrets finds credentials *in code*; our
   recognizers find credentials *in traffic*. "Just run semgrep on the repo" answers
   committed secrets, not pasted-in-prompt secrets, ad-hoc user traffic, or fetched
   content — and has no vault/restore and no workflow preservation.
2. **Open-core shape + local-first marketing are now table stakes.** All three players
   (us, Sunglasses, Semgrep) market local analysis. Local-first is no longer a
   differentiator; the vault/workflow model is.

**The adoption lever (actionable):** Semgrep's rule format is the de facto community
standard, and its **generic mode** is regex-ish patterns over arbitrary text — exactly
what a tool-call argument or a prompt is. Supporting a *subset* of Semgrep
generic-mode YAML rules as a `.sanitize-patterns.json` extension ("semgrep-generic
compatible") would let ClaimCheck ride thousands of community rules as pattern packs
without writing them ourselves. Cheap to parse (regex + minimal DSL), high leverage.

---

## Adjacent set (brief)

- **Cloakroom** — Claude Code plugin, built the same week as our NER tier. Validates
  the niche; small surface.
- **Presidio (Microsoft)** — PII detect/redact *library*. Irreversible redaction, no
  vault, no proxy, integration required. A component we could theoretically sit on
  top of, not a competitor.
- **Hosted gateways** (Nightfall, Private AI, Skyflow, Pillar, etc.) — enterprise,
  hosted, data leaves the perimeter. Antithetical to the local-first pitch; different
  buyer.
- **Injection classifiers** — Meta Prompt Guard 2 (~22M params), protectai
  deberta-injection, Rebuff. These are *building blocks* for our proposed injection
  tier, not competitors.
- **Skill-registry competition** — ClawHub PII/redact skills: idea-saturated,
  adoption-empty (all 0–2 installs/60d as of today). Not a threat lane.

---

## Tier decisions (locked 2026-10-24)

1. **Injection tier: classifier-first.** Prompt Guard 2-class ONNX model, in-process,
   opt-in (`SANITIZE_INJECTION=1`), score-per-message, block-or-flag posture — same
   shape as the NER tier. Plus a deterministic obfuscation pre-pass (zero-width
   chars, homoglyphs, bidi) which is deterministic and helps every downstream layer.
   We do NOT ship injection *phrase patterns* as a detection story: high
   false-positive, trivially evaded. (Sunglasses' pattern lane covers that; flag-only
   pattern hints are acceptable later if wanted, clearly labeled as a floor.)
   Honesty requirement shipped with the tier: detection raises the bar, never closes
   the class — layered defense (system-prompt hardening, tool privilege separation,
   human confirmation) is the real story and the docs must say so.
2. **Toolguard tier: deterministic high-precision + policy file.** Blockable set =
   near-always-hostile shapes (remote-fetch-to-shell, reverse shells, fork bombs,
   credential-file access, mass-destructive ops, obvious exfil shapes). Advisory set
   (`rm -rf`, `chmod`, scope-violating paths) flags, never blocks. User-extensible
   policy file; accepts Semgrep-generic-mode import (the adoption lever above).
   Phase-2: semantic review of flagged calls via a small local model (self-hosted GPU
   lane). The proxy already parses `tool_calls[].function.arguments` — this is a
   recognizer set over existing plumbing, not a new architecture.
3. **Pattern-count race: DECLINED.** Sunglasses owns breadth. Our differentiators are
   the vault/workflow model, the model-wire position, the self-contained binary, and
   (now) concurrency-safe atomic vaults.

---

## Positioning lines

- ClaimCheck = **"the agent keeps working"** — pseudonymize on the way out, restore on
  the way back. Sunglasses = **"decide what passes"** — block/withhold. Semgrep =
  **"scan the repo"** — before anything runs.
- Three enforcement points, one stack: repo (Semgrep) → tool hop (Sunglasses) → model
  hop (ClaimCheck). We are the only vault at the model hop.
- Local-first is table stakes now. Workflow preservation is the differentiator.
- Pro-tier narrative: exfil scrub + injection classifier + toolguard at one local
  chokepoint, with per-lane vaults and audit — the local-first agent security gateway
  nobody else assembles.

## Immediate actions from this survey

1. Ship clean-vs-incomplete reporting in `scan`/`sanitize --json` (surface which
   inputs were unreadable — binary/PDF pass silently today; flag it like Sunglasses'
   exit 3). Backlog: next CLI pass.
2. Keep the injection + toolguard tiers in the roadmap as specced above, behind the
   hardening queue (code review pass, golden corpus, concurrency ✓ done, vault
   DPAPI/Keychain, packaging, signing).
3. Semgrep-generic import: evaluate effort when the toolguard policy file lands.
