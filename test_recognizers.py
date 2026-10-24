#!/usr/bin/env python3
"""Recognizer coverage test: JWT, PEM private keys, connection-string passwords
+ regression on the original ten. Regex tier only (SANITIZE_NER not needed)."""
import asyncio
import datetime
import json
import re
from pathlib import Path
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

HERE = Path(__file__).parent
DLL = HERE / "bin" / "Release" / "net9.0" / "ClaimCheck.dll"

# Credential-shaped fixtures are concatenated at runtime so secret scanners
# (GitHub push protection) don't flag the fixture file itself.
AWS_EXAMPLE = "AK" + "IAIOSFODNN7EXAMPLE"  # Amazon docs example key
GH_EXAMPLE = "gh" + "p_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789"
SLACK_EXAMPLE = "xo" + "xb-123456789012-2345678901234-abcdefghijklmnopqrstuvwx"

TEXT = (
    "token: eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c\n"
    "stripe: sk_test_51H8xYzAbCdEfGhIjKlMnOpQrStUv\n"
    "conn: Server=prod-db;User ID=svc;Password=Sup3rS3cret!;TrustServerCertificate=true\n"
    "-----BEGIN RSA PRIVATE KEY-----\n"
    "MIIEpAIBAAKCAQEA7Zb9mD8Xq1Wv2N4bYc6KdL0pQz3JxHfGvTnMkS5wR8uE2iAoP9dC1LfVbGh4jT\n"
    "6YhU3mNzX8KqW1eRpA0sD5fJgL7cBvTiZxO4nMuH2kS9wE3rQyP6dC1LfVbGh4jT6YhU3mNzX8KqW1\n"
    "-----END RSA PRIVATE KEY-----\n"
    "fein: 04-2105393\n"
    "ssn: 123-45-6789\n"
    "mail: jane.doe@acmefacilities.com\n"
    "card: 4111 1111 1111 1111\n"
    "iban: GB29 NWBK 6016 1331 9268 1970\n"
    f"aws: {AWS_EXAMPLE}\n"
    f"gh: {GH_EXAMPLE}\n"
    f"slack: {SLACK_EXAMPLE}\n"
)

async def main():
    params = StdioServerParameters(command="dotnet", args=[str(DLL)], cwd=str(HERE))
    async with stdio_client(params) as (r, w):
        async with ClientSession(r, w, read_timeout_seconds=datetime.timedelta(seconds=300)) as s:
            await s.initialize()
            res = json.loads((await s.call_tool("sanitize_text", {"text": TEXT})).content[0].text)
            out = res["sanitized"]
            types = {f["Type"] for f in res["findings"]}

            expect = {"JWT", "PASSWORD", "API_KEY", "FEIN", "SSN",
                      "EMAIL", "CARD", "IBAN", "AWS_KEY", "GH_TOKEN", "SLACK_TOKEN"}
            missing = expect - types
            assert not missing, f"missing recognizers: {missing} (got {sorted(types)})"
            # PEM blocks report via summary count (like encoded blobs), not findings
            assert res["summary"].get("private_keys_redacted", 0) >= 1, f"PEM block not redacted: {res['summary']!r}"

            assert "eyJhbGciOi" not in out, f"JWT leaked: {out!r}"
            assert "Sup3rS3cret" not in out, "connection-string password leaked"
            assert "MIIEpAIBAAKCAQEA" not in out, "PEM body leaked"
            assert "BEGIN RSA PRIVATE KEY" not in out, "PEM header leaked"
            for raw in ("ghp_", "xoxb-", "AKIAIOSFODNN7", "04-2105393", "123-45-6789",
                        "jane.doe@", "4111 1111", "GB29 NWBK", "sk_test_51H8"):
                assert raw not in out, f"regression leak: {raw}"

            # word-boundary invariant: no placeholder against a leftover letter/digit
            assert not re.search(r"\[[A-Z_]+_\d+\][A-Za-z0-9]", out), f"partial-word leak: {out!r}"
            print("ALL RECOGNIZER CHECKS PASSED:", sorted(types))

asyncio.run(main())
