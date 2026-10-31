#!/usr/bin/env python3
"""Multitenant proxy lanes test (v0.9.6) — SANITIZE_TENANT_HEADER.

Asserts: (1) per-tenant vaults scrub independently (same ticket number, different
values per tenant; continuity within a tenant), (2) responses are NEVER restored —
tickets stay tickets end-to-end, (3) missing/invalid tenant header fails closed
(400), (4) one tenant's PII never appears in another tenant's vault.

Run: python3 test_proxy_multitenant.py
"""
import json
import os
import shutil
import socket
import subprocess
import time
import urllib.request
import urllib.error
from pathlib import Path

HERE = Path(__file__).parent
DLL = HERE / "bin" / "Release" / "net9.0" / "ClaimCheck.dll"
FEIN_A = "27-9415062"   # tenant acme
FEIN_A2 = "58-1234567"  # tenant acme, second value
FEIN_B = "04-2105393"   # tenant globex

def free_port() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p

PORT = free_port()
HOME = Path("/tmp/cc-mt-test")
shutil.rmtree(HOME, ignore_errors=True)

def post(body: dict, tenant: str | None):
    headers = {"Content-Type": "application/json"}
    if tenant is not None:
        headers["X-ClaimCheck-Tenant"] = tenant
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/v1/chat/completions",
                                 data=json.dumps(body).encode(), headers=headers)
    try:
        return 200, urllib.request.urlopen(req, timeout=15).read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()

failures = []
def check(name, cond, detail=""):
    if cond:
        print(f"  ok  {name}")
    else:
        failures.append(name)
        print(f"FAIL  {name}  {detail}")

mock = subprocess.Popen(["python3", str(HERE / "mock_upstream.py")])
proxy = subprocess.Popen(
    ["dotnet", str(DLL), "--proxy", "--upstream", "http://127.0.0.1:9988", "--port", str(PORT)],
    stderr=subprocess.DEVNULL,
    env=dict(os.environ, HOME=str(HOME), SANITIZE_TENANT_HEADER="X-ClaimCheck-Tenant"))
try:
    for _ in range(60):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{PORT}/health", timeout=1).read()
            break
        except Exception:
            time.sleep(0.2)
    else:
        raise RuntimeError("proxy never became healthy")

    # ---- tenant A: scrub works, vault continuity
    rc, body = post({"model": "t", "stream": False,
                     "messages": [{"role": "user", "content": f"Our FEIN is {FEIN_A}."}]}, "acme")
    check("A1 scrub ok", rc == 200)
    sent = json.dumps(json.loads(Path("/tmp/mock-last-request.json").read_text()))
    check("A1 FEIN never upstream", FEIN_A not in sent and "[FEIN_" in sent, sent[:200])
    check("A1 response NOT restored (ticket stays)", f"[FEIN_" in body and FEIN_A not in body, body[:200])

    rc, body = post({"model": "t", "stream": False,
                     "messages": [{"role": "user", "content": f"Also file under {FEIN_A2}."}]}, "acme")
    sent = json.dumps(json.loads(Path("/tmp/mock-last-request.json").read_text()))
    check("A2 continuity: second value gets FEIN_2", "[FEIN_2]" in sent, sent[:200])

    # ---- tenant B: independent lane, same ticket numbers, no cross-tenant bleed
    rc, body = post({"model": "t", "stream": False,
                     "messages": [{"role": "user", "content": f"Our FEIN is {FEIN_B}."}]}, "globex")
    sent = json.dumps(json.loads(Path("/tmp/mock-last-request.json").read_text()))
    check("B1 scrub ok", FEIN_B not in sent and "[FEIN_1]" in sent, sent[:200])
    check("B1 response ticket stays", "[FEIN_1]" in body and FEIN_B not in body, body[:200])

    vaults = sorted((HOME / ".sanitize-mcp" / "vaults").glob("tenant-*.json"))
    check("two tenant vault files", len(vaults) == 2, str([v.name for v in vaults]))
    # each value lives in exactly ONE vault file (map value + reverse key = 2 occurrences there)
    for fein in (FEIN_A, FEIN_A2, FEIN_B):
        hits = [v.name for v in vaults if fein in v.read_text()]
        check(f"isolation: {fein} in exactly one vault", len(hits) == 1, str(hits))

    # ---- fail-closed: missing + invalid tenant header
    rc, body = post({"model": "t", "stream": False,
                     "messages": [{"role": "user", "content": "hi"}]}, None)
    check("missing header -> 400", rc == 400, f"{rc} {body[:100]}")
    rc, body = post({"model": "t", "stream": False,
                     "messages": [{"role": "user", "content": "hi"}]}, "../evil")
    check("invalid tenant id -> 400", rc == 400, f"{rc} {body[:100]}")

finally:
    mock.terminate()
    proxy.terminate()

print()
if failures:
    print(f"{len(failures)} FAILURES: {failures}")
    raise SystemExit(1)
print("all multitenant lane tests passed")
