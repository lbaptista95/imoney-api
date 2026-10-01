// AC 27: scan a commit range for secrets with gitleaks, independently of the local
// pre-commit hook - which `git commit --no-verify` skips.
//
// gitleaks is pinned to the same 8.30.1 the hook uses, downloaded from its GitHub
// release and verified against the checksum recorded here before it ever runs. No
// third-party action, so nothing between the release and this check can change it.
//
//   dotnet ci/scan-secrets.cs [--repo <path>] [--range <git rev range>]
//
// Exit codes: 0 no leak, 1 a leak, 2 could not scan. 2 is a failure too
// (constitution §6): a missing binary, a failed download or a checksum that does
// not match never turns into a pass.
//
// GITLEAKS_DOWNLOAD_URL replaces where the archive comes from (a URL or a local
// path), so the fail-closed paths can be tested offline. It cannot replace the
// checksum: that stays pinned below, which is the whole point of C50.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

const string Version = "8.30.1";

// From https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_checksums.txt
var pinned = new Dictionary<string, (string Asset, string Sha256)>
{
    ["linux-x64"] = ($"gitleaks_{Version}_linux_x64.tar.gz", "551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb"),
    ["win-x64"] = ($"gitleaks_{Version}_windows_x64.zip", "d29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e"),
};

var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";
var (asset, sha256) = pinned[platform];

var repo = Path.GetFullPath(Option(args, "--repo") ?? FindRepoRoot());
var range = Option(args, "--range") ?? "origin/main..HEAD";

var cacheRoot = Environment.GetEnvironmentVariable("GITLEAKS_CACHE_DIR")
    ?? Path.Combine(Path.GetTempPath(), "imoney-gitleaks");
var cache = Path.Combine(cacheRoot, $"{Version}-{sha256[..12]}");
var binary = Path.Combine(cache, platform == "win-x64" ? "gitleaks.exe" : "gitleaks");
var archive = Path.Combine(cache, asset);

try
{
    await EnsureBinaryAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: could not obtain gitleaks {Version}: {ex.Message}");
    Console.Error.WriteLine("Nothing was scanned, so this is a failure, not a pass.");
    return 2;
}

(int ExitCode, string Output) scan;
try
{
    scan = await RunAsync(
        binary,
        repo,
        "git", "--log-opts", range, "--redact", "--no-banner", "--verbose", "--exit-code", "1");
}
catch (Exception ex)
{
    // A binary that will not execute is "could not scan", never a pass - and never a
    // crash either, which would hide which of the three outcomes this was.
    Console.Error.WriteLine($"FAIL: could not run gitleaks {Version}: {ex.Message}");
    return 2;
}

Console.WriteLine(scan.Output);

switch (scan.ExitCode)
{
    case 0:
        Console.WriteLine($"ok: gitleaks {Version} found no secret in {range}.");
        return 0;
    case 1:
        Console.Error.WriteLine($"FAIL: gitleaks {Version} found a secret in {range}. The file is named above.");
        return 1;
    default:
        Console.Error.WriteLine($"FAIL: gitleaks exited {scan.ExitCode}, so the range was not scanned.");
        return 2;
}

async Task EnsureBinaryAsync()
{
    // The archive is kept and re-verified on every run: a cached binary nobody checks
    // is a binary anyone could have replaced.
    if (File.Exists(archive) && Sha256Of(archive) == sha256 && File.Exists(binary))
    {
        return;
    }

    Directory.CreateDirectory(cache);
    var source = Environment.GetEnvironmentVariable("GITLEAKS_DOWNLOAD_URL")
        ?? $"https://github.com/gitleaks/gitleaks/releases/download/v{Version}/{asset}";

    var partial = archive + ".partial";
    if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        await using var download = await http.GetStreamAsync(uri);
        await using var file = File.Create(partial);
        await download.CopyToAsync(file);
    }
    else
    {
        File.Copy(source, partial, overwrite: true);
    }

    var actual = Sha256Of(partial);
    if (actual != sha256)
    {
        File.Delete(partial);
        throw new InvalidOperationException(
            $"checksum mismatch for {asset}: expected {sha256}, got {actual}. The archive was discarded and not run.");
    }

    File.Move(partial, archive, overwrite: true);

    if (asset.EndsWith(".zip", StringComparison.Ordinal))
    {
        ZipFile.ExtractToDirectory(archive, cache, overwriteFiles: true);
    }
    else
    {
        await using var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, cache, overwriteFiles: true);
    }

    if (!File.Exists(binary))
    {
        throw new InvalidOperationException($"{asset} did not contain {Path.GetFileName(binary)}");
    }
}

static string Sha256Of(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static async Task<(int ExitCode, string Output)> RunAsync(string file, string workingDirectory, params string[] arguments)
{
    var startInfo = new ProcessStartInfo(file)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, await stdout + await stderr);
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Api.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new DirectoryNotFoundException("Api.slnx not found above the current directory; run this from inside repos/api.");
}
