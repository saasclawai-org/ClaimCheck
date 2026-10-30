using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ClaimCheck;

if (args.Length > 0 && args[0] == "import-clients")
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: ClaimCheck import-clients <csv-or-text-file>"); return 1; }
    CustomClients.ImportFrom(args[1]);
    CustomClients.Load();
    Console.Out.WriteLine($"imported {CustomClients.Count} client names -> {CustomClients.DataPath}");
    return 0;
}

if (args.Length > 0 && args[0] == "restore")
{
    Restore.Run(args);
    return 0;
}

if (args.Length > 0 && args[0] == "sanitize-text")
{
    var text = args.Length > 1 ? string.Join(' ', args[1..]) : Console.In.ReadToEnd();
    Console.Out.Write(Tools.SanitizeText(text));
    return 0;
}

if (args.Length > 0 && args[0] == "sanitize-file")
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: ClaimCheck sanitize-file <path> [--content]"); return 1; }
    Console.Out.Write(Tools.SanitizeFile(args[1], args.Contains("--content")));
    return 0;
}

if (args.Length > 0 && args[0] is "status" or "--status")
{
    return Status.Run(args);
}

if (args.Length > 0 && args[0] is "vault-list" or "--vault-list")
{
    VaultList.Run(args);
    return 0;
}

if (args.Length > 0 && args[0] is "version" or "--version")
{
    Console.Out.WriteLine($"ClaimCheck {Status.Version}");
    return 0;
}

if (args.Length > 0 && args[0] == "--proxy")
{
    return await Proxy.Run(args);
}

