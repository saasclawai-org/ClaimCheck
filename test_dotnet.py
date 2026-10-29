#!/usr/bin/env python3
"""Smoke test for the .NET sanitizer over stdio (dotnet ClaimCheck.dll).
Mirrors test_client.py asserts + one extra: placeholder parity with the Python vault
(same project root -> same vault file -> identical placeholders from either implementation)."""
import asyncio
import json
import os
import re
import subprocess
import tempfile
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

HERE = Path(__file__).parent
DOTNET_DIR = HERE.parent / "sanitize-mcp-dotnet"
DLL = DOTNET_DIR / "bin" / "Release" / "net9.0" / "ClaimCheck.dll"

PII = ["27-9415062", "545-12-3456", "jane.doe@acmecorp.test", "(555) 867-5309",
       "4111 1111 1111 1111", "AKIAIO…MPLE", "sk-liv…cdef"]
NOT_PII = ["server_port = 8080", "version = 1.2.3", "theme_color = #ff00ff",
           "hex_const = 0xDEADBEEF", "zip = 94107"]
PH_RE = re.compile(r"\[[A-Z_]+_\d+\]")

async def main() -> None:
    # Fresh vault home: the parity assertion expects fixture values to mint as
    # _1 tickets, which only holds on an empty vault (other tests in the suite
    # mint FEIN/EMAIL/CARD/... into the default vault when they run first).
    vault_home = tempfile.mkdtemp(prefix="cc-dotnet-parity-")
    env = dict(os.environ, SANITIZE_NER="0", HOME=vault_home)
    params = StdioServerParameters(command="dotnet", args=[str(DLL)], cwd=str(HERE),
                                env=env)
    async with stdio_client(params) as (r, w):
        async with ClientSession(r, w) as s:
            await s.initialize()
            tools = sorted(t.name for t in (await s.list_tools()).tools)
            print("TOOLS:", tools)
            assert set(tools) == {"read_sanitized", "scan_file", "sanitize_text"}, tools

            fx = str(HERE / "fixture.txt")
            t1 = (await s.call_tool("read_sanitized", {"path": fx})).content[0].text
            d1 = json.loads(t1)
            c1 = d1["content"]
            leaks = [v for v in PII if v in t1]
            assert not leaks, f"LEAKED: {leaks}"
            ph = sorted(set(PH_RE.findall(c1)))
            expected = sorted(["[FEIN_1]", "[SSN_1]", "[EMAIL_1]", "[PHONE_1]",
                               "[CARD_1]", "[AWS_KEY_1]", "[API_KEY_1]"])
            assert ph == expected, f"placeholder parity FAIL: {ph} vs {expected}"
            keep = [ln for ln in c1.splitlines() if ln in NOT_PII]
            assert len(keep) == len(NOT_PII), f"false positives! only kept {keep}"
            print("read_sanitized OK — placeholders identical to Python vault:", ", ".join(ph))

            t3 = json.loads((await s.call_tool("scan_file", {"path": fx})).content[0].text)
            assert "content" not in t3 and t3["summary"]["total"] >= 7
            print("scan_file OK:", t3["summary"])

            t4 = json.loads((await s.call_tool("sanitize_text",
                            {"text": "Contact bob@corp.test or 123-45-6789, FEIN 98-7654321"})).content[0].text)
            assert not any(x in t4["sanitized"] for x in ["bob@corp.test", "123-45-6789", "98-7654321"]), t4
            print("sanitize_text OK:", t4["sanitized"])

    # restore CLI (dotnet binary, shared vault)
    scrub = DOTNET_DIR / "_restore_check.txt"
    scrub.write_text(t4["sanitized"])
    out = subprocess.run(["dotnet", str(DLL), "restore", str(scrub)],
                         capture_output=True, text=True, env=env).stdout
    assert "bob@corp.test" in out, f"restore failed: {out!r}"
    scrub.unlink()
    print("restore CLI OK")
    print("\nALL DOTNET CHECKS PASSED")

asyncio.run(main())
