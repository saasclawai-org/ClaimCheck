# DISCLAIMER — no warranty, no liability, not compliance advice

ClaimCheck is provided **"AS IS", WITHOUT WARRANTY OF ANY KIND** — the MIT license in
[`LICENSE`](LICENSE) applies in full. Extra plain language, because this tool deals with
sensitive data:

1. **Best-effort, not guaranteed.** No sanitizer — pattern-based, model-based, or human —
   catches 100% of sensitive data. ClaimCheck reduces risk; it does not eliminate it.
   Always verify sanitized output before sharing anything that matters.

2. **Not legal or compliance advice.** ClaimCheck is not certified under GDPR, HIPAA,
   PCI-DSS, SOC 2 or any other framework, and using it does not make your product or
   workflow compliant with any of them. You remain solely responsible for your data,
   your regulatory obligations, and your compliance decisions.

3. **Statistical NER.** The optional NER tier is a machine-learning model trained on
   English text. It will produce false negatives and false positives, especially on
   non-English text, unusual names, and abbreviations.

4. **The vault is local and unencrypted** (until the encryption tier ships). Placeholders
   are reversible by anyone with access to the machine's user profile. Treat the machine
   as the trust boundary.

5. **Defense in depth.** ClaimCheck is one control among many. Do not rely on it as your
   only safeguard for sensitive data.

6. **Third-party models.** HuggingFace NER models carry their own licenses — check the
   model card of whichever model you configure. Nothing here is affiliated with or
   endorsed by any model or LLM provider.

Bugs and false positives: https://github.com/saasclawai-org/ClaimCheck/issues — reports
make the tool better for everyone.
