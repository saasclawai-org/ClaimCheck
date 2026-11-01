#!/usr/bin/env python3
"""SANITIZE_EXEMPT_TYPES (v0.9.9): per-type exemptions skip recognizers entirely —
exempted values pass raw (no tickets, no vault entries); non-exempt types still scrub.

Runs through the proxy e2e path so the exemption is verified where deployments use it.
Run: python3 test_proxy_exempt_types.py
"""
import json
import os
import subprocess
import sys
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

REPO = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(REPO, "bin", "Release", "net9.0", "ClaimCheck.dll")

UPSTREAM_PORT = 9941
PROXY_PORT = 9942

# phone + email must PASS RAW under exemption; SSN (not exempted) must be ticketed
UP_VALUES = {"phone": "555-867-5309", "email": "jane.doe@example.com", "ssn": "318-42-7765"}
SEEN = []  # bodies the stub upstream received


class Upstream(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        SEEN.append(self.rfile.read(length).decode("utf-8", "replace") if length else "")
        payload = json.dumps({"choices": [{"message": {"content": "ok"}}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *a):
        pass


def wait_health(proc, port, timeout=30):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if proc.poll() is not None:
            raise RuntimeError("proxy exited early — see stderr above")
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2) as r:
                if r.read().strip() == b"ok":
                    return
        except Exception:
            time.sleep(0.3)
    raise RuntimeError("proxy never became healthy")


def chat(port, tenant=None):
    msg = {"role": "user", "content": (
        f"call {UP_VALUES['phone']} or email {UP_VALUES['email']} "
        f"ssn {UP_VALUES['ssn']}")}
    body = json.dumps({"model": "x", "messages": [msg]}).encode()
    headers = {"Content-Type": "application/json"}
    if tenant:
        headers["X-Tenant"] = tenant
    req = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions",
                                 data=body, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=15) as r:
        return r.status, r.read().decode()


def status(port):
    with urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=5) as r:
        return json.loads(r.read())


def run(env_extra, vault):
    env = dict(os.environ)
    env.update(env_extra)
    proc = subprocess.Popen(
        ["dotnet", DLL, "--proxy", "--upstream", f"http://127.0.0.1:{UPSTREAM_PORT}",
         "--port", str(PROXY_PORT), "--vault", vault], env=env)
    wait_health(proc, PROXY_PORT)
    return proc


def main():
    if not os.path.exists(DLL):
        subprocess.run(["dotnet", "build", "-c", "Release"], cwd=REPO, check=True,
                       capture_output=True)
    srv = ThreadingHTTPServer(("127.0.0.1", UPSTREAM_PORT), Upstream)
    import threading
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    fails = []

    import tempfile
    v1 = os.path.join(tempfile.mkdtemp(), "v1.json")
    v2 = os.path.join(tempfile.mkdtemp(), "v2.json")
    # default: everything scrubbed (fresh vault — no cross-run contamination)
    proc = run({}, v1)
    try:
        st = status(PROXY_PORT)
        if st.get("exemptTypes") != "":
            fails.append(f"default exemptTypes should be empty, got {st.get('exemptTypes')!r}")
        chat(PROXY_PORT)
        body = SEEN[-1]
        if UP_VALUES["phone"] in body:
            fails.append("default: phone leaked raw")
        if UP_VALUES["email"] in body:
            fails.append("default: email leaked raw")
        if "SSN_" not in body or "PHONE_" not in body or "EMAIL_" not in body:
            fails.append(f"default: expected tickets for all three types, saw: {body[:200]}")
    finally:
        proc.terminate(); proc.wait(timeout=10)

    # exempted phone+email pass raw; SSN still scrubbed; /status reflects it.
    # Fresh vault: an exemption must hold regardless of prior vault entries —
    # but note ScrubKnownValues re-tickets values already in the vault, so a
    # shared vault across config changes WILL re-ticket exempted values once.
    # Run 2 is multitenant (SANITIZE_TENANT_HEADER set) — all chats must carry the header.
    proc = run({"SANITIZE_EXEMPT_TYPES": "phone,email", "SANITIZE_TENANT_HEADER": "X-Tenant"}, v2)
    try:
        st = status(PROXY_PORT)
        if st.get("exemptTypes") != "PHONE,EMAIL":
            fails.append(f"exemptTypes in /status wrong: {st.get('exemptTypes')!r}")
        chat(PROXY_PORT, tenant="prod-1")
        body = SEEN[-1]
        if UP_VALUES["phone"] not in body:
            fails.append("exempted phone did NOT pass raw")
        if UP_VALUES["email"] not in body:
            fails.append("exempted email did NOT pass raw")
        if "SSN_" not in body:
            fails.append(f"non-exempt SSN not scrubbed, saw: {body[:200]}")
        # no-tenant-header request under multitenant mode: fail-closed 400
        code = None
        try:
            req = urllib.request.Request(
                f"http://127.0.0.1:{PROXY_PORT}/v1/chat/completions",
                data=json.dumps({"model": "x", "messages": [{"role": "user", "content": "hi"}]}).encode(),
                method="POST")
            urllib.request.urlopen(req, timeout=10)
            code = 200
        except urllib.error.HTTPError as e:
            code = e.code
        if code != 400:
            fails.append(f"exemption must not weaken tenant gate: expected 400, got {code}")
        chat(PROXY_PORT, tenant="prod-1")
        body = SEEN[-1]
        if UP_VALUES["phone"] not in body or "SSN_" not in body:
            fails.append(f"multitenant+exemption wrong: {body[:200]}")
    finally:
        proc.terminate(); proc.wait(timeout=10)
    srv.shutdown()

    if fails:
        print("test_proxy_exempt_types: FAILED")
        for f in fails:
            print("  FAIL:", f)
        sys.exit(1)
    print("test_proxy_exempt_types: ALL PASSED — default scrub, exemption pass-raw, tenant gate intact")


if __name__ == "__main__":
    main()
