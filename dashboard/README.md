# ClaimCheck Dashboard (v0.1)

Read-only operator console for ClaimCheck **proxy lanes** (`claimcheck-proxy@<lane>`).
One page shows lane health, scrub posture, request volume, ticket analytics, and a
grep-replacement audit browser. Purely a read-only sidecar — it never touches the scrub
path, never holds vault keys, and never displays vault values. Restore stays CLI-only.

**Design doc:** `docs/claimcheck-predator-dashboard-2026-11-09.md` (repo root `docs/`).

## What it reads

- Lane `/status` JSON, polled every 15s over localhost (fields verified against Proxy.cs v0.9.x)
- Lane audit JSONL (`SANITIZE_PROXY_LOG=<path>`), tailed every 2s into a SQLite sidecar (WAL):
  request metadata + ticket counts + hourly rollups. Bodies stay in the JSONL (single source
  of truth); the browser reads lines straight from the file at their recorded byte offset.

## What it never does

- No scrub form (a paste-secrets box trains the habit ClaimCheck exists to kill)
- No reveal/restore (the app holds no vault password, so it *can't* decrypt)
- No writes to lanes (restarts stay `systemctl`)
- No external network calls at all

## Files

```
ClaimCheck.Dashboard.csproj   net9.0, Microsoft.Data.Sqlite only
Program.cs                    minimal API endpoints (all GET, Cache-Control: no-store)
LaneRegistry.cs               dashboard-lanes.json loader, hot-reload on file change
IngestService.cs              2s JSONL tail + 15s /status poll loop
AuditFile.cs                  read-only JSONL access: tail window, forward search, line-at-offset
Db.cs                         SQLite schema, ingest batch, page queries
Render.cs                     server-rendered HTML + SVG charts (no JS build; htmx optional)
wwwroot/app.css               dark theme
wwwroot/htmx.min.js           optional; overview auto-refresh every 15s (falls back to reload script)
```

## Run

```
dotnet run --project dashboard --no-launch-profile -- \
  --lanes ./dashboard-lanes.json --db ./dashboard.db --urls http://127.0.0.1:8770
```

Args: `--lanes` (env `CC_DASHBOARD_LANES`), `--db` (env `CC_DASHBOARD_DB`), `--urls`
(default `http://127.0.0.1:8770` — **binds localhost only by design**).

## Lane registry format

```json
[
  { "name": "anthropic-prod", "port": 8801, "audit": "/home/norman/claimcheck/audit/anthropic-prod.jsonl" },
  { "name": "qwen-test", "port": 8803, "audit": null, "note": "status-only lane" }
]
```

- `name`: URL-safe (`[A-Za-z0-9._-]`), must match the lane identity you want in the UI
- `port`: lane's localhost port (the `--port` given to that `claimcheck-proxy@<lane>` instance)
- `audit`: the lane's `SANITIZE_PROXY_LOG` file path; `null` → status-only lane
- The file is re-checked every ingest tick — adding a lane needs no restart

Finding the values on predator:

```
systemctl cat 'claimcheck-proxy@*' | grep -E 'ExecStart|SANITIZE_PROXY_LOG|EnvironmentFile'
cat /etc/systemd/system/claimcheck-proxy@.service   # ExecStart shows ${PROXY_PORT}, env file path
```

## systemd (predator layout)

```
sudo cp deploy/claimcheck-dashboard.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now claimcheck-dashboard
curl -s http://127.0.0.1:8770/healthz      # → ok
```

Access: `ssh -L 8770:127.0.0.1:8770 predator` → http://127.0.0.1:8770
(If remote access is ever wanted, it must go through CF Access service tokens — no homemade login.)

## Security posture

- Binds 127.0.0.1; no TLS/login because nothing remote can reach it
- Holds zero secrets: no vault passwords, no upstream API keys, no tenant tokens
- Everything rendered is post-scrub by construction (audit bodies are tickets-only)
- Unit hardening: ProtectSystem=strict, ProtectHome=read-only, ReadWritePaths only the
  claimcheck dir, NoNewPrivileges, PrivateTmp

## v0.2 (planned, backward-compatible)

Proxy audit line gains `lane`, `tenant`, `status`, `ms`, `model` → latency + fail-closed
history pages light up; logrotate for audit files. v0.1 already parses those fields if present.
