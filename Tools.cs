using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace ClaimCheck;

[McpServerToolType]
public static class Tools
{
    private static readonly JsonSerializerOptions JOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "read_sanitized")]
    [Description("Read a file with PII/secrets replaced by stable placeholders ([FEIN_1], [SSN_2], ...). Prefer this over read_file for any file that may contain client data. Original values are NEVER returned.")]
    public static string ReadSanitized(string path) => SanitizeFile(path, includeContent: true);

    [McpServerTool(Name = "scan_file")]
    [Description("Scan a file for PII/secrets. Returns a summary only (types, counts, line numbers). Does NOT return file content.")]
    public static string ScanFile(string path) => SanitizeFile(path, includeContent: false);

    [McpServerTool(Name = "sanitize_text")]
    [Description("Scrub PII/secrets from text before it is shared or committed. Returns sanitized text + findings (types/counts/line numbers — never original values).")]
    public static string SanitizeText(string text, string label = "")
    {
        var root = Sanitizer.ProjectRoot(new DirectoryInfo(Directory.GetCurrentDirectory()));
        var (vaultFile, vault) = Sanitizer.LoadVault(root);
        var patterns = Sanitizer.PatternsFor(root);
        var (lines, findings) = Sanitizer.SanitizeLines(text.Split('\n'), patterns, vault);
        Sanitizer.SaveVault(vaultFile, vault);
        return JsonSerializer.Serialize(new
        {
            label,
            summary = Sanitizer.Summary(findings),
            findings = findings.Take(200),
            sanitized = string.Join('\n', lines),
        }, JOpts);
    }

    private static string SanitizeFile(string path, bool includeContent)
    {
        var (text, fullPath) = Sanitizer.LoadTextFile(path);
        var root = Sanitizer.ProjectRoot(Sanitizer.DirOf(fullPath));
        var (vaultFile, vault) = Sanitizer.LoadVault(root);
        var patterns = Sanitizer.PatternsFor(root);
        var (lines, findings) = Sanitizer.SanitizeLines(text.Split('\n'), patterns, vault);
        Sanitizer.SaveVault(vaultFile, vault);
        if (!includeContent)
            return JsonSerializer.Serialize(new { path = fullPath, summary = Sanitizer.Summary(findings) }, JOpts);
        return JsonSerializer.Serialize(new
        {
            path = fullPath,
            summary = Sanitizer.Summary(findings),
            findings = findings.Take(200),
            content = string.Join('\n', lines),
        }, JOpts);
    }
}
