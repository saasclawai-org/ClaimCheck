#!/usr/bin/env python3
"""Proxy fail-closed pass-through gate (v0.9.7).

Body-carrying requests (POST/PUT/PATCH) to any path other than the scrubbed
endpoints (chat/completions; /v1/responses since v0.9.19) are refused with a
400 by default — content can never cross unsanitized invisibly (the
/v1/responses field report: requests crossed unsanitized while the caller
believed they were protected).

SANITIZE_PASSTHROUGH re-opens lanes: comma-separated case-insensitive path
suffixes, or "*" for blanket pass-through (pre-0.9.7 behavior).

Run: python3 test_proxy_failclosed.py
"""
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

REPO = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(REPO, "bin", "Release", "net9.0", "ClaimCheck.dll")

UPSTREAM_PORT = 9931
PROXY_PORT = 9932
SECRET = "sk-test-abc123def456ghi789"

HITS = []  # (path, body) the stub upstream received


class Upstream(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode("utf-8", "replace") if length else ""
        HITS.append((self.path, body))
        payload = json.dumps({"choices": [{"message": {"content": "ok"}}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        payload = json.dumps({"object": "list", "data": []}).encode()
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


def post(port, path, body=b'{"model":"x","input":"hello"}', method="POST"):
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}/{path.lstrip('/')}", data=body, method=method,
        headers={"Content-Type": "application/json", "Authorization": "Bearer t"})
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


def start_proxy(env_extra):
    env = dict(os.environ)
    env.update(env_extra)
    proc = subprocess.Popen(
        ["dotnet", DLL, "--proxy", "--upstream", f"http://127.0.0.1:{UPSTREAM_PORT}",
         "--port", str(PROXY_PORT)],
        env=env)
    wait_health(proc, PROXY_PORT)
    return proc


def stop(proc):
    proc.terminate()
    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()


def main():
    if not os.path.exists(DLL):
        subprocess.run(["dotnet", "build", "-c", "Release"], cwd=REPO, check=True,
                       capture_output=True)
    srv = ThreadingHTTPServer(("127.0.0.1", UPSTREAM_PORT), Upstream)
    threading_upstream = __import__("threading")
    t = threading_upstream.Thread(target=srv.serve_forever, daemon=True)
    t.start()

    fails = []

    # --- default posture: fail closed ---
    proc = start_proxy({})
    try:
        # v0.9.19: /v1/responses is a scrubbed endpoint now (it was the v0.9.7 field
        # report that motivated refusing it) — reaches upstream ticketed, never raw
        code, _ = post(PROXY_PORT, "v1/responses",
                       json.dumps({"model": "x", "input": f"key {SECRET}"}).encode())
        if code != 200:
            fails.append(f"responses: expected 200 (v0.9.19 scrubbed), got {code}")
        if any(SECRET in b for _, b in HITS):
            fails.append("responses leaked raw secret upstream")
        if not any(p.endswith("/v1/responses") for p, _ in HITS):
            fails.append(f"responses never reached upstream: {HITS}")
        HITS.clear()
        code, _ = post(PROXY_PORT, "v1/embeddings")
        if code != 400:
            fails.append(f"embeddings: expected 400, got {code}")
        code, _ = post(PROXY_PORT, "v1/audio/transcriptions", method="PUT")
        if code != 400:
            fails.append(f"PUT non-chat: expected 400, got {code}")
        if HITS:
            fails.append(f"refused requests reached upstream: {HITS}")
        code, _ = post(PROXY_PORT, "v1/models", method="GET")
        if code != 200:
            fails.append(f"GET passthrough: expected 200, got {code}")
        HITS.clear()
        code, _ = post(PROXY_PORT, "v1/chat/completions",
                       json.dumps({"model": "x", "messages": [{"role": "user", "content": SECRET}]}).encode())
        if code != 200:
            fails.append(f"chat/completions: expected 200, got {code}")
        leaked = any(SECRET in b for _, b in HITS)
        fails.append("chat/completions leaked raw secret upstream") if leaked else None
    finally:
        stop(proc)

    # --- allowlist lane ---
    HITS.clear()
    proc = start_proxy({"SANITIZE_PASSTHROUGH": "v1/embeddings"})
    try:
        code, _ = post(PROXY_PORT, "v1/embeddings")
        if code != 200:
            fails.append(f"allowlisted embeddings: expected 200, got {code}")
        # v0.9.19: responses is scrubbed natively — needs no allowlist entry anymore
        code, _ = post(PROXY_PORT, "v1/responses")
        if code != 200:
            fails.append(f"responses: expected 200 (scrubbed regardless of allowlist), got {code}")
        if not any(p.endswith("/v1/embeddings") for p, _ in HITS):
            fails.append(f"embeddings never reached upstream: {HITS}")
    finally:
        stop(proc)

    # --- blanket opt-out ---
    HITS.clear()
    proc = start_proxy({"SANITIZE_PASSTHROUGH": "*"})
    try:
        code, _ = post(PROXY_PORT, "v1/responses")
        if code != 200:
            fails.append(f"'*' blanket: expected 200 for responses, got {code}")
    finally:
        stop(proc)
    srv.shutdown()

    if fails:
        print("test_proxy_failclosed: FAILED")
        for f in fails:
            print("  FAIL:", f)
        sys.exit(1)
    print("test_proxy_failclosed: ALL PASSED — default fail-closed, suffix allowlist, blanket opt-out")


if __name__ == "__main__":
    main()
