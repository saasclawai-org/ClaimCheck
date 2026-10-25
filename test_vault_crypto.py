#!/usr/bin/env python3
"""Vault encryption tests (v0.6.0): password-mode round-trip, legacy plaintext migration,
wrong-password safety, and Linux plaintext default. Regex tier only."""
import asyncio
import json
import os
import subprocess
import tempfile
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

HERE = Path(__file__).parent
DLL = HERE / "bin" / "Release" / "net9.0" / "ClaimCheck.dll"
MAGIC = b"CCVE"
PII = "FEIN 04-1234567 and SSN 123-45-6789"


async def run_tool(text, home, extra_env=None):
    env = dict(os.environ, HOME=str(home), **(extra_env or {}))
    params = StdioServerParameters(command="dotnet", args=[str(DLL)], cwd=str(HERE), env=env)
    async with stdio_client(params) as (r, w):
        async with ClientSession(r, w, read_timeout_seconds=300) as s:
            await s.initialize()
            return json.loads((await s.call_tool("sanitize_text", {"text": text})).content[0].text)


def cli(args, home, pw=None, stdin_text=None):
    env = dict(os.environ, HOME=str(home))
    if pw is not None:
        env["SANITIZE_VAULT_PASSWORD"] = pw
    r = subprocess.run(["dotnet", str(DLL)] + args, cwd=str(HERE), env=env,
                       input=stdin_text, capture_output=True, text=True)
    return r


async def main():
    pw = "test-pass-123"
    failures = []

    # 1. password mode: sanitize → vault encrypted (magic) → restore with same pw returns values
    with tempfile.TemporaryDirectory(prefix="cc-vcrypt-") as home:
        await run_tool(PII, home, {"SANITIZE_VAULT_PASSWORD": pw})
        vaults = list(Path(home, ".sanitize-mcp", "vaults").glob("*.json"))
        enc = [v for v in vaults if v.read_bytes()[:4] == MAGIC]
        if not enc:
            failures.append("password mode: no encrypted vault file found")
        scrub = cli(["sanitize-text", PII], home, pw).stdout
        ph = json.loads(scrub)["sanitized"]
        if "[FEIN" not in ph or "04-1234567" in ph:
            failures.append(f"password mode scrub failed: {ph!r}")
        rst = cli(["restore", "-"], home, pw, stdin_text=ph)
        if PII not in rst.stdout:
            failures.append(f"password mode restore failed: got {rst.stdout!r}")

        # 2. wrong password: restore must fail loudly; encrypted file untouched
        before = {v: v.read_bytes() for v in enc}
        rst2 = cli(["restore", "-"], home, "WRONG", stdin_text=ph)
        if "04-1234567" in rst2.stdout:
            failures.append("wrong password: values restored anyway")
        for v, data in before.items():
            if v.read_bytes() != data:
                failures.append("encrypted vault modified under wrong password")

    # 3. legacy migration: plaintext vault (created without password) re-encrypts
    #    on the next save once SANITIZE_VAULT_PASSWORD is set, and stays restorable
    with tempfile.TemporaryDirectory(prefix="cc-vcrypt-") as home:
        await run_tool(PII, home)  # creates plaintext vault
        vaults = list(Path(home, ".sanitize-mcp", "vaults").glob("*.json"))
        if vaults and vaults[0].read_bytes()[:4] == MAGIC:
            failures.append("pre-migration: vault unexpectedly encrypted without password")
        await run_tool(PII + " and more", home, {"SANITIZE_VAULT_PASSWORD": pw})
        enc = [v for v in vaults if v.read_bytes()[:4] == MAGIC]
        if not enc:
            failures.append("legacy migration: plaintext vault did not re-encrypt")
        rst = cli(["restore", "-"], home, pw, stdin_text="[FEIN_1] [SSN_1]")
        if "04-1234567" not in rst.stdout or "123-45-6789" not in rst.stdout:
            failures.append(f"legacy migration restore failed: {rst.stdout!r}")

    # 4. Linux default (no password env): vault stays plaintext + user-only perms
    with tempfile.TemporaryDirectory(prefix="cc-vcrypt-") as home:
        await run_tool(PII, home)
        vaults = list(Path(home, ".sanitize-mcp", "vaults").glob("*.json"))
        if not vaults:
            failures.append("default mode: no vault file created")
        elif vaults[0].read_bytes()[:4] == MAGIC:
            failures.append("default mode: vault unexpectedly encrypted without password")
        elif (vaults[0].stat().st_mode & 0o777) & 0o077:
            failures.append(f"default mode: plaintext vault perms too open: {oct(vaults[0].stat().st_mode & 0o777)}")

    if failures:
        print("FAILURES:")
        for f in failures:
            print(" -", f)
        raise SystemExit(1)
    print("vault crypto: ALL PASSED — password round-trip, wrong-password safety, legacy migration, Linux plaintext default")


asyncio.run(main())
