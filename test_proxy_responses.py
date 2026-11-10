#!/usr/bin/env python3
"""v0.9.19: /v1/responses — Responses API scrub/restore end-to-end.
Asserts: (1) upstream NEVER sees raw FEIN (instructions + input items: message text,
function_call arguments, function_call_output output all ticketed); (2) client always
sees restored values — non-streaming object, streaming deltas split across chunks,
*.done events, response.completed; (3) max_tokens -> max_output_tokens compat shim;
(4) fail-closed still refuses non-scrubbed body paths."""
import json
import os
import socket
import subprocess
import time
import urllib.request
import urllib.error
from pathlib import Path

HERE = Path(__file__).parent
DLL = HERE / "bin" / "Release" / "net9.0" / "ClaimCheck.dll"
LAST = Path("/tmp/mock-last-request.json")
FEIN = "27-9415062"


def free_port() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


PORT = free_port()  # never hardcode 8766 — the production proxy service owns it


def wait_health() -> None:
    for _ in range(60):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{PORT}/health", timeout=1).read()
            return
        except Exception:
            time.sleep(0.2)
    raise RuntimeError("proxy never became healthy")


def post_responses(body: dict, timeout: int = 20) -> str:
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/v1/responses",
                                 data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=timeout).read().decode()


def responses_body(marker: str, stream: bool) -> dict:
    return {
        "model": "gpt-6.1",
        "instructions": f"Employer records use FEIN {FEIN}.",
        "stream": stream,
        "input": [
            {"type": "message", "role": "user",
             "content": [{"type": "input_text",
                          "text": f"{marker} Summarize employer {FEIN} records."}]},
            {"type": "function_call", "id": "fc_prev", "call_id": "call_prev",
             "name": "read_file", "arguments": json.dumps({"path": f"{FEIN}.txt"})},
            {"type": "function_call_output", "call_id": "call_prev",
             "output": f"file contents mention FEIN {FEIN}"},
        ],
    }


def main() -> None:
    pv = Path.home() / ".sanitize-mcp" / "vaults" / "proxy.json"
    if pv.exists():
        pv.unlink()  # deterministic ticket numbering

    mock = subprocess.Popen(["python3", str(HERE / "mock_upstream.py")])
    # SANITIZE_OPENAI_MAXTOKENS=1: the test upstream is 127.0.0.1 (no 'openai' in host),
    # so auto-detect can't fire — force the shim on, same convention as test_proxy_openai_compat
    env = dict(os.environ)
    env["SANITIZE_OPENAI_MAXTOKENS"] = "1"
    proxy = subprocess.Popen(["dotnet", str(DLL), "--proxy", "--upstream", "http://127.0.0.1:9988",
                              "--port", str(PORT)], env=env, stderr=subprocess.DEVNULL)
    try:
        wait_health()

        # ---- 1. non-streaming: request ticketed, response object restored
        out = post_responses(responses_body("RESPONSES-JSON", stream=False))
        resp = json.loads(out)
        sent = LAST.read_text()
        assert FEIN not in sent, f"LEAK: upstream saw raw FEIN: {sent[:300]}"
        assert "[FEIN_1]" in sent, f"upstream got no placeholder: {sent[:300]}"
        txt = resp["output"][1]["content"][0]["text"]
        args = resp["output"][2]["arguments"]
        assert FEIN in txt, f"non-streaming text restore failed: {txt!r}"
        assert FEIN in args, f"non-streaming arguments restore failed: {args!r}"
        assert FEIN in resp.get("instructions", ""), "instructions echo not restored"
        assert "[FEIN_1]" not in json.dumps(resp), "client got raw tickets"
        print("1. non-streaming responses OK — request ticketed, object restored")

        # ---- 2. streaming: deltas split across chunks + done + completed
        raw = post_responses(responses_body("RESPONSES-STREAM", stream=True))
        events = []
        for line in raw.splitlines():
            if line.startswith("data:"):
                payload = line[5:].strip()
                if payload and payload != "[DONE]":
                    events.append(json.loads(payload))
        text_deltas = "".join(e.get("delta", "") for e in events
                              if e.get("type") == "response.output_text.delta")
        args_deltas = "".join(e.get("delta", "") for e in events
                              if e.get("type") == "response.function_call_arguments.delta")
        assert FEIN in text_deltas, f"stream text restore failed: {text_deltas!r}"
        assert "[FEIN_1]" not in text_deltas, f"raw ticket leaked in deltas: {text_deltas!r}"
        assert "[" not in text_deltas, f"partial placeholder fragment leaked: {text_deltas!r}"
        assert FEIN in args_deltas, f"stream arguments restore failed: {args_deltas!r}"
        assert "[FEIN_1]" not in args_deltas, f"raw ticket leaked in args deltas: {args_deltas!r}"
        done_text = next(e for e in events if e.get("type") == "response.output_text.done")["text"]
        assert FEIN in done_text, f"output_text.done not restored: {done_text!r}"
        done_args = next(e for e in events
                         if e.get("type") == "response.function_call_arguments.done")["arguments"]
        assert FEIN in done_args, f"arguments.done not restored: {done_args!r}"
        completed = next(e for e in events if e.get("type") == "response.completed")["response"]
        assert FEIN in json.dumps(completed), "response.completed not restored"
        assert "[FEIN_1]" not in json.dumps(completed), "completed still carries tickets"
        print("2. streaming responses OK — split placeholders restored, done + completed clean")

        # ---- 3. max_tokens -> max_output_tokens compat shim
        body = responses_body("RESPONSES-JSON", stream=False)
        body["max_tokens"] = 100
        post_responses(body)
        sent = LAST.read_text().replace(" ", "")
        assert '"max_output_tokens":100' in sent, f"shim failed: {sent[:200]}"
        assert '"max_tokens"' not in sent, f"max_tokens still present: {sent[:200]}"
        print("3. max_tokens -> max_output_tokens shim OK")

        # ---- 4. fail-closed: non-scrubbed body path still refused
        try:
            req = urllib.request.Request(f"http://127.0.0.1:{PORT}/v1/embeddings",
                                         data=b"{}", headers={"Content-Type": "application/json"})
            urllib.request.urlopen(req, timeout=5)
            raise AssertionError("embeddings should have been refused")
        except urllib.error.HTTPError as e:
            assert e.code == 400, f"expected 400 fail-closed, got {e.code}"
        print("4. fail-closed still refuses unlisted paths OK")

        print("ALL RESPONSES TESTS PASSED")
    finally:
        mock.terminate()
        proxy.terminate()


if __name__ == "__main__":
    main()
