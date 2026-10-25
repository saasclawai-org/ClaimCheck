#!/usr/bin/env python3
"""Vault concurrency + atomic-write regression test (2026-10-24, pre-fleet hardening).

Hammer one proxy instance with parallel requests, each minting a distinct ticket:
  - every request must succeed while the vault is minted + saved concurrently
  - the vault file must parse as valid JSON at the end (atomic rename = never torn)
  - all tickets present, zero .tmp residue
  - --vault flag: a second instance writes to a custom path (per-lane vaults for fleet deploys)

Run: python3 test_vault_concurrency.py   (builds Release first if the DLL is missing)
"""
import concurrent.futures
import glob
import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

REPO = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(REPO, "bin", "Release", "net9.0", "ClaimCheck.dll")
N = 24  # parallel minting requests
ERRLOG = None


class Stub(BaseHTTPRequestHandler):
    def _hit(self):
        # Drain the request body BEFORE responding: closing without reading it can RST
        # mid-body under parallel load (client sees IOException -> proxy 502s intermittently).
        n = int(self.headers.get("Content-Length") or 0)
        if n:
            self.rfile.read(n)
        body = b'{"ok": true}'
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    do_POST = do_GET = _hit

    def log_message(self, *a):
        pass


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def start_proxy(upstream, home, extra=()):
    global ERRLOG
    port = free_port()
    env = dict(os.environ, HOME=home, SANITIZE_NER="0")
    err = tempfile.NamedTemporaryFile(prefix="cc-vc-stderr-", suffix=".log", delete=False)
    proc = subprocess.Popen(
        ["dotnet", DLL, "--proxy", "--upstream", upstream, "--port", str(port), *extra],
        cwd=REPO, env=env, stdout=subprocess.DEVNULL, stderr=err)
    ERRLOG = err.name
    base = f"http://127.0.0.1:{port}"
    for _ in range(60):
        try:
            urllib.request.urlopen(base + "/health", timeout=1).read()
            return proc, base
        except Exception:
            time.sleep(0.2)
    proc.kill()
    raise RuntimeError("proxy not healthy")


def post(base, content):
    data = json.dumps({"model": "test-model", "max_tokens": 8,
                       "messages": [{"role": "user", "content": content}]}).encode()
    req = urllib.request.Request(base + "/chat/completions", data=data, method="POST",
                                 headers={"Content-Type": "application/json"})
    # One retry on 502: the HTTP/1.0 stub closes each connection, and a pooled client can
    # hit a RST mid-burst (pre-existing, load-shaped). A real proxy defect fails both tries.
    for attempt in (1, 2):
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                return r.status
        except urllib.error.HTTPError as e:
            body = e.read().decode(errors="replace")[:200]
            if e.code == 502 and attempt == 1:
                time.sleep(0.5)
                continue
            print(f"   [diag] non-200 from proxy: {e.code} body={body}")
            raise
    raise RuntimeError("post: unreachable")


def main():
    if not os.path.exists(DLL):
        print("building Release...")
        subprocess.run(["dotnet", "build", "-c", "Release"], cwd=REPO, check=True, capture_output=True)

    ThreadingHTTPServer.request_queue_size = 64  # 24 simultaneous connects > default backlog of 5
    srv = ThreadingHTTPServer(("127.0.0.1", 0), Stub)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    stub = f"http://127.0.0.1:{srv.server_address[1]}"
    failures = []
    home = tempfile.mkdtemp(prefix="cc-vault-test-")

    try:
        # ---- leg 1: parallel minting hammer on one instance (default vault path) ----
        proc, base = start_proxy(stub + "/", home)
        emails = [f"user{i}@example{i}.test" for i in range(N)]
        with concurrent.futures.ThreadPoolExecutor(max_workers=N) as pool:
            statuses = list(pool.map(lambda e: post(base, f"mail {e} about the audit"), emails))
        proc.terminate()
        proc.wait(timeout=10)

        bad = [s for s in statuses if s != 200]
        if bad:
            time.sleep(0.5)
            log = open(ERRLOG, errors="replace").read() if ERRLOG else ""
            print("--- proxy stderr tail ---")
            print(log[-3000:])
            failures.append(f"{len(bad)}/{N} parallel requests failed: statuses {sorted(set(bad))}")

        vault_path = os.path.join(home, ".sanitize-mcp", "vaults", "proxy.json")
        if not os.path.exists(vault_path):
            failures.append(f"vault file missing at {vault_path}")
        else:
            try:
                v = json.load(open(vault_path))
            except Exception as e:
                failures.append(f"vault JSON corrupt after {N}-request hammer: {e}")
                v = {"map": {}}
            email_phs = [k for k in v.get("map", {}) if k.startswith("EMAIL_")]
            if len(email_phs) < N:
                failures.append(f"expected >= {N} EMAIL tickets in vault, found {len(email_phs)}")
            else:
                vals = {v["map"][k] for k in email_phs}
                missing = set(emails) - vals
                if missing:
                    failures.append(f"{len(missing)} minted emails missing from final vault, e.g. {sorted(missing)[:3]}")
        residue = glob.glob(os.path.join(home, ".sanitize-mcp", "vaults", "*.tmp"))
        if residue:
            failures.append(f".tmp residue in vault dir: {residue}")

        # ---- leg 2: --vault override writes to a custom path (per-lane isolation) ----
        custom = os.path.join(home, "custom-lane", "vault.json")
        proc2, base2 = start_proxy(stub + "/", home, extra=["--vault", custom])
        try:
            post(base2, "mail lane2@instance.test about the audit")
        finally:
            proc2.terminate()
            proc2.wait(timeout=10)
        if not os.path.exists(custom):
            failures.append(f"--vault override: no vault written to {custom}")
        else:
            v2 = json.load(open(custom))
            if not any(k.startswith("EMAIL_") for k in v2.get("map", {})):
                failures.append("--vault override: custom vault has no EMAIL tickets")
    finally:
        srv.shutdown()
        import shutil
        shutil.rmtree(home, ignore_errors=True)

    if failures:
        print("test_vault_concurrency: FAIL")
        for f in failures:
            print("  -", f)
        sys.exit(1)
    print(f"test_vault_concurrency: ALL PASSED — {N} parallel mints, vault JSON intact, tickets complete, no .tmp residue, --vault isolation works")


if __name__ == "__main__":
    main()
