// AC 24: fail when contracts/openapi.json differs from what a clean build generates.
//
// Builds the API with the generated document redirected to a temporary file, then
// compares it with the committed one. The working tree is never written: a check
// that "fixes" the file it is checking would hide the drift it exists to report.
//
//   dotnet ci/check-contract.cs
//
// Exit codes: 0 current, 1 out of date, 2 could not check. 2 is a failure too -
// a check that cannot run must not pass.

using System.Diagnostics;

const string ContractPath = "contracts/openapi.json";

var root = FindRepoRoot();
var committed = Path.Combine(root, ContractPath);

if (!File.Exists(committed))
{
    Console.Error.WriteLine($"FAIL: {ContractPath} does not exist. Run `dotnet build` and commit it.");
    return 2;
}

var generated = Path.Combine(Path.GetTempPath(), $"openapi-{Guid.NewGuid():N}.json");

try
{
    var build = await RunAsync(
        root,
        "dotnet",
        "build", "src/Api/Api.csproj", "-nologo", "-v", "quiet", $"-p:ContractPath={generated}");

    if (build.ExitCode != 0 || !File.Exists(generated))
    {
        Console.Error.WriteLine("FAIL: could not generate the contract from a clean build, so it cannot be checked.");
        Console.Error.WriteLine(build.Output);
        return 2;
    }

    // Line endings are normalized: git may check the file out with CRLF on Windows,
    // and that is not drift in the contract.
    var expected = Normalize(await File.ReadAllTextAsync(generated));
    var actual = Normalize(await File.ReadAllTextAsync(committed));

    if (expected != actual)
    {
        Console.Error.WriteLine(
            $"FAIL: {ContractPath} is out of date. It differs from what a clean build generates. "
            + "Run `dotnet build` and commit the regenerated file.");
        return 1;
    }

    Console.WriteLine($"ok: {ContractPath} matches a clean build.");
    return 0;
}
finally
{
    File.Delete(generated);
}

static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

static async Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, string file, params string[] args)
{
    var startInfo = new ProcessStartInfo(file)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var arg in args)
    {
        startInfo.ArgumentList.Add(arg);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"could not start {file}");
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
