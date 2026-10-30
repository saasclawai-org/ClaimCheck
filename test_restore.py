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

    # ---- v0.9.1: --vault pinning + sweep collision warning (audit finding #3)
    # second vault sorts AFTER proxy.json (zz_…) so the sweep's first-vault-wins is deterministic
    other = {
        "map": {"FEIN_1": "98-7654321", "SSN_1": "123-45-6789"},
        "reverse": {"FEIN_1|98-7654321": "[FEIN_1]", "SSN_1|123-45-6789": "[SSN_1]"},
        "counts": {"FEIN": 1, "SSN": 1},
    }
    json.dump(other, open(os.path.join(vdir, "zz_other.json"), "w"))

    def restore_argv(argv, text):
        p = subprocess.run(["dotnet", DLL, "restore"] + argv, input=text,
                           capture_output=True, text=True, env=env)
        return p.stdout.rstrip("\n"), p.stderr, p.returncode

    vmain = os.path.join(vdir, "proxy.json")
    vother = os.path.join(vdir, "zz_other.json")

    out_main, _, _ = restore_argv(["-", "--vault", vmain], "[FEIN_1]")
    if out_main != "84-3920175":
        fails.append(f"--vault main: [FEIN_1] -> {out_main!r}, want 84-3920175")
    out_o, _, _ = restore_argv(["-", "--vault", vother], "[FEIN_1]")
    if out_o != "98-7654321":
        fails.append(f"--vault other: [FEIN_1] -> {out_o!r}, want 98-7654321")

    # sweep with a real collision: deterministic first-vault-wins + loud warning
    out_sweep, err_sweep, _ = restore_argv(["-"], "[FEIN_1]")
    if out_sweep != "84-3920175":
        fails.append(f"sweep collision: [FEIN_1] -> {out_sweep!r}, want proxy.json's 84-3920175 (sorted first)")
    if "multiple vaults" not in err_sweep or "--vault" not in err_sweep:
        fails.append(f"sweep collision: no warning on stderr: {err_sweep[-300:]!r}")

    # same-value duplicates must NOT warn
    out_same, err_same, _ = restore_argv(["-"], "[SSN_1]")
    if out_same != "123-45-6789":
        fails.append(f"sweep same-value: [SSN_1] -> {out_same!r}")
    if "multiple vaults" in err_same:
        fails.append("sweep same-value: false collision warning")

    # pinned vault that doesn't exist -> fatal
    _, _, rc_missing = restore_argv(["-", "--vault", os.path.join(vdir, "nope.json")], "x")
    if rc_missing == 0:
        fails.append("--vault nonexistent: should exit non-zero")

    print(f"test_restore: {len(CASES) - len(fails)}/{len(CASES)} variation groups passed")
    if fails:
        print("FAILED:", *fails, sep="\n  ")
        sys.exit(1)
    print("test_restore: ALL PASSED")

main()
