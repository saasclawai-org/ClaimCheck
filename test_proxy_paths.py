#!/usr/bin/env python3
"""Proxy path-composition regression test (2026-10-24 OpenClaw version-difference issue).

Different OpenClaw builds construct provider URLs differently:
  - some send the full provider path:  POST /api/coding/paas/v4/chat/completions
  - some send only:                    POST /chat/completions (base URL carries the path)

The proxy must compose BOTH client shapes to the SAME upstream URL — never double-prefixing,
never relying on upstream URL normalization. Also covers: GET passthrough dedup, root-upstream
passthrough unchanged, SANITIZE_PROXY_LOG=1 request logging.

Run: python3 test_proxy_paths.py   (builds Release first if the DLL is missing)
"""
import json
import os
import shutil
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

HITS = []  # every path the stub upstream receives, in order


class Stub(BaseHTTPRequestHandler):
    def _hit(self):
        HITS.append(self.path)
        # Drain request body before responding (unread body + close can RST mid-flight).
        n = int(self.headers.get("Content-Length") or 0)
        if n:
            self.rfile.read(n)
        body = json.dumps({"ok": True, "path": self.path}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    do_GET = do_POST = _hit

    def log_message(self, *a):  # silence
        pass


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def start_proxy(upstream, log=False):
    home = tempfile.mkdtemp(prefix="cc-proxy-test-home-")  # isolate vault writes from the real vault
    port = free_port()
    err = tempfile.NamedTemporaryFile(prefix="cc-proxy-stderr-", suffix=".log", delete=False)
    env = dict(os.environ, HOME=home)
    if log:
        env["SANITIZE_PROXY_LOG"] = "1"
    proc = subprocess.Popen(
        ["dotnet", DLL, "--proxy", "--upstream", upstream, "--port", str(port)],
        cwd=REPO, env=env, stdout=subprocess.DEVNULL, stderr=err)
    base = f"http://127.0.0.1:{port}"
    for _ in range(60):
        try:
            urllib.request.urlopen(base + "/health", timeout=1).read()
            return proc, base, err.name, home
        except Exception:
            time.sleep(0.2)
    proc.kill()
    err.seek(0)
    raise RuntimeError(f"proxy not healthy: {err.read().decode()[-800:]}")


def request(base, path, method):
    data = json.dumps({"model": "test-model", "messages": [{"role": "user", "content": "hi"}], "max_tokens": 8}).encode() if method == "POST" else None
    req = urllib.request.Request(base + path, data=data, method=method,
                                 headers={"Content-Type": "application/json"} if data else {})
    with urllib.request.urlopen(req, timeout=15) as r:
        return r.status, r.read()


def wait_hits(n):
    for _ in range(80):
        if len(HITS) >= n:
            return
        time.sleep(0.1)
    raise RuntimeError(f"stub received {len(HITS)} hits, expected {n}")


def main():
    if not os.path.exists(DLL):
        print("building Release...")
        subprocess.run(["dotnet", "build", "-c", "Release"], cwd=REPO, check=True, capture_output=True)

    srv = ThreadingHTTPServer(("127.0.0.1", 0), Stub)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    stub = f"http://127.0.0.1:{srv.server_address[1]}"
    failures = []

    FULL_CHAT = "/api/coding/paas/v4/chat/completions"
    FULL_MODELS = "/api/coding/paas/v4/models"

    # ---- Case A: prefixed upstream — every client shape must land on the same paths ----
    proc, base, errlog, home = start_proxy(f"{stub}/api/coding/paas/v4/", log=True)
    try:
        request(base, "/chat/completions", "POST")                  # older-build shape
        request(base, FULL_CHAT, "POST")                            # newer-build shape
        request(base, FULL_MODELS, "GET")                           # passthrough dedup
        wait_hits(3)
        if HITS[0] != FULL_CHAT:
            failures.append(f"A1 bare shape: stub saw {HITS[0]!r}, want {FULL_CHAT!r}")
        if HITS[1] != FULL_CHAT:
            failures.append(f"A2 full shape: stub saw {HITS[1]!r}, want {FULL_CHAT!r} (prefix not de-duplicated)")
        if HITS[2] != FULL_MODELS:
            failures.append(f"A3 GET passthrough: stub saw {HITS[2]!r}, want {FULL_MODELS!r}")
    finally:
        proc.terminate()
        proc.wait(timeout=10)
        logged = open(errlog).read()
        shutil.rmtree(home, ignore_errors=True)
        if "-> 200" not in logged:
            failures.append("A4 SANITIZE_PROXY_LOG=1: no '-> 200' request lines in proxy stderr")

    # ---- Case B: root upstream — full client shape passes through unchanged ----
    HITS.clear()
    proc, base, errlog, home = start_proxy(stub + "/")
    try:
        request(base, FULL_CHAT, "POST")
        wait_hits(1)
        if HITS[0] != FULL_CHAT:
            failures.append(f"B1 root upstream: stub saw {HITS[0]!r}, want {FULL_CHAT!r}")
    finally:
        proc.terminate()
        proc.wait(timeout=10)
        shutil.rmtree(home, ignore_errors=True)

    # ---- Case C: root upstream + /v1 client path (vLLM/OpenAI-style lanes, 2026-10-26) ----
    # HandleChat must NOT strip the client's /v1 when the upstream has no provider prefix:
    # stripping lands on /chat/completions and vLLM 404s. Regression for the predator lane.
    HITS.clear()
    proc, base, errlog, home = start_proxy(stub + "/")
    try:
        request(base, "/v1/chat/completions", "POST")
        request(base, "/v1/models", "GET")
        wait_hits(2)
        if HITS[0] != "/v1/chat/completions":
            failures.append(f"C1 root upstream /v1 POST: stub saw {HITS[0]!r}, want '/v1/chat/completions' (v1 stripped?)")
        if HITS[1] != "/v1/models":
            failures.append(f"C2 root upstream /v1 GET: stub saw {HITS[1]!r}, want '/v1/models'")
    finally:
        proc.terminate()
        proc.wait(timeout=10)
        shutil.rmtree(home, ignore_errors=True)

    srv.shutdown()

    if failures:
        print("test_proxy_paths: FAIL")
        for f in failures:
            print("  -", f)
        sys.exit(1)
    print("test_proxy_paths: ALL PASSED — bare-origin and full-path client shapes compose identically; no double-prefix; request log active")


if __name__ == "__main__":
    main()
