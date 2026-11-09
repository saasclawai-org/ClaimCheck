#!/usr/bin/env python3
"""v0.9.17: OpenAI max_tokens -> max_completion_tokens compat shim (test_proxy_openai_compat).
Cases: (1) SANITIZE_OPENAI_MAXTOKENS=1 forces rename; (2) both params present -> max_tokens
dropped, completion kept; (3) absent -> nothing injected; (4) no override + non-openai
upstream -> auto-detect off, max_tokens passes through untouched."""
import json
import os
import socket
import subprocess
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).parent
DLL = HERE / "bin" / "Release" / "net9.0" / "ClaimCheck.dll"
LAST = Path("/tmp/mock-last-request.json")


def free_port() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def wait_health(port: int) -> None:
    for _ in range(60):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1).read()
            return
        except Exception:
            time.sleep(0.2)
    raise RuntimeError("proxy never became healthy")


def post(port: int, body: dict) -> str:
    req = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions",
                                 data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=15).read().decode()


def start_proxy(env_extra: dict):
    port = free_port()
    env = dict(os.environ)
    env.pop("SANITIZE_OPENAI_MAXTOKENS", None)
    env.update(env_extra)
    proxy = subprocess.Popen(["dotnet", str(DLL), "--proxy", "--upstream", "http://127.0.0.1:9988",
                              "--port", str(port)], env=env, stderr=subprocess.DEVNULL)
    wait_health(port)
    return proxy, port


def check(name: str, cond: bool) -> None:
    print(("PASS: " if cond else "FAIL: ") + name)
    if not cond:
        os._exit(1)


def main() -> None:
    mock = subprocess.Popen(["python3", str(HERE / "mock_upstream.py")])
    time.sleep(0.5)  # if 9988 is already bound, this instance exits silently — one is running

    # ---- phase 1: SANITIZE_OPENAI_MAXTOKENS=1 (force on; a 127.0.0.1 upstream would not auto-detect)
    proxy, port = start_proxy({"SANITIZE_OPENAI_MAXTOKENS": "1"})
    try:
        post(port, {"model": "t", "stream": False, "max_tokens": 123,
                    "messages": [{"role": "user", "content": "hi"}]})
        sent = json.loads(LAST.read_text())
        check("force-on: max_tokens renamed to max_completion_tokens",
              "max_tokens" not in sent and sent.get("max_completion_tokens") == 123)

        post(port, {"model": "t", "stream": False, "max_tokens": 5, "max_completion_tokens": 99,
                    "messages": [{"role": "user", "content": "hi"}]})
        sent = json.loads(LAST.read_text())
        check("both present: max_tokens dropped, completion kept",
              "max_tokens" not in sent and sent.get("max_completion_tokens") == 99)

        post(port, {"model": "t", "stream": False,
                    "messages": [{"role": "user", "content": "hi"}]})
        sent = json.loads(LAST.read_text())
        check("absent: nothing injected",
              "max_tokens" not in sent and "max_completion_tokens" not in sent)
    finally:
        proxy.terminate()

    # ---- phase 2: no override + non-openai upstream -> auto-detect off, untouched passthrough
    proxy, port = start_proxy({})
    try:
        post(port, {"model": "t", "stream": False, "max_tokens": 77,
                    "messages": [{"role": "user", "content": "hi"}]})
        sent = json.loads(LAST.read_text())
        check("auto-detect off: max_tokens untouched",
              sent.get("max_tokens") == 77 and "max_completion_tokens" not in sent)
    finally:
        proxy.terminate()

    mock.terminate()
    print("ALL OPENAI-COMPAT TESTS PASS")


if __name__ == "__main__":
    main()
