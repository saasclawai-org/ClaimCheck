using System.Text.Json;

namespace ClaimCheck;

/// <summary>vault-list — dump vault contents (ticket → value) for audit and inspection.</summary>
internal static class VaultList
{
    public static void Run(string[] args)
    {
        var summary = args.Contains("--summary");
        string? vaultPath = null;
        var i = Array.IndexOf(args, "--vault");
        if (i >= 0 && i + 1 < args.Length) vaultPath = args[i + 1];

        IEnumerable<FileInfo> files = vaultPath is not null
            ? [new FileInfo(Path.GetFullPath(vaultPath))]
            : Directory.Exists(Sanitizer.VaultDir)
                ? new DirectoryInfo(Sanitizer.VaultDir).EnumerateFiles("*.json")
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                : [];

        var any = false;
        foreach (var f in files)
        {
            any = true;
            if (!f.Exists) { Console.Error.WriteLine($"not found: {f.FullName}"); continue; }
            if (!VaultCrypto.TryReadJson(f.FullName, out var json, out var err))
            { Console.WriteLine($"{f.FullName}: locked or unreadable ({err})"); continue; }
            VaultData v;
            try { v = JsonSerializer.Deserialize<VaultData>(json) ?? new VaultData(); }
            catch { Console.WriteLine($"{f.FullName}: unreadable vault data"); continue; }
            Console.WriteLine($"== {f.FullName} — {v.Map.Count} entr{(v.Map.Count == 1 ? "y" : "ies")}");
            if (!summary)
                foreach (var (ph, val) in v.Map.OrderBy(k => k.Key, StringComparer.Ordinal))
                    Console.WriteLine($"  [{ph}] = {val}");
        }
        if (!any) Console.Error.WriteLine("no vaults found (run a scrub first, or pass --vault <path>)");
    }
}
