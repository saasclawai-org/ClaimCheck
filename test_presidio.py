#!/usr/bin/env python3
"""Presidio sidecar pass regression (2026-10-26 spike follow-up).

The pass is env-gated (SANITIZE_PRESIDIO_URL). Checks, against stub presidio
analyzer + stub upstream + a real proxy instance:
  1. Presidio-detected spans (CRYPTO wallet) are masked via the vault with
     entity-type placeholders, and the presidio stub was actually consulted.
  2. Default exclusion: DATE_TIME findings are dropped (spike FP: benign prose).
  3. Tier separation: built-in-covered values (FEIN) are masked by the
     deterministic tier before presidio runs — no double-masking.
  4. Fail-soft: unreachable presidio -> pass skipped, request still completes,
     failure counter reported via /status.

Run: python3 test_presidio.py   (builds Release first if the DLL is missing)
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
PRESIDIO_HITS = []  # every path the presidio stub received


class PresidioStub(BaseHTTPRequestHandler):
    def _h(self):
        n = int(self.headers.get("Content-Length") or 0)
        text = ""
        if n:
            try:
                text = json.loads(self.rfile.read(n)).get("text", "")
            except Exception:
                pass
        PRESIDIO_HITS.append(self.path)
        ents = []
        i = text.find("bc1q")
        if i >= 0:
            ents.append({"entity_type": "CRYPTO", "start": i, "end": i + 42, "score": 1.0})
        m = text.find("May and June")
        if m >= 0:
            ents.append({"entity_type": "DATE_TIME", "start": m, "end": m + 12, "score": 0.9})
        k = text.find("Klaus")
        if k >= 0:
            ents.append({"entity_type": "PERSON", "start": k, "end": k + 5, "score": 0.9})
        out = json.dumps(ents).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(out)))
        self.end_headers()
        self.wfile.write(out)

    do_POST = _h
    do_GET = _h

    def log_message(self, *a):
        pass


RECORDED_BODIES = []  # request bodies the upstream received = the scrubbed text the model would see


class UpstreamStub(BaseHTTPRequestHandler):
    def _h(self):
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n).decode("utf-8", "replace") if n else ""
        RECORDED_BODIES.append(body)
        out = json.dumps({"choices": [{"message": {"role": "assistant", "content": "ok"}}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(out)))
        self.end_headers()
        self.wfile.write(out)

    do_GET = do_POST = _h

    def log_message(self, *a):
        pass


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def start_proxy(upstream, presidio_url):
    home = tempfile.mkdtemp(prefix="cc-presidio-test-home-")
    port = free_port()
    err = tempfile.NamedTemporaryFile(prefix="cc-presidio-stderr-", suffix=".log", delete=False)
    env = dict(os.environ, HOME=home, SANITIZE_PRESIDIO_URL=presidio_url)
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


def post(base, path, content):
    body = json.dumps({"model": "batmodel", "messages": [{"role": "user", "content": content}]}).encode()
    req = urllib.request.Request(base + path, data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)


def status(base):
    with urllib.request.urlopen(base + "/status", timeout=10) as r:
        return json.load(r)


def main():
    if not os.path.exists(DLL):
        print("building Release...")
        subprocess.run(["dotnet", "build", "-c", "Release"], cwd=REPO, check=True, capture_output=True)

    psrv = ThreadingHTTPServer(("127.0.0.1", 0), PresidioStub)
    threading.Thread(target=psrv.serve_forever, daemon=True).start()
    presidio = f"http://127.0.0.1:{psrv.server_address[1]}"

    usrv = ThreadingHTTPServer(("127.0.0.1", 0), UpstreamStub)
    threading.Thread(target=usrv.serve_forever, daemon=True).start()
    upstream = f"http://127.0.0.1:{usrv.server_address[1]}"

    failures = []

    # Case 1: presidio ON — CRYPTO detected and masked via the vault
    proc, base, errlog, home = start_proxy(upstream, presidio)
    try:
        r = post(base, "/v1/chat/completions", "wallet bc1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh here")
        body = RECORDED_BODIES[-1]  # the scrubbed text the model would see
        if "[CRYPTO_" not in body:
            failures.append(f"1 presidio CRYPTO not masked in forwarded body: {body!r}")
        if "bc1q" in body:
            failures.append(f"1 raw wallet leaked in forwarded body: {body!r}")
        if len(PRESIDIO_HITS) < 1:
            failures.append("1 presidio stub was never consulted")
    finally:
        proc.terminate(); proc.wait(timeout=10); shutil.rmtree(home, ignore_errors=True)

    # Case 2: DATE_TIME default-excluded — prose survives, no mint
    PRESIDIO_HITS.clear()
    proc, base, errlog, home = start_proxy(upstream, presidio)
    try:
        r = post(base, "/v1/chat/completions", "May and June are good months")
        body = RECORDED_BODIES[-1]
        if "[DATE_TIME" in body:
            failures.append(f"2 DATE_TIME masked (default-excluded tier violated): {body!r}")
        if "May and June" not in body:
            failures.append(f"2 benign prose corrupted: {body!r}")
    finally:
        proc.terminate(); proc.wait(timeout=10); shutil.rmtree(home, ignore_errors=True)

    # Case 3: tier separation — FEIN masked by the deterministic tier, presidio not
    # double-masking and placeholders not re-flagged
    PRESIDIO_HITS.clear()
    proc, base, errlog, home = start_proxy(upstream, presidio)
    try:
        r = post(base, "/v1/chat/completions", "Client FEIN 04-2105393 and Klaus from Berlin")
        body = RECORDED_BODIES[-1]
        if "[FEIN_" not in body:
            failures.append(f"3 FEIN not masked by deterministic tier: {body!r}")
        if body.count("[FEIN_") > 1:
            failures.append(f"3 FEIN double-masked: {body!r}")
        if "[PERSON_" not in body:
            failures.append(f"3 presidio PERSON not masked: {body!r}")
        if "04-2105393" in body or "Klaus" in body:
            failures.append(f"3 raw FEIN/name leaked: {body!r}")
    finally:
        proc.terminate(); proc.wait(timeout=10); shutil.rmtree(home, ignore_errors=True)

    # Case 4: fail-soft — dead presidio URL must not break the lane
    PRESIDIO_HITS.clear()
    proc, base, errlog, home = start_proxy(upstream, "http://127.0.0.1:1/analyze")
    try:
        r = post(base, "/v1/chat/completions", "wallet bc1q here")
        body = RECORDED_BODIES[-1]
        if "bc1q" not in body:
            failures.append(f"4 fail-soft passthrough missing raw wallet: {body!r}")
        if status(base).get("presidioFailures", 0) < 1:
            failures.append("4 fail-soft: presidio failure counter not reported")
    finally:
        proc.terminate(); proc.wait(timeout=10); shutil.rmtree(home, ignore_errors=True)

    psrv.shutdown()
    usrv.shutdown()

    if failures:
        print("test_presidio: FAIL")
        for f in failures:
            print("  -", f)
        sys.exit(1)
    print("test_presidio: ALL PASSED — presidio masking, DATE_TIME exclusion, tier separation, fail-soft")


if __name__ == "__main__":
    main()
