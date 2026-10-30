#!/usr/bin/env python3
"""Realistic mode (SANITIZE_FAKE_DATA=1): stable per-ticket fakes in reserved namespaces."""
import json
import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(HERE, "bin", "Release", "net9.0", "ClaimCheck.dll")
home = tempfile.mkdtemp(prefix="cc-fake-")
env = dict(os.environ, SANITIZE_FAKE_DATA="1", HOME=home)
env_off = {k: v for k, v in env.items() if k != "SANITIZE_FAKE_DATA"}

def sanitize(text, e=env):
    p = subprocess.run(["dotnet", DLL, "sanitize-text", text], capture_output=True, text=True, env=e)
    assert p.returncode == 0, p.stderr
    return json.loads(p.stdout)["sanitized"]

fails = []
def check(name, cond, detail=""):
    if not cond: fails.append(f"{name} {detail}")

# 1. SSN → reserved fake (9xx-00), stable by ticket
s1 = sanitize("SSN 456-78-9012 for the new hire")
m1 = re.search(r"9\d{2}-00-\d{4}", s1)
check("ssn fake shape (9xx-00)", bool(m1), s1[:140])
check("real value gone", "456-78-9012" not in s1)
check("ticket not shown", "[SSN" not in s1)
f1 = m1.group(0) if m1 else ""

# 2. stable by ticket (same value → same fake, across calls)
s2 = sanitize("SSN 456-78-9012 for the new hire")
m2 = re.search(r"9\d{2}-00-\d{4}", s2)
check("stable across calls", bool(m2) and m2.group(0) == f1, f"{f1} vs {m2 and m2.group(0)}")

# 3. distinct value → distinct fake
s3 = sanitize("SSN 789-01-2345 for another hire")
m3 = re.search(r"9\d{2}-00-\d{4}", s3)
check("distinct fakes", bool(m3) and m3.group(0) != f1, f"{f1} vs {m3 and m3.group(0)}")

# 4. FEIN/EMAIL/PHONE/CARD fakes
s4 = sanitize("FEIN 84-3920175, email jane@corp.com, phone (212) 555-0147, card 4111 1111 1111 1111")
check("fein fake (00-prefix)", "00-" in s4 and "84-3920175" not in s4, s4[:140])
check("email fake (.invalid)", "@claimcheck.invalid" in s4 and "jane@corp.com" not in s4)
check("phone fake (555-010)", "(555) 010-" in s4, s4[:140])
check("card fake luhn-shaped", re.search(r"4\d{3}-\d{4}-\d{4}-\d{4}", s4) is not None, s4[:160])

# 5. restore round-trip: fake → real
p = subprocess.run(["dotnet", DLL, "restore", "-"], input=s1, capture_output=True, text=True, env=env)
check("restore fake → real", "456-78-9012" in p.stdout, p.stdout[:140])
if f1: check("restore strips fake", f1 not in p.stdout)

# 6. mode off default → bracketed tickets
p = subprocess.run(["dotnet", DLL, "sanitize-text", "SSN 456-78-9012"], capture_output=True, text=True, env=env_off)
d6 = json.loads(p.stdout)["sanitized"]
check("mode off → bracketed ticket", "[SSN_1]" in d6, d6[:140])

# 7. fakes don't re-mint on re-scrub (recognizer skips reserved space)
s_fb = sanitize(s1)
check("fakes stable on re-scrub", bool(f1) and f1 in s_fb and "[SSN" not in s_fb, s_fb[:200])

print(f"test_fakedata: {7 - len(fails)}/7 checks passed")
if fails:
    print("FAILED:", *fails, sep="\n  ")
    sys.exit(1)
print("test_fakedata: ALL PASSED")
