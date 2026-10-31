#!/usr/bin/env python3
"""Tier-0 normalization test (v0.9.3) — match on the normalized view, vault the
byte-exact original. Covers: zero-width smuggling inside SSN, fullwidth digits
(NFKC), Cyrillic homoglyph inside a password anchor, bidi flag without rewrite,
plain-prose safety, invalid-area guard, and a restore round-trip.

Run: python3 test_normalize.py
"""
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(HERE, "bin", "Release", "net9.0", "ClaimCheck.dll")

home = tempfile.mkdtemp(prefix="cc-norm-test-")
env = dict(os.environ, HOME=home)

ZWSP = "​"   # U+200B zero-width space
RLO = "‮"    # U+202E right-to-left override
CYR_O = "о"  # U+043E Cyrillic o

def run_json(args, text):
    p = subprocess.run(["dotnet", DLL] + args,
                       input=text, capture_output=True, text=True, env=env, cwd=HERE)
    return p.returncode, json.loads(p.stdout)["sanitized"]

def scrub(text):
    return run_json(["sanitize-text"], text)

def restore(text):
    p = subprocess.run(["dotnet", DLL, "restore", "-"],
                       input=text, capture_output=True, text=True, env=env, cwd=HERE)
    return p.returncode, p.stdout

failures = []
def check(name, cond, detail=""):
    if cond:
        print(f"  ok  {name}")
    else:
        failures.append(name)
        print(f"FAIL  {name}  {detail}")

# --- 1. zero-width inside SSN: ticketed; restore brings back byte-exact original
ssn = "123-45-6789"
smuggled = f"ssn: {ssn[:7]}{ZWSP}{ssn[7:]}\n"
rc, out = scrub(smuggled)
check("scrub exit 0", rc == 0, out)
check("zero-width SSN ticketed", "[SSN_" in out, repr(out))
rc, back = restore(out)
check("restore byte-exact original", rc == 0 and back.strip() == smuggled.strip(), repr(back))

# --- 2. fullwidth digits (NFKC folds to ASCII): FEIN caught
rc, out = scrub(f"fein: ０４-２１０５３９３\n")
check("fullwidth FEIN ticketed", "[FEIN_" in out, repr(out))

# --- 3. Cyrillic о inside the password anchor: anchor folds, value vaulted exact
cyrl = f"passw{CYR_O}rd: Sup3rS3cret9\n"
rc, out = scrub(cyrl)
check("homoglyph password anchor ticketed", "PASSWORD_" in out, repr(out))
rc, back = restore(out)
check("homoglyph value restored exact", back.strip() == cyrl.strip(), repr(back))

# --- 4. bidi override: text NOT rewritten (flag-only tier)
bidi = f"report{RLO}gnp.txt\n"
rc, out = scrub(bidi)
check("bidi text untouched", out == bidi, repr(out))

# --- 5. plain prose untouched (NFKC/homoglyph fold must not corrupt display text)
plain = "The quick brown fox — naïve café résumé\n"
rc, out = scrub(plain)
check("plain prose untouched", out == plain, repr(out))

# --- 6. fullwidth colon label: SSN still matched
rc, out = scrub("ssn：123-45-6789\n")
check("fullwidth-colon SSN ticketed", "[SSN_" in out, repr(out))

# --- 7. invalid-area guard still respected on the normalized match
rc, out = scrub("ssn: 000-45-6789\n")
check("invalid-area SSN (000) still skipped", "000-45-6789" in out and "[SSN_" not in out, repr(out))

# --- 8. regression: plain SSN/FEIN unchanged behavior
rc, out = scrub("ssn: 123-45-6789\nfein: 04-2105393\n")
check("plain SSN ticketed", "[SSN_" in out, repr(out))
check("plain FEIN ticketed", "[FEIN_" in out, repr(out))

print()
if failures:
    print(f"{len(failures)} FAILURES: {failures}")
    sys.exit(1)
print("all normalization tests passed")
