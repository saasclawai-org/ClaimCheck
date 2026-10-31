# Injection tier — design spec (draft, not yet implemented)

Status: spec only. Owner: SaaSClaw. Created 2026-10-31 after the Sunglasses
(sunglasses-dev/sunglasses) survey — see docs/competitive-analysis-2026-10-24.md
for the competitive context and docs/CONFIGURATION.md for existing knob conventions.

## Goal

Add prompt-injection detection to ClaimCheck as an opt-in tier, consistent with
the existing architecture: local-only, deterministic-where-possible, classifier
where needed, fail-closed at the proxy, every knob opt-in via env var, shipped
inside the single self-contained binary.

Scope guard: ClaimCheck's lane is the **model hop** (client ↔ provider).
Tool-lane injection (client ↔ MCP) is Sunglasses' territory — we stack, we don't
rebuild. This tier protects the LLM traffic ClaimCheck already touches.

## Tier strategy (locked 2026-10-24, refined 2026-10-31)

| Tier | What | Local/dep cost | Catches |
|---|---|---|---|
| 0. Normalization (always on) | Unicode fold, zero-width strip, homoglyph map, base64/URL/hex decode of candidate spans before ALL matching | Zero deps, native C# | Obfuscated payloads for every tier below |
| 1. Patterns (opt-in) | Deterministic injection/override patterns in C#, consulted against the public registries (Sunglasses pattern registry, MCP Attack Atlas) for coverage philosophy — implemented natively, no Python runtime | Zero deps | Known attack shapes, instant, auditable |
| 2. Classifier (opt-in) | Prompt Guard-class 2-class model as local ONNX (same shipping shape as the NER tier) | ~100–300MB cached model download, ONNX Runtime already referenced | Novel/semantic injection that patterns miss |

Ordering matters: tier 0 normalizes once; tiers 1–2 run on the normalized text.
Caps follow the existing fail-soft pattern (see CONFIGURATION.md tier-cap table):
patterns run at every size; the classifier skips above a size cap, cleanly.

## Env knobs (proposed)

| Variable | Default | Purpose |
|---|---|---|
| `SANITIZE_INJECTION_PATTERNS` | off | `=1` enables the deterministic pattern tier. |
| `SANITIZE_INJECTION` | off | `=1` enables the classifier tier (downloads model on first use to `~/.claimcheck/models/`, like NER). |
| `SANITIZE_INJECTION_THRESHOLD` | 0.5 | Global confidence floor (clamped, same convention as NER). |
| `SANITIZE_INJECTION_ACTION` | `ticket` | `ticket` = findings become tickets like any other entity; `flag` = annotate in findings only, don't rewrite. |
| `SANITIZE_INJECTION_MAX_CHARS` | 100,000 | Classifier skipped above this; patterns still run (fail-soft). |
| `SANITIZE_INJECTION_MODEL` | Prompt-Guard-2 ONNX | Any HuggingFace text-classification ONNX repo. |

Note the `ticket` action nuance: injection findings are *instructions*, not
values — ticketing an instruction preserves the agent's ability to see that
something was said, without the payload surviving verbatim into the prompt.
`flag` exists because some deployments want the request logged and the choice
left to the caller. Default is conservative either way; the proxy path stays
fail-closed per existing behavior (scrub failure = hold, never forward).

## Tier 0 normalization details (in scope for existing recognizers too)

Adapted from the Sunglasses Clean stage — implement natively in C#:

- Strip zero-width and format characters (U+200B–200F, U+2060–206F, U+FEFF).
- Unicode NFKC fold + homoglyph mapping before pattern matching.
- Detect and decode base64/hex/URL-encoded spans above a length floor, run
  recognizers on decoded text, restore from the original span.
- RTL override / bidi-isolate detection → flag (classic smuggle vector).

This stage benefits ALL tiers unconditionally (FEIN inside a base64 blob
currently ships through untouched) and should be landed FIRST, before any
injection-specific work, behind its own audit tests.

## What we explicitly don't do

- No Python runtime, no Sunglasses dependency (breaks single-binary + Pi path).
- No cloud classifier APIs (Lakera, Azure Prompt Shields) — violates local-only.
- No tool-lane MCP gating (their lane).
- No telemetry about what was caught — audit logging stays the existing
  tickets-only body-log.

## Sequencing

1. Tier 0 normalization + tests (benefits existing recognizers — highest value/effort ratio).
2. Tier 1 patterns behind `SANITIZE_INJECTION_PATTERNS`.
3. Tier 2 classifier ONNX, mirroring the NER plumbing end-to-end.
4. Website + README reveal only after 1–3 ship (roadmap line already says "prompt-injection classifier tier · tool-call guard").
