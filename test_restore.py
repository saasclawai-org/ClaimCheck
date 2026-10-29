#!/usr/bin/env python3
"""Restore regression test — bracketed + bare-form tickets (v0.9.0).

Runs ClaimCheck restore against a controlled temp vault and asserts the full
boundary matrix: bare-form restore (--bare), guards (MY_SSN_1, SSN_10, SSN_1x,
SSN_1_extra, SSN_1_2), bracketed pass, case sensitivity, unmapped tickets.

Run: python3 test_restore.py   (builds Release first if the DLL is missing)
"""
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(HERE, "bin", "Release", "net9.0", "ClaimCheck.dll")

home = tempfile.mkdtemp(prefix="cc-restore-test-")
vdir = os.path.join(home, ".sanitize-mcp", "vaults")
os.makedirs(vdir, exist_ok=True)
vault = {
    "map": {"SSN_1": "123-45-6789", "SSN_10": "987-65-4321", "FEIN_1": "84-3920175"},
    "reverse": {"SSN_1|123-45-6789": "[SSN_1]", "SSN_10|987-65-4321": "[SSN_10]",
                "FEIN_1|84-3920175": "[FEIN_1]"},
    "counts": {"SSN": 10, "FEIN": 1},
}
json.dump(vault, open(os.path.join(vdir, "proxy.json"), "w"))
env = dict(os.environ, HOME=home)

# (input, expected WITHOUT --bare, expected WITH --bare)
CASES = [
    ("SSN_1",                     "SSN_1",                  "123-45-6789"),
    ("[SSN_1]",                   "123-45-6789",            "123-45-6789"),
    ("SSN_10",                    "SSN_10",                 "987-65-4321"),
    ("MY_SSN_1",                  "MY_SSN_1",               "MY_SSN_1"),
    ("SSN_1_extra",               "SSN_1_extra",            "SSN_1_extra"),
    ("SSN_1_2",                   "SSN_1_2",                "SSN_1_2"),
    ("(SSN_1)",                   "(SSN_1)",                "(123-45-6789)"),
    ("xSSN_1",                    "xSSN_1",                 "xSSN_1"),
    ("SSN_1x",                    "SSN_1x",                 "SSN_1x"),
    ("ssn_1",                     "ssn_1",                  "ssn_1"),
    ("-SSN_1-",                   "-SSN_1-",                "-123-45-6789-"),
    ("[SSN_1] + SSN_1 + [SSN_10]", "123-45-6789 + SSN_1 + 987-65-4321",
                                   "123-45-6789 + 123-45-6789 + 987-65-4321"),
    ("FEIN_1",                    "FEIN_1",                 "84-3920175"),
    ("SSN_99",                    "SSN_99",                 "SSN_99"),
    ("a SSN_1 b SSN_1",           "a SSN_1 b SSN_1",        "a 123-45-6789 b 123-45-6789"),
]

def restore(text, bare):
    args = ["dotnet", DLL, "restore", "-"] + (["--bare"] if bare else [])
    p = subprocess.run(args, input=text, capture_output=True, text=True, env=env)
    return p.stdout.rstrip("\n"), p.stderr

def main():
    if not os.path.exists(DLL):
        print("building Release...")
        subprocess.run(["dotnet", "build", "-c", "Release", "-f", "net9.0"],
                       cwd=HERE, check=True, capture_output=True)

    fails, disclosure_seen = [], False
    for text, want_plain, want_bare in CASES:
        got_plain, err = restore(text, bare=False)
        got_bare, _ = restore(text, bare=True)
        if got_plain != want_plain:
            fails.append(f"{text!r}: plain -> {got_plain!r}, want {want_plain!r}")
        if got_bare != want_bare:
            fails.append(f"{text!r}: --bare -> {got_bare!r}, want {want_bare!r}")
        if "bare-form" in err:
            disclosure_seen = True

    if not disclosure_seen:
        fails.append("disclosure: no bare-form hint on plain restore (expected for bare-ticket inputs)")

    print(f"test_restore: {len(CASES) - len(fails)}/{len(CASES)} variation groups passed")
    if fails:
        print("FAILED:", *fails, sep="\n  ")
        sys.exit(1)
    print("test_restore: ALL PASSED")

main()
