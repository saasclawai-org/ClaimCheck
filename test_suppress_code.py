#!/usr/bin/env python3
"""v0.9.12 SANITIZE_SUPPRESS_CODE tests: fenced code masked in SSE + non-streaming
responses when the flag is on; passes through intact when off. Qwen-style per-char
streaming exercises the cross-chunk fence state machine."""
import json, os, socket, subprocess, time, urllib.request
from pathlib import Path

HERE = Path(__file__).parent
DLL = HERE / "bin" / "Release" / "net9.0" / "ClaimCheck.dll"
MSG = "[code removed"

def free_port():
    s = socket.socket(); s.bind(("127.0.0.1", 0)); p = s.getsockname()[1]; s.close(); return p

def wait_health(port):
    for _ in range(60):
        try: urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1).read(); return
        except Exception: time.sleep(0.2)
    raise RuntimeError("proxy never healthy")

def sse_content(body, port, tag="x"):
    req = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions",
        data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    req_id = str(id(body) % 1000)
    raw = urllib.request.urlopen(req, timeout=15).read().decode()
    open(f"/tmp/raw-sse-{tag}.txt", "w").write(raw)
    out = []
    for line in raw.splitlines():
        if line.startswith("data:") and "[DONE]" not in line:
            try:
                ev = json.loads(line[5:].strip())
                c = ev["choices"][0]["delta"].get("content")
                if c: out.append(c)
            except Exception: pass
    return "".join(out)

def json_content(body, port):
    req = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions",
        data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    resp = json.loads(urllib.request.urlopen(req, timeout=15).read().decode())
    return resp["choices"][0]["message"]["content"]

FENCED = "ECHOMODE Here is code:\n```js\nconst result = 1 + 1;\nconsole.log(result);\n```\nDone."

def main():
    results = []
    mock = subprocess.Popen(["python3", str(HERE / "mock_upstream.py")])

    def run_proxy(suppress: bool):
        port = free_port()
        env = dict(os.environ)
        if suppress: env["SANITIZE_SUPPRESS_CODE"] = "1"
        errf = open(f"/tmp/suppress-proxy-{port}.err", "wb")
        proxy = subprocess.Popen(["dotnet", str(DLL), "--proxy", "--upstream",
            "http://127.0.0.1:9988", "--port", str(port)],
            stderr=errf, env=env)
        wait_health(port)
        st = json.loads(urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=5).read())
        assert st.get("suppressCode") == suppress, f"/status suppressCode={st.get('suppressCode')} expected {suppress}"
        return proxy, port

    try:
        # 1. streaming + flag on: fenced code masked, message + surrounding text kept
        proxy, port = run_proxy(True)
        content = sse_content({"model": "t", "stream": True,
                               "messages": [{"role": "user", "content": FENCED}]}, port, tag="on")
        print("proxy alive after stream:", proxy.poll() is None)
        proxy.terminate()
        ok1 = "code removed" in content and "const result" not in content and "Here is code:" in content and "Done." in content
        results.append(("streaming+flag masks fenced code", ok1, content[:120]))

        # 2. streaming + flag off: code passes intact (per-char stream reassembles)
        proxy, port = run_proxy(False)
        content = sse_content({"model": "t", "stream": True,
                               "messages": [{"role": "user", "content": FENCED}]}, port, tag="off")
        proxy.terminate()
        ok2 = "const result = 1 + 1;" in content and "code removed" not in content
        results.append(("streaming, flag off passes code", ok2, content[:120]))

        # 3. non-streaming + flag on: masked
        proxy, port = run_proxy(True)
        content = json_content({"model": "t", "stream": False,
                                "messages": [{"role": "user", "content": "CODEBLOCK-MODE"}]}, port)
        proxy.terminate()
        ok3 = "code removed" in content and "const result" not in content and "Done." in content
        results.append(("non-streaming+flag masks code", ok3, content[:120]))
    finally:
        mock.terminate()

    fails = 0
    for name, ok, sample in results:
        print(("PASS" if ok else "FAIL"), "|", name, "|", sample.replace("\n", "⏎"))
        if not ok: fails += 1
    raise SystemExit(1 if fails else 0)

if __name__ == "__main__":
    main()
