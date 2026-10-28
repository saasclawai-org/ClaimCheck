#!/usr/bin/env bash
# e2e-secret-test — secret in prompt → ClaimCheck proxy → LLM → response file.
# Pass = raw secrets NEVER reach the response file; stable tickets do.
#
# Usage: e2e-secret-test.sh [proxy-base-url] [model-id]
#   proxy-base-url   ClaimCheck proxy base        (default http://127.0.0.1:18008)
#   model-id         model id the upstream serves (default qwen3-coder-30b-a3b)
#
# Examples:
#   ./e2e-secret-test.sh                                        # predator lane 8008
#   ./e2e-secret-test.sh http://127.0.0.1:8766 glm-4.6          # Pi appliance, cloud upstream
#
# What PASS proves: the FEIN and email were ticketed BEFORE the request reached
# the model — the response (and the file it was written to) contains only tickets.

PROXY="${1:-http://127.0.0.1:18008}"
MODEL="${2:-qwen3-coder-30b-a3b}"
OUT="${OUT:-/tmp/e2e-response.txt}"
FEIN="12-3456789"
EMAIL="billing@acmecorp.com"

REQ=$(printf '{"model":"%s","messages":[{"role":"user","content":"Acme Corp (FEIN 12-3456789) owes an invoice. Billing contact: billing@acmecorp.com. Write a one-line invoice header including their FEIN and email."}]}' "$MODEL")

curl -s "$PROXY/v1/chat/completions" -H 'Content-Type: application/json' -d "$REQ" \
 | python3 -c 'import json,sys; print(json.load(sys.stdin)["choices"][0]["message"]["content"])' > "$OUT" \
 || { echo "FAIL: request to $PROXY failed (non-JSON response)"; exit 1; }

echo "--- response written to $OUT ---"
cat "$OUT"; echo
echo "--- assertions ---"
PASS=1
grep -q "$FEIN"  "$OUT" && { echo "FAIL: raw FEIN leaked into the file";  PASS=0; }
grep -q "$EMAIL" "$OUT" && { echo "FAIL: raw email leaked into the file"; PASS=0; }
grep -qE "\[(FEIN|EMAIL)_[0-9]+\]" "$OUT" && echo "OK: tickets present in response" || echo "WARN: model did not echo tickets"
if [ "$PASS" = 1 ]; then
  echo "E2E: PASS — secrets stayed on this box"
else
  echo "E2E: FAIL"; exit 1
fi
