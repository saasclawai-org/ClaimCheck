#!/usr/bin/env python3
"""v0.9.16 run-based ORG recognizer regression (spec locked with Norman, 2026-11-06).
CLI sanitize-text on isolated vaults; asserts exact scrubbed output per case.
Covers the 8005 field failures (Lopez/Smithfield fragments), the hammering set
(Mitsui, Sumitomo, Barnes, Acme+Beta, ACME-caps, Home Depot, Johnson&Johnson),
and the caption trap (et al. / v. / Commonwealth)."""
import json, os, subprocess, sys, tempfile

REPO = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(REPO, "bin", "Release", "net9.0", "ClaimCheck.dll")

CASES = [
    ("Contract with Lopez Logistics Inc over unpaid freight.",
     "Contract with [ORG_ENTITY_1] over unpaid freight."),
    ("Employer: Smithfield Staffing LLC filed late.",
     "Employer: [ORG_ENTITY_1] filed late."),
    ("Revenue Has Sued Lopez Logistics Inc over unpaid freight.",
     "Revenue Has Sued [ORG_ENTITY_1] over unpaid freight."),
    ("Contract signed with Mitsui Engineering & Shipbuilding Co Ltd today.",
     "Contract signed with [ORG_ENTITY_1] today."),
    ("A second with Sumitomo Heavy Industries, Ltd also due.",
     "A second with [ORG_ENTITY_1] also due."),
    ("Barnes & Noble Education Inc filed Q2 results.",
     "[ORG_ENTITY_1] filed Q2 results."),
    ("Acme LLC and Beta Corp merged today.",
     "[ORG_ENTITY_2] and [ORG_ENTITY_1] merged today."),
    ("ACME INDUSTRIAL SERVICES LLC owes money.",
     "[ORG_ENTITY_1] owes money."),
    ("The Home Depot Inc sent a notice.",
     "[ORG_ENTITY_1] sent a notice."),
    ("Johnson and Johnson Inc settled the case.",
     "[ORG_ENTITY_1] settled the case."),
    ("Caption: Lopez Logistics Inc, et al. v. Commonwealth of Pennsylvania.",
     "Caption: [ORG_ENTITY_1], et al. v. Commonwealth of Pennsylvania."),
    # v0.9.21: bare-digit tokens join runs mid-name, never start one
    ("Contract with ABC 9 LLC over unpaid freight.",
     "Contract with [ORG_ENTITY_1] over unpaid freight."),
    ("Level 3 Communications, LLC filed late.",
     "[ORG_ENTITY_1] filed late."),
    ("Route 66 LLC owes money.",
     "[ORG_ENTITY_1] owes money."),
    ("I have 5 LLC filings due today.",
     "I have 5 LLC filings due today."),          # bare amount: digit can't start a run
    ("99 Cents Only Stores Corp opened a store.",
     "99 [ORG_ENTITY_1] opened a store."),        # digit-led name: partial (99 stays)
]


def main():
    if not os.path.exists(DLL):
        print("building Release...")
        subprocess.run(["dotnet", "build", "-c", "Release"], cwd=REPO, check=True, capture_output=True)
    failures = []
    for idx, (src, expected) in enumerate(CASES, 1):
        home = tempfile.mkdtemp(prefix="cc-orgruns-")
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
        print("test_org_runs: FAIL")
        for f in failures:
            print("  -", f)
        sys.exit(1)
    print(f"test_org_runs: ALL {len(CASES)} PASSED")


if __name__ == "__main__":
    main()
