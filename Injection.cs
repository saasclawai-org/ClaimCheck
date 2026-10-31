using System.Text.RegularExpressions;

namespace ClaimCheck;

/// <summary>Tier-1 injection patterns (v0.9.4, spec: docs/INJECTION-TIER.md §Tier 1).
/// Deterministic, curated regex set — no ML, no downloads. Opt-in via
/// SANITIZE_INJECTION_PATTERNS=1 (default off: security test files and
/// prompt-engineering docs in a repo would otherwise self-ticket).
/// Patterns run on the Tier-0 normalized view, so zero-width/homoglyph/unicode
/// smuggled payloads are caught by the same machinery as every other tier.
/// Findings are ticketed like any other entity — the payload never crosses the
/// wire verbatim; restore only brings it back on the user's machine.</summary>
public static class Injection
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("SANITIZE_INJECTION_PATTERNS") is "1" or "on" or "true";

    private static readonly (string Type, Regex Rx, Func<string, bool>?)[] Patterns =
    [
        // direct instruction override
        ("INJECTION", new Regex(@"\b(?:ignore|disregard|forget|override)\b[^.\n]{0,40}\b(?:all\s+)?(?:previous|prior|above|earlier|preceding)\b[^.\n]{0,40}\b(?:instructions?|prompts?|rules?|directions?|constraints?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),
        ("INJECTION", new Regex(@"\b(?:ignore|disregard)\s+(?:the\s+)?(?:above|preceding)\b[^.\n]{0,40}\b(?:content|text|instructions?|rules?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),

        // prompt extraction
        ("INJECTION", new Regex(@"\b(?:reveal|show|print|repeat|output|display|repeat back|leak)\b[^.\n]{0,40}\b(?:your\s+)?(?:system\s+)?(?:prompt|instructions?|rules?|system message|initial message)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),
        ("INJECTION", new Regex(@"\bwhat(?:'s| is) your (?:system )?prompt\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),

        // persona jailbreaks
        ("INJECTION", new Regex(@"\bdo anything now\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),
        ("INJECTION", new Regex(@"\bact(?:ing)? as (?:an? )?(?:unrestricted|uncensored|unfiltered|jailbroken|DAN)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),
        ("INJECTION", new Regex(@"\byou are (?:now|no longer)\b[^.\n]{0,60}\b(?:unrestricted|uncensored|jailbroken|free|allowed|able to)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),
        ("INJECTION", new Regex(@"\bdeveloper mode (?:is\s+)?(?:now\s+)?enabled\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),

        // fake chat-template control tokens (im_start/im_end/endoftext/eot)
        ("INJECTION", new Regex(@"<\|(?:im_start|im_end|endoftext|eot|system)\|>", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),

        // secret-exfiltration directives
        ("INJECTION", new Regex(@"\b(?:send|post|upload|forward|exfiltrate|copy)\b[^.\n]{0,60}\b(?:api[- ]?keys?|secrets?|credentials?|passwords?|tokens?|vaults?|\.env)\b[^.\n]{0,60}\b(?:to|at|via|https?://)", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),

        // urgent-priority override preamble
        ("INJECTION", new Regex(@"\b(?:IMPORTANT|URGENT|ATTENTION)\s*:\s*(?:ignore|disregard|override|bypass)", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),
        ("INJECTION", new Regex(@"\b(?:new|updated|revised)\s+(?:system\s+)?(?:instructions?|rules?)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),

        // tool/agent hijack phrasing aimed at agent loops
        ("INJECTION", new Regex(@"\b(?:call|invoke|execute|run)\s+the\s+\w+\s+tool\s+(?:with|using)\s+(?:the\s+)?(?:following|these)", RegexOptions.IgnoreCase | RegexOptions.Compiled), null),
    ];

    /// <summary>Append the injection patterns to a pattern list (call sites use the
    /// same view-matching + vault-ticket machinery as every other tier).</summary>
    public static void Append(List<(string Type, Regex Rx, Func<string, bool>?)> list)
    {
        if (!Enabled) return;
        list.AddRange(Patterns);
    }
}
