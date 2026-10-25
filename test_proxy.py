#!/usr/bin/env python3
"""End-to-end test of the pseudonymizing proxy: Cline-style request -> proxy -> mock upstream.
Asserts: (1) upstream NEVER sees raw PII, (2) client ALWAYS sees restored values,
(3) SSE placeholders split across chunks are restored cleanly, (4) vault persistence."""
import json
import socket
import subprocess
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).parent
DLL = HERE / "bin" / "Release" / "net9.0" / "ClaimCheck.dll"
FEIN = "27-9415062"

def free_port() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p

PORT = free_port()  # never hardcode 8766 — the production proxy service owns it

def post_raw(body: dict) -> str:
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/v1/chat/completions",
                                 data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=15).read().decode()

def main() -> None:
    pv = Path.home() / ".sanitize-mcp" / "vaults" / "proxy.json"
    if pv.exists():
        pv.unlink()  # deterministic numbering

    mock = subprocess.Popen(["python3", str(HERE / "mock_upstream.py")])
    proxy = subprocess.Popen(["dotnet", str(DLL), "--proxy", "--upstream", "http://127.0.0.1:9988", "--port", str(PORT)],
                             stderr=subprocess.DEVNULL)
    try:
        for _ in range(60):
            try:
                urllib.request.urlopen(f"http://127.0.0.1:{PORT}/health", timeout=1).read()
                break
            except Exception:
                time.sleep(0.2)
        else:
            raise RuntimeError("proxy never became healthy")

        # ---- non-streaming
        resp = json.loads(post_raw({"model": "t", "stream": False, "messages": [
            {"role": "user", "content": f"The employer FEIN is {FEIN}. What is it?"}]}))
        out = resp["choices"][0]["message"]["content"]
        assert FEIN in out, f"restore to client failed: {out!r}"
        seen = json.loads(Path("/tmp/mock-last-request.json").read_text())
        sent = json.dumps(seen)
        assert FEIN not in sent, "LEAK: upstream saw raw FEIN"
        assert "[FEIN_1]" in sent, f"upstream got no placeholder: {sent[:200]}"
        print("non-stream OK — upstream saw [FEIN_1], client saw real FEIN")

        # ---- streaming (placeholder deliberately split across chunks by the mock)
        raw = post_raw({"model": "t", "stream": True, "messages": [
            {"role": "user", "content": f"The employer FEIN is {FEIN}."}]})
        deltas = ""
        for line in raw.splitlines():
            if line.startswith("data: {"):
                d = json.loads(line[6:])["choices"][0]["delta"].get("content", "")
                deltas += d
        assert FEIN in deltas, f"stream restore failed: {deltas!r}"
        assert "[FE" not in deltas, f"split placeholder artifact in stream: {deltas!r}"
        seen = json.loads(Path("/tmp/mock-last-request.json").read_text())
        assert FEIN not in json.dumps(seen), "stream path leaked FEIN upstream"
        print("stream OK — split placeholder restored cleanly:", deltas)

        # ---- encoded blob (base64 .env-style dump — the `base64 .env` leak class)
        import base64
        blob = base64.b64encode(b"FEIN=27-9415062\\nACCOUNT=AK-12345678\\nTOKEN=hunter2secret").decode()
        raw2 = post_raw({"model": "t", "stream": False, "messages": [
            {"role": "user", "content": f"Config dump: {blob}"}]})
        seen = json.loads(Path("/tmp/mock-last-request.json").read_text())
        sent = json.dumps(seen)
        assert blob not in sent, "encoded blob leaked upstream"
        assert "[ENCODED_" in sent, f"encoded blob not masked: {sent[:300]}"
        assert FEIN not in sent, "decoded FEIN leaked upstream"
        print("encoded blob OK — upstream saw [ENCODED_…], blob and decoded value never left")

        # ---- vault persisted
        assert pv.exists() and "FEIN_1" in pv.read_text(), "vault not persisted"
        print("vault persistence OK:", pv)

        print("\nALL PROXY CHECKS PASSED")
    finally:
        mock.terminate()
        proxy.terminate()

main()
