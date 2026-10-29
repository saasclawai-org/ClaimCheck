using System.Text.Json;
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
    Console.Error.WriteLine("usage: ClaimCheck [status | version | sanitize-text <text> | sanitize-file <path> [--content] | import-clients <file> | restore <backup> | --proxy --upstream <base-url> [--port 8766]]");
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
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ClaimCheck restore <file|->   ('-' = stdin)");
            Environment.Exit(1);
        }
        var src = args[1];
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
        foreach (var f in vaultFiles)
        {
            VaultData v;
            if (!VaultCrypto.TryReadJson(f.FullName, out var vjson, out var verr))
            { Console.Error.WriteLine($"claimcheck: skipping vault {f.Name} ({verr})"); continue; }
            try { v = JsonSerializer.Deserialize<VaultData>(vjson) ?? new VaultData(); }
            catch { continue; }
            foreach (var (ph, val) in v.Map)
                text = text.Replace("[" + ph + "]", val);
        }
        Console.Out.Write(text);
    }
}
