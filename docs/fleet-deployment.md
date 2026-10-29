# Fleet deployment — multiple projects and users

How ClaimCheck's vault model scales across teams, projects, and users: what
spans a vault, what crosses lanes, and how to choose a deployment shape.

## The isolation rule

**One lane per tenant = one proxy instance = one vault.** A vault is a ticket
namespace: everything minted inside it maps only inside it, and placeholders
minted by one lane restore only through that lane. The systemd template unit
that implements one-instance-per-lane is in
[PROXY.md — Multi-lane fleet](PROXY.md#multi-lane-fleet-systemd-template).

## Ticket semantics inside one vault

- **Same string, same ticket — always.** Minting is an idempotent lookup, not
  a counter race: the vault is consulted before minting, writes are atomic +
  locked, and the vault survives restarts. `123-45-6789` → `[SSN_1]` the first
  time and every time.
- **Tickets are per exact string.** Formatting and case variants
  (`123456789`, `john smith`) are distinct strings and get distinct tickets.
- **Stable across restarts.** The vault is disk-backed and reloaded on start;
  ticket numbering continues where it left off.

## Choosing a shape

| | Shared lane (one vault) | Lane per tenant (recommended) |
|---|---|---|
| Same PII string, two users | Same ticket for both | Each lane's own ticket |
| Restore rights | Anyone who can query the lane restores any ticket on it | Restores stay within the tenant |
| Counter metadata | Shared — `[SSN_7]` implies ≥7 SSNs lane-wide | Scoped to the tenant |
| Deployment cost | One instance | One instance per tenant (a template unit + a 2-line env file) |

A shared lane is fine for a trusted single team that wants cross-member
consistency. For multi-project or multi-tenant separation, run one lane per
tenant and hand out lane URLs per audience.

## Restores and rights

Restoration happens at the proxy that holds the vault — clients never need
vault access, and values never leave the lane host except inside restored
responses. Today, restore rights are as wide as lane access: per-user
attribution and RBAC over restore rights are planned (see
[ROADMAP.md](ROADMAP.md) Phase 3). Until then, treat lane access as restore
access, and use lane-per-tenant wherever audiences must not be able to
restore each other's tickets.

## Verifying a lane (audit-grade)

Two checks, both runnable against a live lane:

1. **Reversal probe** — see
   [PROXY.md — Verifying a lane](PROXY.md#verifying-a-lane-the-reversal-probe).
2. **Wire proof** — capture the loopback hop between the proxy and the model
   endpoint on the lane host (plain HTTP; nothing to decrypt):

```bash
sudo tcpdump -i lo -A -s0 'tcp dst port <MODEL_PORT>' > /tmp/llm-saw.cap
# run traffic through the lane, then:
grep -c '<the-value>' /tmp/llm-saw.cap   # 0 — the value never reached the endpoint
grep -c '<TICKET_1>'  /tmp/llm-saw.cap   # ≥1 — the ticket did
```

3. **Fingerprint a deployed instance** — `GET /status` on the lane returns the
   build version, upstream, vault path, entry count, and scrub counters. Compare
   before/after upgrades and during incident triage.
