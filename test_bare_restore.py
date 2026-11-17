#!/usr/bin/env python3
"""v0.9.23: bare-form ticket restore in the proxy.

Field report 2026-11-17: a model echoed tickets without brackets ("MISC_51")
and with the underscore dropped ("MISC51") — both leaked user-visible because
RestoreText only replaced exact [TYPE_N] forms. The fix: vault-gated bare
restore — a token restores only when it (or its underscore-inserted form) is an
EXACT key in the lane's vault, with the CLI --bare boundary guards (MY_SSN_1
variables, SSN_10 prefixes, SSN_1_extra suffixes stay untouched).

Covers: bracketed restore still works, bare non-stream restore, underscore-
dropped restore, per-character streaming (bare-tail hold), guard tokens, and
the SANITIZE_BARE_RESTORE=0 kill switch."""
import json
import os
import re
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).parent
DLL = HERE / "bin" / "Release" / "net9.0" / "ClaimCheck.dll"
FEIN = "27-9415062"
GUARDS = ["MY_FEIN_1", "FEIN_1_extra", "FEIN_1_10", "FIELD_1"]
TICKET_RX = re.compile(r"(?<![A-Za-z0-9_])FEIN_?1(?![A-Za-z0-9_])")
EXPECTED = f"Report: bare {FEIN}, dropped {FEIN}, guards MY_FEIN_1 FEIN_1_extra FEIN_1_10 and unknown FIELD_1."


def free_port() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def post(port: int, body: dict) -> str:
    req = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions",
                                 data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=15).read().decode()


def wait_health(port: int) -> None:
    for _ in range(60):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1).read()
            return
        except Exception:
            time.sleep(0.2)
    raise RuntimeError("proxy never became healthy")


def stream_text(raw: str) -> str:
    out = ""
    for line in raw.splitlines():
        if line.startswith("data: {"):
            out += json.loads(line[6:])["choices"][0]["delta"].get("content", "")
    return out


def main() -> None:
    home = tempfile.mkdtemp(prefix="cc-bare-")
    env = {**os.environ, "HOME": home}
    port, port0 = free_port(), free_port()
    mock = subprocess.Popen(["python3", str(HERE / "mock_upstream.py")])
    proxy = subprocess.Popen(
        ["dotnet", str(DLL), "--proxy", "--upstream", "http://127.0.0.1:9988", "--port", str(port)],
        env=env, stderr=subprocess.DEVNULL)
    strict = subprocess.Popen(
        ["dotnet", str(DLL), "--proxy", "--upstream", "http://127.0.0.1:9988", "--port", str(port0)],
        env={**env, "SANITIZE_BARE_RESTORE": "0"}, stderr=subprocess.DEVNULL)
    try:
        wait_health(port)
        wait_health(port0)

        # ---- 1. bracketed restore still works + mints FEIN_1 (non-stream)
        out = json.loads(post(port, {"model": "t", "stream": False, "messages": [
            {"role": "user", "content": f"The employer FEIN is {FEIN}."}]}))["choices"][0]["message"]["content"]
        assert FEIN in out, f"bracketed restore broke: {out!r}"
        seen = Path("/tmp/mock-last-request.json").read_text()
        assert FEIN not in seen and "[FEIN_1]" in seen, "upstream mint check failed"
        print("bracketed restore OK — [FEIN_1] minted and restored")

        # ---- 2. bare + underscore-dropped echo restores (non-stream BARE-MODE)
        out = json.loads(post(port, {"model": "t", "stream": False, "messages": [
            {"role": "user", "content": f"BARE-MODE employer FEIN {FEIN}"}]}))["choices"][0]["message"]["content"]
        assert out == EXPECTED, f"bare restore mismatch:\n got: {out!r}\nwant: {EXPECTED!r}"
        assert out.count(FEIN) == 2, f"expected 2 raw FEINs, got {out.count(FEIN)}"
        for g in GUARDS:
            assert g in out, f"guard token {g} was wrongly altered: {out!r}"
        assert not TICKET_RX.search(out), f"ticket literal leaked to client: {out!r}"
        seen = Path("/tmp/mock-last-request.json").read_text()
        assert FEIN not in seen, "LEAK: upstream saw raw FEIN"
        print("bare + dropped restore OK — guards untouched, upstream only ever saw [FEIN_1]")

        # ---- 3. per-character streaming: bare-tail hold carries partial runs
        raw = post(port, {"model": "t", "stream": True, "messages": [
            {"role": "user", "content": f"BARE-STREAM employer FEIN {FEIN}"}]})
        got = stream_text(raw)
        assert got == EXPECTED, f"streamed bare restore mismatch:\n got: {got!r}\nwant: {EXPECTED!r}"
        assert not TICKET_RX.search(got), f"ticket literal leaked in stream: {got!r}"
        print("streaming bare restore OK — per-char fragments reassembled and restored")

        # ---- 4. kill switch: SANITIZE_BARE_RESTORE=0 restores bracketed only
        out = json.loads(post(port0, {"model": "t", "stream": False, "messages": [
            {"role": "user", "content": f"The employer FEIN is {FEIN}."}]}))["choices"][0]["message"]["content"]
        assert FEIN in out, f"kill-switch proxy broke bracketed restore: {out!r}"
        out = json.loads(post(port0, {"model": "t", "stream": False, "messages": [
            {"role": "user", "content": f"BARE-MODE employer FEIN {FEIN}"}]}))["choices"][0]["message"]["content"]
        assert out.count(FEIN) == 0, f"kill switch ignored — bare restored anyway: {out!r}"
        assert TICKET_RX.search(out), f"expected untouched bare ticket, got: {out!r}"
        for g in GUARDS:
            assert g in out, f"guard token {g} missing: {out!r}"
        print("kill switch OK — SANITIZE_BARE_RESTORE=0 keeps bare forms as tickets")

        print("ALL bare-restore checks passed")
    finally:
        for p in (proxy, strict, mock):
            p.terminate()
        try:
            shutil.rmtree(home)
        except OSError:
            pass


if __name__ == "__main__":
    main()
