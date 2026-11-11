#!/usr/bin/env python3
"""v0.9.20 regression: camelCase/code false positives in the PASSWORD tier.

Field report 2026-11-11 (payroll-scraper vault dump): bare `key:` in JS/JSON
field maps ticketed every field name (id, endDate, jobCode, cc1, ...), and the
unquoted branch captured code RHS after password-named variables
(`pwd = Regex.Match(loginHtml, ...)` -> "Regex.Match(loginHtml"). These shapes
must stay clean while real secrets still ticket. Regex tier only; runs anywhere.
"""
import json
import os
import subprocess
import sys
import tempfile

BIN = os.environ.get("CC_BIN", "dotnet bin/Release/net9.0/ClaimCheck.dll").split()
# Fresh HOME per run → fresh vault → immune to known-value re-ticketing from
# earlier scans (a regression that mints a negative value must not poison
# later runs via the re-ticket tier).
_TEST_HOME = tempfile.mkdtemp(prefix="cc-fp-test-")


def sanitize(text):
    env = {**os.environ, "HOME": _TEST_HOME}
    p = subprocess.run(BIN + ["sanitize-text"], input=text,
                       capture_output=True, text=True, env=env)
    if p.returncode != 0:
        print(f"FAIL: sanitize-text exited {p.returncode}: {p.stderr[:200]}")
        sys.exit(1)
    return json.loads(p.stdout)


def pw_count(d):
    return d["summary"]["by_type"].get("PASSWORD", 0)


fails = []


def ck(name, cond):
    print(("PASS " if cond else "FAIL ") + name)
    if not cond:
        fails.append(name)


# --- negatives: must ticket ZERO passwords ----------------------------------
JS_FIELD_MAP = """const fields = [
  { key: "id", type: "text" },
  { key: "code", type: "text" },
  { key: "name", type: "text" },
  { key: "date", type: "text" },
  { key: "notes", type: "text" },
  { key: "ssn", type: "text" },
  { key: "endDate", type: "date" },
  { key: "jobCode", type: "text" },
  { key: "shiftCode", type: "text" },
  { key: "wcc", type: "text" },
  { key: "cc1", type: "text" },
  { key: "cc2", type: "text" },
  { key: "costCenter2", type: "text" },
  { key: "filingStatus", type: "text" },
  { key: "taxCode", type: "text" },
  { key: "exemptions", type: "text" },
  { key: "includeTaxes", type: "bool" },
  { key: "percentageOfGross", type: "num" },
  { key: "overrideTaxCalc", type: "bool" }
];
"""
NEGATIVES = {
    "js-field-map (bare key: flood)": JS_FIELD_MAP,
    "csharp regex-match RHS": 'pwd = Regex.Match(loginHtml, @"id=""user""").Value;\n',
    "csharp ctx.Items RHS": 'password = ctx.Items["AuthTicket"] as string;\n',
    "pure-alpha RHS": "token = cancellationToken;\nsecret = config;\n",
}
for name, text in NEGATIVES.items():
    d = sanitize(text)
    ck(f"negative | {name} | 0 PASSWORD (got {pw_count(d)})", pw_count(d) == 0)

# --- positives: must still ticket --------------------------------------------
# Synthetic values split so scanners never match this source file.
PW_DIGIT = "hunter" "2"                 # unquoted, digit guard
PW_HASH = "X7#mK" "2$vL9pQ"             # v0.9.16 regression: '#' inside value
API_KEY_Q = "sk-li" "ve-abc123def456ghij"
POSITIVES = {
    "unquoted with digit": f"password = {PW_DIGIT}\n",
    "field-report testpass": "password = testpass1\n",
    "v0.9.16 hash value": f"Password={PW_HASH}\n",
    "api_key quoted (compound anchor)": f'api_key = "{API_KEY_Q}"\n',
    "env prefix": "DB_PASSWORD=Sup3rS3cret!\n",
    "smtp pass .env": "SMTP_PASS=Vb4nC6xZ8qW2\n",
}
for name, text in POSITIVES.items():
    d = sanitize(text)
    ck(f"positive | {name} | >=1 PASSWORD (got {pw_count(d)})", pw_count(d) >= 1)

print()
if fails:
    print(f"{len(fails)} check(s) FAILED")
    sys.exit(1)
print("all password-FP regression checks passed")
