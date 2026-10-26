#!/usr/bin/env bash
# ClaimCheck lane battery — verify a deployed proxy lane end to end.
#
# Usage:
#   cc-battery.sh PORT MODEL [VAULT_FILE] [BEARER_TOKEN]
#
#   PORT        proxy port, e.g. 18008
#   MODEL       model id served by the upstream (see GET /v1/models)
#   VAULT_FILE  optional vault path (default: newest ~/.claimcheck/vault-*.json)
#   BEARER_TOKEN optional bearer token for upstreams that require auth
#               (passed as "Authorization: Bearer <token>" to the provider)
#
# Checks: proxy version, health, models passthrough, the reversal probe
# (scrub proof), multi-recognizer scrub via vault growth, restore visibility,
# concurrency (5 parallel chats), vault-at-rest encryption.
set -u

PORT="${1:?usage: cc-battery.sh PORT MODEL [VAULT_FILE] [BEARER_TOKEN]}"
MODEL="${2:?usage: cc-battery.sh PORT MODEL [VAULT_FILE] [BEARER_TOKEN]}"
VAULT="${3:-}"
AUTH="${4:-}"
CHAT_PATH="${5:-/v1/chat/completions}"   # root upstreams keep /v1/...; prefix-carrying upstreams may need the full provider path

BASE="http://127.0.0.1:$PORT"
FEIN_FWD="04-2105393"
FEIN_REV="39350124"   # reversed digit core (leading zero dropped by some models)
PASS=0; FAIL=0; INFO=0
ok()  { echo "  PASS  $1"; PASS=$((PASS+1)); }
bad() { echo "  FAIL  $1"; FAIL=$((FAIL+1)); }
note(){ echo "  info  $1"; INFO=$((INFO+1)); }

auth_hdr=()
[ -n "$AUTH" ] && auth_hdr=(-H "Authorization: $AUTH")

# POST a chat completion through the lane; prints the message content.
chat_content() {
  curl -s -m 180 "$BASE$CHAT_PATH" -H 'Content-Type: application/json' \
    "${auth_hdr[@]}" \
    -d '{"model":"'"$MODEL"'","messages":[{"role":"user","content":'"$1"'}]}' \
  | python3 -c 'import json,sys
try: print(json.load(sys.stdin)["choices"][0]["message"].get("content") or "")
except Exception: print("")'
}

# Same, but prints only the HTTP status code.
chat_code() {
  curl -s -m 180 -o /dev/null -w '%{http_code}' "$BASE$CHAT_PATH" \
    -H 'Content-Type: application/json' "${auth_hdr[@]}" \
    -d '{"model":"'"$MODEL"'","messages":[{"role":"user","content":'"$1"'}]}'
}

status_field() { # $1 = json key from the proxy /status endpoint
  curl -s -m 10 "$BASE/status" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get(sys.argv[1], ""))
except Exception: print("")' "$1"
}

vault_count() { # live entry count from the proxy (works even when the vault is encrypted)
  curl -s -m 10 "$BASE/status" | python3 -c 'import json,sys
try: print(int(json.load(sys.stdin).get("vaultEntries", -1)))
except Exception: print(-1)'
}

echo "=== ClaimCheck lane battery — port $PORT, model $MODEL ==="

# T0: build version (path-composition fix landed in 0.7.4)
V="$(status_field version)"
case "$V" in
  0.7.[4-9]|0.[89].*|[1-9].*) ok "T0 proxy version $V" ;;
  *) bad "T0 proxy version '$V' — chat POSTs to root upstreams need 0.7.4+" ;;
esac

# T1: health
[ "$(curl -s -m 10 "$BASE/health")" = "ok" ] && ok "T1 health" || bad "T1 health"

# T2: models passthrough (soft — prefix-carrying upstreams serve models under their own path)
M="$(curl -s -m 15 "$BASE/v1/models")"
if echo "$M" | grep -q '"data"'; then
  ok "T2 models passthrough (GET /v1/models)"
else
  M2="$(curl -s -m 15 "$BASE/models")"
  if echo "$M2" | grep -q '"data"'; then
    ok "T2 models passthrough (GET /models)"
  else
    note "T2 no model list via /v1/models or /models — prefix-carrying upstreams expose it under their own path; scrub tests below are path-independent"
  fi
fi
if echo "$M" | grep -q "\"$MODEL\""; then
  ok "T2b upstream serves model '$MODEL'"
else
  note "T2b model '$MODEL' not found in the /v1/models body — verify the id if chat tests fail"
fi