if (args.Length > 0)
{
    Console.Error.WriteLine($"Unknown command: {args[0]}");
    Console.Error.WriteLine("usage: ClaimCheck [status | version | vault-list [--vault <path>] [--summary] | sanitize-text <text> | sanitize-file <path> [--content] | import-clients <file> | restore <file|-> [--bare] [--vault <path>] | --proxy --upstream <base-url> [--port 8766]]");
    Console.Error.WriteLine("       ClaimCheck              (no arguments = MCP stdio server)");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders(); // stdio transport: NOTHING may write to stdout
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();
return 0;

internal static class Restore
{
    public static void Run(string[] args)
    {
        var bare = false;
        string? vaultPath = null;
        var positional = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--bare") bare = true;
            else if (args[i] == "--vault" && i + 1 < args.Length) vaultPath = args[++i];
            else positional.Add(args[i]);
        }
        if (positional.Count < 1)
        {
            Console.Error.WriteLine("usage: ClaimCheck restore <file|-> [--bare] [--vault <path>]   ('-' = stdin; --bare also restores bare-form tickets like SSN_2; --vault pins exactly one vault — no sweep)");
            Environment.Exit(1);
        }
        var src = positional[0];
        string text;

        // vault selection — v0.9.1 (audit finding #3): --vault pins exactly one vault; the
        // stdin sweep crosses project boundaries and whichever enumerates first silently wins.
        IEnumerable<FileInfo> vaultFiles;
        if (!string.IsNullOrWhiteSpace(vaultPath))
        {
            var vf = new FileInfo(Path.GetFullPath(Environment.ExpandEnvironmentVariables(vaultPath)));
            if (!vf.Exists) { Console.Error.WriteLine($"claimcheck: vault not found: {vf.FullName}"); Environment.Exit(1); }
            vaultFiles = [vf];
        }
        else if (src == "-")
        {
            vaultFiles = Directory.Exists(Sanitizer.VaultDir)
                ? new DirectoryInfo(Sanitizer.VaultDir).EnumerateFiles("*.json").OrderBy(f => f.Name, StringComparer.Ordinal)
                : [];
        }
        else
        {
            vaultFiles = [Sanitizer.LoadVault(Sanitizer.ProjectRoot(Sanitizer.DirOf(new FileInfo(Path.GetFullPath(src)).FullName))).File];
        }

        // text source — independent of vault selection (stdin, or the file restored in place)
        if (src == "-")
        {
            text = Console.In.ReadToEnd();
        }
        else
        {
            var p = new FileInfo(Path.GetFullPath(src));
            if (!p.Exists) { Console.Error.WriteLine($"not found: {p.FullName}"); Environment.Exit(1); }
            text = File.ReadAllText(p.FullName);
        }
        // v0.9.1 (audit finding #3): the sweep crosses project boundaries — a ticket that
        // exists in several vaults resolves to whichever enumerates first. Surface any
        // same-ticket/different-value collisions instead of silently gambling. Values are
        // never printed to stderr — vault names only.
        if (vaultFiles.Skip(1).Any())
        {
            var seen = new Dictionary<string, (string Val, string Vault)>(StringComparer.Ordinal);
            var conflictVaults = new Dictionary<string, (string A, string B)>(StringComparer.Ordinal);
            foreach (var f in vaultFiles)
            {
                if (!VaultCrypto.TryReadJson(f.FullName, out var cj, out var ce)) continue;
                VaultData cv;
                try { cv = JsonSerializer.Deserialize<VaultData>(cj) ?? new VaultData(); }
                catch { continue; }
                foreach (var (ph, val) in cv.Map)
                {
                    if (seen.TryGetValue(ph, out var prev))
                    {
                        if (prev.Val != val) conflictVaults[ph] = (prev.Vault, f.Name);
                    }
                    else seen[ph] = (val, f.Name);
                }
            }
            // only warn about tickets this restore would actually touch — vault-level
            // conflicts on unrelated tickets are noise (caught by test_restore: an
            // unrelated FEIN_1 conflict must not fire on a [SSN_1]-only restore)
            var touched = conflictVaults.Keys
                .Where(ph => text.Contains("[" + ph + "]") ||
                             new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(ph) + @"(?![A-Za-z0-9_])").IsMatch(text))
                .OrderBy(ph => ph, StringComparer.Ordinal)
                .ToList();
            if (touched.Count > 0)
            {
                Console.Error.WriteLine($"claimcheck: {touched.Count} ticket(s) defined in multiple vaults with DIFFERENT values — first vault wins; pass --vault <path> to pin one:");
                foreach (var ph in touched.Take(10))
                    Console.Error.WriteLine($"  [{ph}] — {conflictVaults[ph].A} vs {conflictVaults[ph].B}");
            }
        }
        var bareHits = 0;
        foreach (var f in vaultFiles)
        {
            VaultData v;
            if (!VaultCrypto.TryReadJson(f.FullName, out var vjson, out var verr))
            {
                if (!string.IsNullOrWhiteSpace(vaultPath))
                { Console.Error.WriteLine($"claimcheck: cannot read pinned vault {f.FullName} ({verr})"); Environment.Exit(1); }
                Console.Error.WriteLine($"claimcheck: skipping vault {f.Name} ({verr})"); continue;
            }
            try { v = JsonSerializer.Deserialize<VaultData>(vjson) ?? new VaultData(); }
            catch
            {
                if (!string.IsNullOrWhiteSpace(vaultPath))
                { Console.Error.WriteLine($"claimcheck: cannot parse pinned vault {f.FullName}"); Environment.Exit(1); }
                continue;
            }
            // longest-first: keeps SSN_1 from matching inside SSN_10 during the bare pass
            foreach (var (ph, val) in v.Map.OrderByDescending(kvp => kvp.Key.Length))
            {
                text = text.Replace("[" + ph + "]", val);
                // v0.9.0: bare-form tickets — models strip brackets when writing files
                // (captured 2026-10-29: employees.json written with bare SSN_2/SSN_3).
                // Boundary-guarded: never touches MY_SSN_1 variables, SSN_10 prefixes,
                // or SSN_1_extra/SSN_1_2 underscore-suffixed identifiers.
                var rx = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(ph) + @"(?![A-Za-z0-9_])");
                var hits = rx.Matches(text).Count;
                if (hits == 0) continue;
                bareHits += hits;
                if (bare) text = rx.Replace(text, val);
            }
            text = FakeData.RestoreFake(text, v);
        }
        if (bareHits > 0 && !bare)
            Console.Error.WriteLine($"claimcheck: {bareHits} bare-form ticket identifier(s) detected — re-run with --bare to restore them");
        Console.Out.Write(text);
    }
}
