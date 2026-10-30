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
    Console.Error.WriteLine("usage: ClaimCheck [status | version | vault-list [--vault <path>] [--summary] | sanitize-text <text> | sanitize-file <path> [--content] | import-clients <file> | restore <file|-> [--bare] | --proxy --upstream <base-url> [--port 8766]]");
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
        var bare = args.Contains("--bare");
        var positional = args.Skip(1).Where(a => a != "--bare").ToArray();
        if (positional.Length < 1)
        {
            Console.Error.WriteLine("usage: ClaimCheck restore <file|-> [--bare]   ('-' = stdin; --bare also restores bare-form tickets like SSN_2)");
            Environment.Exit(1);
        }
        var src = positional[0];
        string text;
        IEnumerable<FileInfo> vaultFiles;
        if (src == "-")
        {
            text = Console.In.ReadToEnd();
            vaultFiles = Directory.Exists(Sanitizer.VaultDir)
                ? new DirectoryInfo(Sanitizer.VaultDir).EnumerateFiles("*.json")
                : [];
        }
        else
        {
            var p = new FileInfo(Path.GetFullPath(src));
            if (!p.Exists) { Console.Error.WriteLine($"not found: {p.FullName}"); Environment.Exit(1); }
            text = File.ReadAllText(p.FullName);
            vaultFiles = [Sanitizer.LoadVault(Sanitizer.ProjectRoot(Sanitizer.DirOf(p.FullName))).File];
        }
        var bareHits = 0;
        foreach (var f in vaultFiles)
        {
            VaultData v;
            if (!VaultCrypto.TryReadJson(f.FullName, out var vjson, out var verr))
            { Console.Error.WriteLine($"claimcheck: skipping vault {f.Name} ({verr})"); continue; }
            try { v = JsonSerializer.Deserialize<VaultData>(vjson) ?? new VaultData(); }
            catch { continue; }
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
        }
        if (bareHits > 0 && !bare)
            Console.Error.WriteLine($"claimcheck: {bareHits} bare-form ticket identifier(s) detected — re-run with --bare to restore them");
        Console.Out.Write(text);
    }
}
