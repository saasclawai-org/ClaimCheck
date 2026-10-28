# Worked example — scan → ticket → restore

A complete ClaimCheck round-trip in four commands. **Every value in
`secrets-test.txt` is fake**: the AWS key is Amazon's official documentation
example (`AKIAIOSFODNN7EXAMPLE`), the FEIN is a textbook dummy, the password
is invented, the email is `example.com`. Run it against your own install and
watch the whole loop work — without a real secret in sight.

| File | What it is |
|---|---|
| `secrets-test.txt` | sample "config" file with four fake secrets |
| `expected-sanitize.json` | verified `sanitize-file --content` output for that file |
| `run-example.ps1` | one-command round-trip with a PASS/FAIL check (Windows) |

## 1. The input — `secrets-test.txt`

```
DB_PASSWORD = Summer2026!Sunset
AWS_KEY = AKIAIOSFODNN7EXAMPLE
FEIN = 12-3456789
contact = jane.doe@acme.com
```

## 2. Scan it

```powershell
ClaimCheck sanitize-file secrets-test.txt --content
```

Verified output (path will differ on your machine):

```json
{
  "summary": { "total": 4,
    "by_type": { "PASSWORD": 1, "AWS_KEY": 1, "FEIN": 1, "EMAIL": 1 } },
  "findings": [
    { "Line": 1, "Type": "PASSWORD", "Placeholder": "PASSWORD_1" },
    { "Line": 2, "Type": "AWS_KEY",  "Placeholder": "AWS_KEY_1" },
    { "Line": 3, "Type": "FEIN",     "Placeholder": "FEIN_1" },
    { "Line": 4, "Type": "EMAIL",    "Placeholder": "EMAIL_1" }
  ],
  "content": "DB_PASSWORD = [PASSWORD_1]\nAWS_KEY = [AWS_KEY_1]\nFEIN = [FEIN_1]\ncontact = [EMAIL_1]\r\n"
}
```

What each field proves:

- **`summary`** — counts per recognizer; four different detection tiers hit
  (label-anchored `KEY=VALUE` pair, AWS token regex, FEIN, email).
- **`findings`** — line number, type, placeholder name *only*. Raw values
  never appear in the findings, so this JSON is safe to log anywhere.
- **`content`** — the shareable text: every secret swapped for a stable
  ticket. Same input always yields the same ticket (`FEIN_1` stays `FEIN_1`),
  which is what keeps cross-references intact for the model.

## 3. Restore — the mirror

```powershell
# one-liner: feed ticketed text on stdin
"FEIN = [FEIN_1]" | ClaimCheck restore -
```

Or run the full script (scan → save ticketed copy → restore → compare):

```powershell
powershell -File examples\run-example.ps1
```

Expected ending: `ROUND-TRIP PASS — restored output identical to original`.
The ticketed file on disk contains **none** of the original values; restore
pulls them purely from the local vault.

## Gotcha: restore is not a sanitizer

Restore expects *ticketed* text. Point it at the original file and there's
nothing to reverse — it echoes the raw content straight back, a no-op. This
trips up everyone exactly once.

## Where the values actually live

In the local vault (`~/.sanitize-mcp/vaults`), DPAPI-encrypted on Windows.
Send the ticketed text to anyone, paste it into any LLM chat — without this
machine and its vault, `[FEIN_1]` restores to nothing.
