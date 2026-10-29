// bundlepack — ClaimCheck Linux/macOS release packer + verification gates.
// Replicates the proven v0.8.8 manual pipeline (non-single-file R2R publish ->
// HostWriter.CreateAppHost binds singlefilehost -> HostModel Bundler packs the dir,
// host spec first) and encodes every lesson from the v0.8.6/v0.8.7 broken-bundle
// incidents as a hard gate:
//   1. entry identity (AssemblyName == ClaimCheck, version == expected)
//   2. entry assembly carries real R2R native code (instruction-pattern count)
//   3. framework corelib carries R2R native code
//   4. apphost bound: placeholder hash absent, app dll string present
//   5. bundle runs: `ClaimCheck version` prints the expected version, exit 0
//
// usage: dotnet bundlepack.dll <publishDir> <outBundle> <expectedVersion> <rid>
//   rid: linux-x64 | linux-arm64 | osx-x64 | osx-arm64 (must match the runner)
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.NET.HostModel.AppHost;
using Microsoft.NET.HostModel.Bundle;

if (args.Length != 4)
{
    Console.Error.WriteLine("usage: bundlepack <publishDir> <outBundle> <expectedVersion> <rid>");
    return 2;
}

var publish = Path.GetFullPath(args[0]);
var outPath = Path.GetFullPath(args[1]);
var expected = args[2].TrimStart('v');
var rid = args[3];

void Fail(string why)
{
    Console.Error.WriteLine($"GATE FAIL [{rid}]: {why}");
}

// --- gate 1: entry identity ---------------------------------------------------
var entryDll = Path.Combine(publish, "ClaimCheck.dll");
if (!File.Exists(entryDll)) { Fail($"missing {entryDll}"); return 1; }
AssemblyName id;
try { id = AssemblyName.GetAssemblyName(entryDll); }
catch (Exception e) { Fail($"entry assembly unreadable: {e.Message}"); return 1; }
if (!string.Equals(id.Name, "ClaimCheck", StringComparison.OrdinalIgnoreCase))
{
    Fail($"entry identity is '{id.Name}', expected ClaimCheck");
    return 1;
}
if (!id.Version!.ToString().StartsWith(expected, StringComparison.Ordinal))
{
    Fail($"entry version {id.Version} != expected {expected} (bump csproj Version to match the tag)");
    return 1;
}

// --- gate 2/3: R2R native code present ---------------------------------------
var isArm = rid.Contains("arm64");
var entryMarks = CountNativeMarks(File.ReadAllBytes(entryDll), isArm);
var entryMin = isArm ? 100L : 50L;
if (entryMarks < entryMin) { Fail($"entry R2R marks {entryMarks} < {entryMin} — IL-only build shipped!"); return 1; }

var corelib = Path.Combine(publish, "System.Private.CoreLib.dll");
var coreMarks = File.Exists(corelib) ? CountNativeMarks(File.ReadAllBytes(corelib), isArm) : 0;
var coreMin = isArm ? 5000L : 2000L;
if (coreMarks < coreMin) { Fail($"corelib R2R marks {coreMarks} < {coreMin} — framework not ReadyToRun"); return 1; }

// --- gate 4: host pack present, bind the apphost ------------------------------
var host = FindSingleFileHost(rid);
if (host is null) { Fail($"singlefilehost not found for {rid} — restore Microsoft.NETCore.App.Host.{rid} first"); return 1; }

var bound = Path.Combine(Path.GetTempPath(), $"cc-host-{Guid.NewGuid():N}");
HostWriter.CreateAppHost(host, bound, "ClaimCheck.dll", windowsGraphicalUserInterface: false);

const string PlaceholderHash = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";
var boundText = Encoding.ASCII.GetString(File.ReadAllBytes(bound));
if (boundText.Contains(PlaceholderHash)) { Fail("bound host still contains the placeholder hash (unbound)"); return 1; }
if (!boundText.Contains("ClaimCheck.dll")) { Fail("bound host lacks the app dll string"); return 1; }

// --- pack: host spec first, then every publish file (no pdbs, no SDK apphost) -
var outDir = Path.GetDirectoryName(outPath)!;
Directory.CreateDirectory(outDir);
var files = new List<FileSpec> { new(bound, "ClaimCheck") };
foreach (var f in new DirectoryInfo(publish).EnumerateFiles())
{
    if (f.Name == "ClaimCheck") continue; // SDK's own apphost — replaced by our bound host
    if (f.Name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
    files.Add(new FileSpec(f.FullName, f.Name));
}

var bundler = new Bundler(hostName: "ClaimCheck", outputDir: outDir);
var produced = bundler.GenerateBundle(files);
if (!string.Equals(Path.GetFullPath(produced), outPath, StringComparison.Ordinal))
{
    if (File.Exists(outPath)) File.Delete(outPath);
    File.Move(produced, outPath);
}

var bundleText = Encoding.ASCII.GetString(File.ReadAllBytes(outPath));
if (bundleText.Contains(PlaceholderHash)) { Fail("final bundle contains the placeholder hash"); return 1; }

if (!OperatingSystem.IsWindows())
{
    File.SetUnixFileMode(outPath,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
}

// --- gate 5: the shipped artifact runs and reports the right version ----------
var psi = new ProcessStartInfo(outPath, "version")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
using var proc = Process.Start(psi)!;
var stdout = await proc.StandardOutput.ReadToEndAsync();
if (!proc.WaitForExit(120_000)) { proc.Kill(true); Fail("version check timed out after 120s"); return 1; }
if (proc.ExitCode != 0) { Fail($"version exited {proc.ExitCode}: {proc.StandardError.ReadToEnd().Trim()}"); return 1; }
if (!stdout.Trim().StartsWith($"ClaimCheck {expected}", StringComparison.Ordinal))
{
    Fail($"version printed '{stdout.Trim()}', expected 'ClaimCheck {expected}'");
    return 1;
}

Console.WriteLine($"OK [{rid}]: identity ClaimCheck v{id.Version} | entry marks {entryMarks} | corelib marks {coreMarks} | bundle {outPath} ({new FileInfo(outPath).Length:N0} bytes) | version gate passed");
return 0;

static long CountNativeMarks(byte[] data, bool isArm)
{
    byte[] pat = isArm ? [0xC0, 0x03, 0x5F, 0xD6] : [0x48, 0x83, 0xEC]; // arm64 ret / x64 sub rsp,imm8
    long count = 0;
    for (int i = 0; i + pat.Length <= data.Length; i++)
    {
        int j = 0;
        for (; j < pat.Length; j++)
        {
            if (data[i + j] != pat[j]) break;
        }
        if (j == pat.Length) count++;
    }
    return count;
}

static string? FindSingleFileHost(string rid)
{
    var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages",
        $"microsoft.netcore.app.host.{rid}");
    if (!Directory.Exists(cache)) return null;
    return Directory.EnumerateFiles(cache, "singlefilehost", SearchOption.AllDirectories)
        .OrderByDescending(p => p)
        .FirstOrDefault();
}
