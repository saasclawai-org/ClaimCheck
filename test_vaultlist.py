#!/usr/bin/env python3
"""vault-list regression: listing, --summary, --vault targeting, unreadable vaults."""
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(HERE, "bin", "Release", "net9.0", "ClaimCheck.dll")

home = tempfile.mkdtemp(prefix="cc-vaultlist-")
vdir = os.path.join(home, ".sanitize-mcp", "vaults")
os.makedirs(vdir, exist_ok=True)
json.dump({"map": {"SSN_1": "123-45-6789", "SSN_2": "456-78-9012"}, "reverse": {}, "counts": {"SSN": 2}},
          open(os.path.join(vdir, "proxy.json"), "w"))
json.dump({"map": {"FEIN_1": "84-3920175"}, "reverse": {}, "counts": {"FEIN": 1}},
          open(os.path.join(vdir, "aaa_other.json"), "w"))  # aaa_ sorts first
open(os.path.join(vdir, "locked.json"), "w").write("not-vault-json")  # unreadable-data path

env = dict(os.environ, HOME=home)

def run(args):
    p = subprocess.run(["dotnet", DLL] + args, capture_output=True, text=True, env=env)
    return p.stdout, p.stderr, p.returncode

fails = []
def check(name, got, want):
    if want not in got:
        fails.append(f"{name}: wanted {want!r} in {got!r}")

# 1. full listing: both vaults, entries + values visible
out, err, rc = run(["vault-list"])
check("listing header", out, "== " + os.path.join(vdir, "proxy.json"))
check("listing ssn entry", out, "[SSN_1] = 123-45-6789")
check("listing fein entry", out, "[FEIN_1] = 84-3920175")
check("listing two vaults", out, "aaa_other.json")

# 2. --summary: headers only, no values
out, err, rc = run(["vault-list", "--summary"])
check("summary header", out, "== ")
if "123-45-6789" in out:
    fails.append(f"summary leaked values: {out!r}")

# 3. --vault targets one file only
out, err, rc = run(["vault-list", "--vault", os.path.join(vdir, "aaa_other.json")])
check("target vault", out, "[FEIN_1] = 84-3920175")
if "SSN_1" in out:
    fails.append(f"--vault leaked other vault: {out!r}")

# 4. unreadable vault data reported without crashing
out, err, rc = run(["vault-list", "--vault", os.path.join(vdir, "locked.json")])
check("unreadable note", out, "unreadable")

# 5. no vaults at all -> hint, no crash
empty = tempfile.mkdtemp(prefix="cc-vaultlist-empty-")
p = subprocess.run(["dotnet", DLL, "vault-list"], capture_output=True, text=True,
                   env=dict(os.environ, HOME=empty))
check("no vaults hint", p.stdout + p.stderr, "no vaults found")

print(f"test_vaultlist: {5 - len(fails)}/{5} checks passed")
if fails:
    print("FAILED:", *fails, sep="\n  ")
    sys.exit(1)
print("test_vaultlist: ALL PASSED")
