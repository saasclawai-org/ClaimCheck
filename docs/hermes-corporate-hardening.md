# Hermes Agent — Corporate Hardening (CPS / PVC deployment)

_2026-11-09 · for the PVC vibe-coder platform rollout · supersedes nothing; complements `docs/internal-apps-it-direction-2026-10-22.md`_

## What hermes is (threat model in one paragraph)

Hermes Agent (NousResearch, `hermes-agent`) is an **autonomous** agent — not an IDE copilot.
Server reference build: v0.22.6 at `~/.hermes/hermes-agent` (git: `NousResearch/hermes-agent`).
It has: **60+ built-in tools** (terminal, web search, browser, image gen, TTS…), a **persistent
memory system** (`memories/MEMORY.md`, `USER.md` — "builds a model of who you are"), a **skills
system where the agent writes its own reusable code** (`skills/`, via `skill_manage`), a
**messaging gateway** (Telegram/Discord/Slack/WhatsApp/Teams), **bot mode**, **cron** (scheduled
jobs), **MCP integration** (external tool servers), and a **self-updater** (`hermes update`).
Config lives in `%LOCALAPPDATA%\hermes` (Windows) / `~/.hermes` (Linux): `config.yaml` (settings),
`.env` (secrets), `SOUL.md` (system-prompt slot #1), `logs/` (auto-redacts secrets).

Autonomy + persistence + remote surfaces + self-modification = the hardening list below. None of
it is optional once ~10 devs depend on it daily.

---

## 0. Corporate shape: centralize

Do NOT install per-dev hermes on 10 laptops. Run hermes **server-side on the PVC engine host**
(the internal IIS app per the REV 2 direction doc), under a dedicated low-privilege service
account. One hardened host, one data dir to ACL, one place to audit — instead of 10 uncontrolled
installs holding provider keys.

## 1. Egress — never direct-to-provider (NON-NEGOTIABLE)

Hermes's model traffic goes through a **predator claimcheck lane**, never straight at
api.openai.com / api.anthropic.com. This is the predator thesis applied: scrub on the way out,
audit body-log records exactly what the model saw, provider keys stay server-side.

Enforced with **managed scope** (hermes's built-in IT-pinning — verified against the official docs):

- Managed layer lives in a system dir: `/etc/hermes/config.yaml` + `/etc/hermes/.env`
  (on Windows: set `HERMES_MANAGED_DIR` — **baked into the service definition by IT**, never
  user-settable, or a user can repoint it and defeat the whole mechanism; `hermes doctor`
  surfaces the resolved dir).
- Managed keys **win over the user's config AND the shell environment** for exactly those keys.
  Pin: `model.default`, the provider base-URL key (→ claimcheck lane), and the lane's API key.
- Files owned by root/admin (dir 0755, files 0644) — readable, not writable, by the service user.
- Verify: `hermes doctor` (reports resolved managed dir + pinned key count) and
  `hermes config set model.default anything` → must refuse with "managed by your administrator".

Also pin in managed config: `security.redact_secrets: true` (the docs' own example of a
corp-worthy pin).

## 2. Terminal sandbox

`hermes config set terminal.backend docker` — terminal tool execution runs in a container
(hermes ships a Dockerfile for exactly this). On the engine host Docker is already the pattern
(UFW-forward exemption, existing containers). Users on a centralized host never get bare-metal exec.

## 3. HERMES_YOLO_MODE — never set

`HERMES_YOLO_MODE` is on hermes's own config-writer **denylist** (the tool refuses to save it) —
which tells you how dangerous it is (auto-approval semantics). Corporate posture: verify unset in
every launch environment; audit for it in env dumps. If it appears anywhere, treat as an incident.

## 4. Messaging gateway + bot mode: OFF

Never run `hermes gateway` (Telegram/Discord/Slack/WhatsApp/Teams bridges) or bot mode. The agent
is driven by PVC sessions only. Concretely: no `TELEGRAM_*` / `DISCORD_*` keys in any `.env`
(user or managed), no gateway process in the service definition, no firewall path for it.

## 5. Tool surface — strip to the minimum

`hermes tools` is the control point (60+ built-ins: web search, browser, image gen, TTS, exec…).
Corporate baseline: browser/web-search/image/TTS **disabled** unless a workflow demands it —
browser is the double threat (arbitrary fetch = exfil + prompt-injection in one). MCP: internal,
vetted servers only, with hermes's per-server tool filtering so each server exposes a narrow
slice. Re-review the enabled toolset every quarter.

## 6. Skills + cron — the persistence vectors (gate them)

- `~/.hermes/skills/` — **agent-authored code that persists across sessions**. The "self-improving
  loop" is a feature and a persistence attack vector (injected instructions can become a permanent
  skill). Corporate: the skills dir is IT-owned/read-governed, reviewed on a cadence, and shipped
  from the managed baseline — not freely grown.
- `~/.hermes/cron/` — scheduled jobs the agent itself registered. Audit on the same cadence; an
  injected cron entry is persistence + scheduled exfil in one.

## 7. Memory — data-retention scope

`memories/MEMORY.md` + `USER.md` persist everything the agent learned about users and work.
Treat as company data: include in retention policy, ACL to the service account, wipe on
offboarding, and remember it feeds future prompts (a memory poisoning is a prompt poisoning).

## 8. Policy injection — SOUL.md / AGENTS.md

`SOUL.md` is **slot #1 in the system prompt**. Put the corporate policy there: PII handling rules,
never-exfiltrate, approved behaviors, the ClaimCheck ticket conventions (tickets stay tickets,
never invented — the exact rules we already wrote for the qwen lane). Per-project `AGENTS.md`
for repo-specific rules. Review diffs on hermes version bumps.

## 9. Version pinning

No `hermes update` in production. IT installs a pinned release (server currently runs v0.22.6),
tests in the eval lane, rolls deliberately. The self-updater is a supply-chain door; keep it shut.

## 10. Audit trail (already mostly built)

Three records, ship all to central logging:
1. **ClaimCheck body-log** (predator lanes) — every prompt/response body, post-scrub, tickets only
2. **PVC session records** — which human asked for what
3. **Hermes `logs/`** (`errors.log`, `gateway.log` — hermes auto-redacts secrets) + skills/cron/memories dirs — what the agent did and kept

Answerable post-incident: what did the agent see, do, and keep.

---

## PVC wiring (engine host)

- `appsettings.json` → `"Engines": { "Hermes": true }` (only enabled engine on the current build)
- `HermesBin` → full path to the hermes binary (server relies on PATH resolution via
  `~/.local/bin/hermes`; set explicitly on the engine host)
- Endpoint: `POST /api/v1/projects/{id}/send-hermes` (PVC scrubs before invoke — ClaimCheck is the
  sole scrub layer; PVC's own PII guard is disabled by design)

---

## Is Cline just as dangerous?

**Per-instance: no. In aggregate: it's the same two core risks wearing a smaller hat.**

Shared with hermes (identical in kind — the non-negotiables apply to both):
- Arbitrary terminal + file access running with **dev-level privileges**
- Model egress — direct-to-provider leaks everything in context → **same fix: claimcheck lane,
  no provider keys on the endpoint**
- Prompt injection from repo content (issues, READMEs, dependency docs) → both can act on it via tools
- MCP integration — unvetted MCP servers are a tool-exfil surface for both

Where Cline is **less** dangerous than hermes:
- **Human-in-the-loop by default** — every file edit / command needs explicit approval; auto-approve
  is opt-in per tool. Hermes is built to run autonomously under PVC without a human clicking yes.
- **No persistent state** — no memory system, no self-authored skills; injected instructions die
  with the session instead of becoming a permanent skill or memory entry.
- **No remote surfaces** — no messaging gateways, no bot mode, no self-updater; the attack surface
  is the open IDE workspace.

Where Cline is **more** dangerous (the corporate trap):
- **Per-machine sprawl** — 10 laptops × 10 configs, each holding provider creds, each pointed
  wherever the dev sets it. No choke point. Hermes centralized = one place to enforce and audit.
- **No managed-scope equivalent** — VS Code gives you no IT-pinned config layer; enforcement is
  endpoint egress rules + policy, not the tool itself.
- **Full user privileges** — Cline's terminal runs as the dev's normal Windows account on a corp
  laptop; centralized hermes runs as a scoped service account.

**CPS verdict:** allow Cline on dev machines **only if** (a) locked to the claimcheck lane
(endpoint egress rule + base-URL policy; today's Cline→claimcheck→OpenAI path is exactly this),
(b) command auto-approve stays off, (c) MCP limited to vetted servers, (d) no provider keys —
lane-issued tokens only. Hermes: server-side only, under managed scope. The two non-negotiables
(egress + no provider creds on the endpoint) are identical for both — that's why predator lanes
are the answer to "how do we let devs use any agent" rather than picking the "safe" agent.

---

## Hardening quick-reference (the setup sequence)

```bash
# 1. install pinned version as the service account (no hermes update ever)
# 2. managed scope — admin-owned
/etc/hermes/config.yaml:  model.default → claimcheck lane model
                          security.redact_secrets: true
                          terminal.backend: docker
/etc/hermes/.env:         provider key (lane token), HERMES_MANAGED_DIR fixed in service unit
# 3. verify
hermes doctor                       # managed dir + pinned key count
hermes config set model.default x   # must REFUSE
hermes config get security.redact_secrets
# 4. environment audit
env | grep -i yolo                  # must be empty
systemctl status hermes-gateway     # must not exist
# 5. data dirs ACL'd to service account; skills/cron/memories reviewed + scheduled for audit
```
