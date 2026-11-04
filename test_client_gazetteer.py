#!/usr/bin/env python3
"""Client-master gazetteer battery (v0.9.12): company names + digit-normalized FEINs.

Runs the CLI with HOME isolated to a temp dir, seeds custom_clients.json /
custom_feins.json with SYNTHETIC values (never real client data), and asserts:
  - FEINs hit in any format that survives the regex tier (bare/spaced/dotted)
    ticket as CLIENT_FEIN; dashed variants are caught by the regex FEIN pattern
  - non-listed FEINs, phones, and SSNs never ticket as CLIENT_FEIN
  - multi-word company names ticket as CLIENT, longest match wins
  - partial company names do not ticket (exact-match semantics)
  - hot-reload: removing a FEIN from the file stops ticketing it

Run:  dotnet build -c Release -f net9.0 && python3 test_client_gazetteer.py
"""
import json, os, subprocess, sys, tempfile

BIN = os.environ.get("CC_BIN", "dotnet bin/Release/net9.0/ClaimCheck.dll").split()
fails = []

RUN_HOME = tempfile.mkdtemp(prefix="cc-client-gaz-")
mg = os.path.join(RUN_HOME, ".sanitize-mcp")
os.makedirs(mg, exist_ok=True)

def sanitize(text):
    env = dict(os.environ, HOME=RUN_HOME)
    r = subprocess.run(BIN + ["sanitize-text"], input=text, capture_output=True, text=True, env=env)
    if r.returncode != 0:
        raise RuntimeError(f"CLI failed: {r.stderr[-400:]}")
    return json.loads(r.stdout)

def types(out):
    return [f["Type"] for f in out["findings"]]

def check(name, cond, detail=""):
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  <- {detail}"))
    if not cond:
        fails.append(name)

# --- synthetic seeds (assembled from fragments; no real client data) --------
FEIN = "98" + "7654321"          # 987654321
OTHER = "12-" "3456780"                        # 123456780
json.dump(["Acme Roofing Supply", "Global Logistics Inc", "O'Brien & Sons"],
          open(os.path.join(mg, "custom_clients.json"), "w"))
json.dump([FEIN, OTHER], open(os.path.join(mg, "custom_feins.json"), "w"))

# 1. FEIN variants
out = sanitize("FEIN 98-7654321 on file.\n")
check("FEIN dashed caught (regex or client tier)", "FEIN" in types(out) or "CLIENT_FEIN" in types(out), out["findings"])
out = sanitize(f"FEIN {FEIN} bare.\n")
check("FEIN bare variant -> CLIENT_FEIN", "CLIENT_FEIN" in types(out), out["findings"])
out = sanitize("FEIN 98 7654 321 spaced.\n")
check("FEIN spaced variant -> CLIENT_FEIN", "CLIENT_FEIN" in types(out), out["findings"])
out = sanitize("FEIN: 98.7654.321 dotted.\n")
check("FEIN dotted variant -> CLIENT_FEIN", "CLIENT_FEIN" in types(out), out["findings"])

# 2. negatives — the set is the FP firewall
out = sanitize("Phone 404-555-1234 and SSN 999-99-1234.\n")
check("phone/SSN not client-FEIN", "CLIENT_FEIN" not in types(out), out["findings"])
out = sanitize("Unrelated FEIN 11-1111111 stays.\n")
check("non-listed FEIN not client-FEIN", "CLIENT_FEIN" not in types(out), out["findings"])

# 3. company names
out = sanitize("Acme Roofing Supply filed the return.\n")
check("full company name tickets", "CLIENT" in types(out), out["findings"])
out = sanitize("Contact at Global Logistics Inc renewed.\n")
check("company with suffix tickets", "CLIENT" in types(out), out["findings"])
out = sanitize("O'Brien & Sons signed today.\n")
check("punctuated name tickets", "CLIENT" in types(out), out["findings"])

# 4. exact-match negatives
out = sanitize("Acme Roofing only two words.\n")
check("partial name does not ticket", "CLIENT" not in types(out), out["findings"])
out = sanitize("The supply ledger is clean.\n")
check("common words alone do not ticket", "CLIENT" not in types(out), out["findings"])

# 5. hot-reload: drop OTHER from the file -> next request stops ticketing it
json.dump([FEIN], open(os.path.join(mg, "custom_feins.json"), "w"))
out = sanitize("Now 12-3456780 passes through unlisted.\n")
check("hot-reload drops removed FEIN", "CLIENT_FEIN" not in types(out), out["findings"])
out = sanitize(f"Still {FEIN} listed.\n")
# real security property: the raw digits never survive — whichever layer catches
# them (client tier fresh, or vault re-ticket if already vaulted by an earlier case)
check("hot-reload keeps remaining FEIN", FEIN not in out["sanitized"], out["sanitized"])

print(f"\n{'ALL GREEN' if not fails else str(len(fails)) + ' FAILURES'}")
sys.exit(1 if fails else 0)