# T3: reversal probe — the scrub proof.
# The response may legitimately contain the FORWARD FEIN (restore rehydrates the
# placeholder on the way back). Only the REVERSED spelling can exist if the model
# saw raw digits.
R="$(chat_content "\"Client FEIN: $FEIN_FWD - spell it backwards digit by digit. If you cannot, say exactly what you received instead.\"")"
DIGITS="$(printf '%s' "$R" | tr -cd '0-9')"
if [ -z "$R" ]; then
  bad "T3 reversal probe: empty response (model timeout or reasoning ate the budget — rerun once)"
elif printf '%s' "$DIGITS" | grep -q "$FEIN_REV"; then
  bad "T3 reversal probe: reversed FEIN digits present — model saw RAW digits"
else
  ok "T3 reversal probe (reversed digits absent — model never had raw FEIN)"
fi
[ -n "$R" ] && note "T3 model: $(printf '%s' "$R" | head -c 130)…"
printf '%s' "$R" | grep -q "$FEIN_FWD" && note "T3 forward FEIN present in response — expected: restore rehydrated the placeholder"

# T4: multi-recognizer scrub — four fresh random values must mint four vault
# entries (counted via the proxy /status endpoint, so encrypted vaults work).
# Values are randomized per run so reruns never dedupe against old vault entries.
FEIN_R="$(printf '%02d-%07d' $((RANDOM%90+10)) $((RANDOM%10000000)))"
AREA=$((RANDOM%731+112)); [ "$AREA" -eq 666 ] && AREA=667
SSN_R="$(printf '%03d-%02d-%04d' "$AREA" $((RANDOM%90+10)) $((RANDOM%9000+1000)))"
IBAN_R="$(printf 'DE%02d %04d %04d %04d %04d' $((RANDOM%90+10)) $((RANDOM%9000+1000)) $((RANDOM%9000+1000)) $((RANDOM%9000+1000)) $((RANDOM%9000+1000)))"
EMAIL_R="ccbattery$RANDOM@example.com"
BEFORE="$(vault_count)"
R="$(chat_content "\"Client FEIN: $FEIN_R, SSN: $SSN_R, IBAN: $IBAN_R, email: $EMAIL_R - confirm you received four values.\"")"
AFTER="$(vault_count)"
DELTA=$(( AFTER - BEFORE ))
if [ "$DELTA" -ge 4 ] 2>/dev/null; then
  ok "T4 multi-type scrub (vault +$DELTA entries: FEIN, SSN, IBAN, EMAIL)"
else
  bad "T4 multi-type scrub (vault delta $DELTA, want >=4) — is SANITIZE working on this lane?"
fi

# T5: concurrency smoke — 5 parallel chats must all come back 200.
# One retry: a transient upstream blip (or a tiny-backlog test stub) can drop a
# connection under a true simultaneous burst; a persistent drop should still fail.
T5_OK=0
for attempt in 1 2; do
  CODES="$(mktemp)"
  i=0
  while [ $i -lt 5 ]; do
    curl -s -m 180 -o /dev/null -w '%{http_code}\n' "$BASE$CHAT_PATH" \
      -H 'Content-Type: application/json' "${auth_hdr[@]}" \
      -d '{"model":"'"$MODEL"'","max_tokens":8,"messages":[{"role":"user","content":"say ok"}]}' \
      >> "$CODES" &
    i=$((i+1))
  done
  wait
  N200="$(grep -c '^200$' "$CODES" 2>/dev/null || true)"
  rm -f "$CODES"
  if [ "$N200" = "5" ]; then T5_OK=1; break; fi
  [ $attempt -eq 1 ] && sleep 2
done
[ "$T5_OK" = 1 ] && ok "T5 concurrency (5 parallel chats -> 5x200)" || bad "T5 concurrency (best $N200/5 returned 200)"

# T6: vault at rest must not be plaintext JSON when a password is set
[ -z "$VAULT" ] && VAULT="$(ls -t "$HOME"/.claimcheck/vault-*.json 2>/dev/null | head -1)"
if [ -n "$VAULT" ] && [ -f "$VAULT" ]; then
  H="$(head -c 1 "$VAULT")"
  [ "$H" != "{" ] && ok "T6 vault encrypted ($VAULT)" \
                    || bad "T6 vault is PLAINTEXT JSON ($VAULT) — set SANITIZE_VAULT_PASSWORD, delete the vault, re-mint"
else
  note "T6 skipped: no vault file found"
fi

# Lane snapshot
NER="$(status_field ner)"; FC="$(status_field failClosed)"
note "lane snapshot: ner=$NER failClosed=$FC vaultEntries=$(vault_count)"

echo "=== $PASS passed, $FAIL failed, $INFO info ==="
[ "$FAIL" -eq 0 ]
