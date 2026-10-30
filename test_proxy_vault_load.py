#!/usr/bin/env python3
"""Proxy startup vault-load tests (external audit finding, 2026-10-30).

The proxy SAVES its vault encrypted (DPAPI on Windows default, password mode with
SANITIZE_VAULT_PASSWORD) but used to START by reading it with raw File.ReadAllText +
JsonSerializer. Plaintext Linux masked it; on Windows every restart threw in the
catch, silently started with an EMPTY vault, and the next scrubbed request's
SaveVault overwrote the real mapping. Now the startup loads via VaultCrypto and
refuses to start on any unreadable/malformed vault. Scenarios:
  1. password-encrypted vault, password not set  -> proxy refuses (old: started empty)
  2. malformed plaintext vault                   -> proxy refuses (old: started empty)
  3. valid plaintext vault                       -> starts, /status shows entries,
                                                    [FEIN_1] restored from loaded mapping
All isolated under a temp HOME — never touches the real ~/.sanitize-mcp vaults.
"""
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).parent
DLL = HERE / "bin" / "Release" / "net9.0" / "ClaimCheck.dll"
MAGIC = b"CCVE"
FEIN = "27-9415062"
PW = "test-pass-123"


def free_port() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def base_env(home, extra=None) -> dict:
    """Isolated HOME + ambient vault/env knobs cleared so the box's real config
    (a live proxy may run here) can never leak into the test."""
    env = dict(os.environ, HOME=str(home))
    for k in ("SANITIZE_VAULT_PASSWORD", "SANITIZE_FAKE_DATA", "SANITIZE_PROXY_LOG"):
        env.pop(k, None)
    if extra:
        env.update(extra)
    return env


def run_proxy(args, home, timeout=60):
    """Run the proxy in the foreground and wait for exit (refusal scenarios)."""
    return subprocess.run(["dotnet", str(DLL), "--proxy"] + args, cwd=str(HERE),
                          env=base_env(home), capture_output=True, text=True,
                          timeout=timeout)


def expect_refusal(label, args, home, failures):
    port = free_port()
    try:
        r = run_proxy(["--upstream", "http://127.0.0.1:1", "--port", str(port),
                       "--vault", str(args)], home)
    except subprocess.TimeoutExpired:
        failures.append(f"{label}: proxy did NOT exit — started despite unreadable vault")
        return
    if r.returncode == 0:
        failures.append(f"{label}: proxy exited 0 — started despite unreadable vault")
    elif "FATAL" not in r.stderr:
        failures.append(f"{label}: no FATAL on stderr: {r.stderr[-200:]!r}")
    else:
        print(f"{label} OK — exit {r.returncode}, FATAL on stderr")


def main() -> None:
    failures = []

    with tempfile.TemporaryDirectory(prefix="cc-pvault-") as tmp:
        home = Path(tmp)

        # ---- setup: create a password-encrypted vault with the binary itself
        r = subprocess.run(["dotnet", str(DLL), "sanitize-text",
                            f"The employer FEIN is {FEIN}."], cwd=str(HERE),
                           env=base_env(home, {"SANITIZE_VAULT_PASSWORD": PW}),
                           capture_output=True, text=True, timeout=180)
        if r.returncode != 0:
            print(f"setup: sanitize-text failed rc={r.returncode}: {r.stderr[-300:]}")
            sys.exit(1)
        enc = [v for v in (home / ".sanitize-mcp" / "vaults").glob("*.json")
               if v.read_bytes()[:4] == MAGIC]
        if not enc:
            print("setup: no encrypted vault file after password-mode scrub")
            sys.exit(1)

        # ---- 1. encrypted vault + missing password -> refuse to start
        expect_refusal("encrypted-vault (no password)", enc[0], home, failures)

        # ---- 2. malformed plaintext vault -> refuse to start
        bad = home / "bad-vault.json"
        bad.write_bytes(b"\x00\x01{not json at all")
        expect_refusal("malformed-vault", bad, home, failures)

        # ---- 3. valid plaintext vault -> starts, loads, restores from the mapping
        good = home / "good-vault.json"
        good.write_text(json.dumps({
            "map": {"FEIN_1": FEIN},
            "reverse": {"27-9415062|fein": "FEIN_1"},
            "counts": {"FEIN_1": 1},
        }))
        port = free_port()
        mock = subprocess.Popen(["python3", str(HERE / "mock_upstream.py")])
        proxy = subprocess.Popen(["dotnet", str(DLL), "--proxy",
                                  "--upstream", "http://127.0.0.1:9988",
                                  "--port", str(port), "--vault", str(good)],
                                 cwd=str(HERE), env=base_env(home),
                                 stderr=subprocess.DEVNULL)
        try:
            for _ in range(60):
                try:
                    urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1).read()
                    break
                except Exception:
                    time.sleep(0.2)
            else:
                failures.append("valid plaintext vault: proxy never became healthy")
                return finish(failures)

            st = json.loads(urllib.request.urlopen(
                f"http://127.0.0.1:{port}/status", timeout=5).read())
            if st.get("vaultEntries") != 1:
                failures.append(f"valid vault: vaultEntries={st.get('vaultEntries')} (want 1)")

            req = urllib.request.Request(
                f"http://127.0.0.1:{port}/v1/chat/completions",
                data=json.dumps({"model": "t", "stream": False, "messages": [
                    {"role": "user", "content": f"Confirm the employer FEIN: [FEIN_1]"}]}).encode(),
                headers={"Content-Type": "application/json"})
            out = json.loads(urllib.request.urlopen(req, timeout=15).read())
            content = out["choices"][0]["message"]["content"]
            if FEIN not in content:
                failures.append(f"valid vault: loaded mapping not restored: {content!r}")
            else:
                print("valid-vault load OK — vaultEntries=1, [FEIN_1] restored from loaded mapping")
        finally:
            proxy.terminate()
            mock.terminate()

    finish(failures)


def finish(failures) -> None:
    if failures:
        for f in failures:
            print(f"FAIL: {f}")
        print(f"test_proxy_vault_load: {len(failures)} FAILURE(S)")
        sys.exit(1)
    print("test_proxy_vault_load: ALL PASSED")


if __name__ == "__main__":
    main()
