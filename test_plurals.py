#!/usr/bin/env python3
"""v0.9.22 plural + possessive name regression (Norman 2026-11-12: "patch them
without presidio's NER because of the character limitation" — Presidio skips
>50K chars, NER chunks at 510 tokens; these shapes must live in the always-on
scanner tier at any document size).

Covers: possessive org suffixes ("ABC 9 LLC's", "Corp's"), plural surnames
("the Moores"), possessive plurals ("Moores'"), singular possessives
("Moore's"), forward pairs ("Norman Moore's"), and the FP guards (bare amounts,
"The Companies Act", "Today's", pangrams, "The Returns")."""
import json, os, subprocess, sys, tempfile

REPO = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(REPO, "bin", "Release", "net9.0", "ClaimCheck.dll")

CASES = [
    # possessive org suffixes (scanner tier, fresh vault)
    ("Contract with ABC 9 LLC's affiliate over freight.",
     "Contract with [ORG_ENTITY_1]'s affiliate over freight."),
    ("Level 3 Communications, LLC's contract expired.",
     "[ORG_ENTITY_1]'s contract expired."),
    ("Zentari Corp's invoice was paid.",
     "[ORG_ENTITY_1]'s invoice was paid."),
    # plural + possessive surnames (always-on PERSON pass)
    ("The Moores' paperwork arrived today.",
     "[PERSON_1]' paperwork arrived today."),
    ("Moores' paperwork came in.",
     "[PERSON_1]' paperwork came in."),
    ("The Moores arrived early.",
     "The [PERSON_1] arrived early."),
    # forward pair + singular possessive
    ("Norman Moore's office called.",
     "[PERSON_1]'s office called."),
    # reverse format regression (existing pass handles comma format + trailing 's)
    ("Moore, Norman's file is ready.",
     "[PERSON_1]'s file is ready."),
    # negative controls — must stay untouched
    ("I have 5 LLC filings due today.",
     "I have 5 LLC filings due today."),
    ("The Companies Act 2006 changed.",
     "The Companies Act 2006 changed."),
    ("Today's meeting ran long.",
     "Today's meeting ran long."),
    ("The quick brown fox jumps.",
     "The quick brown fox jumps."),
    ("The Returns were filed late.",
     "The Returns were filed late."),
]


def main():
    if not os.path.exists(DLL):
        print("building Release...")
        subprocess.run(["dotnet", "build", "-c", "Release"], cwd=REPO, check=True, capture_output=True)
    failures = []
    for idx, (src, expected) in enumerate(CASES, 1):
        home = tempfile.mkdtemp(prefix="cc-plurals-")
        env = dict(os.environ, HOME=home)
        r = subprocess.run(["dotnet", DLL, "sanitize-text", src],
                           capture_output=True, text=True, env=env, cwd=REPO, timeout=120)
        try:
            got = json.loads(r.stdout)["sanitized"]
        except Exception:
            failures.append(f"{idx} no output: {r.stdout[:200]!r} {r.stderr[:300]!r}")
            continue
        if got != expected:
            failures.append(f"{idx}\n   in:  {src}\n   got: {got}\n   exp: {expected}")
    if failures:
        print("test_plurals: FAIL")
        for f in failures:
            print("  -", f)
        sys.exit(1)
    print(f"test_plurals: ALL {len(CASES)} PASSED")


if __name__ == "__main__":
    main()
