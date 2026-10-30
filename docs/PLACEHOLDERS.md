# Placeholders & vault tickets

ClaimCheck replaces sensitive values with **opaque, stable tickets** — `[FEIN_1]`,
`[SSN_2]`, `[PERSON_7]` — and keeps the mapping in a local vault. This page documents
exactly what those tickets mean, what's guaranteed, and what isn't.

## The contract

**A ticket is scoped to the vault that minted it.** `[FEIN_1]` means "the first FEIN
minted in *this* vault" — nothing more global than that.

Within one vault, the contract is absolute:

- **Same value → same ticket, forever.** Mint order is recorded, the mapping is
  persisted to disk, saves are atomic, and minting is lock-protected. `[FEIN_1]`
  minted yesterday is `[FEIN_1]` today and next year.
- **Referential integrity for the model.** Every occurrence of the same client's FEIN
  becomes the same token, so the model can reason consistently across an entire
  session — "compare [FEIN_1] to [FEIN_2]" works.
- **Restore is exact.** `[FEIN_1]` maps back to the original bytes as matched.

Across vaults, lanes, or machines, **numbers are not comparable**. `[FEIN_1]` in one
lane's vault and `[FEIN_1]` in another are different mapping tables. The number is an
opaque ticket ID, not an identifier.

## Why opaque numbering (and not a hash of the value)

The obvious alternative — `SHA256(value)` as the placeholder — would leak. FEINs,
SSNs, and account numbers are **low-entropy identifiers**: the search space is small
enough to brute-force. Anyone holding a hash-based placeholder could confirm a guess
("is this ticket the Acme client?") or match tickets across documents to reconstruct
relationships.

Opaque per-vault numbering carries **zero information about the value**: it can't be
matched, confirmed, cross-referenced, or reversed by anyone who never had the
plaintext. It also makes tickets meaningless outside their vault — which is the
feature.

## Practical rules

1. **Restore with the vault that scrubbed the content.** The mapping only exists in
   that file. The proxy restores with its own lane vault automatically; the CLI needs
   the same vault passed with `--vault` (v0.9.1: `ClaimCheck restore - --vault <path>`
   pins exactly one vault; the default multi-vault sweep restores in sorted order and
   warns when a ticket being restored exists in several vaults with different values).
   Inspect first: `ClaimCheck vault-list [--vault <path>] [--summary]` prints ticket→value
   entries (or counts only); password-protected vaults show as locked unless the command
   runs with the same `SANITIZE_VAULT_PASSWORD`.
2. **Want cross-lane consistency?** Point lanes at the same `--vault` file — same
   value → same ticket in both. Trade-off: shared blast radius. Reasonable for two
   personal lanes; keep fleets separate.
3. **Never treat the number as meaningful.** It's a ticket, not an ID. There is no
   ordering, no collision-domain, no meaning outside the vault.
4. **Deleting the vault is destructive.** Tickets without their vault are unrestorable
   by design.

## Why this matters for model correctness

A model never cross-references placeholders across sessions — every session is a
fresh context. What matters for work quality is **per-session integrity**: the same
client's FEIN appearing six times in one conversation stays `[FEIN_1]` all six times.
That is the guaranteed part, and it's what lets the model produce correct,
consistent work over redacted data.

## Security note

The vault *is* the sensitivity — it's the only place tickets resolve. It's encrypted
at rest (DPAPI on Windows, AES-GCM everywhere via `SANITIZE_VAULT_PASSWORD`), and the
password matters most on Linux, where there is no OS keychain backing it.
