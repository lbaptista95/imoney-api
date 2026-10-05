// C38: the proof runner fails when the test a proof names does not run.
//
// Every proof in checks.md is `dotnet test ... --filter-method "*<Name>"`. If that
// command exited 0 when the name matched nothing, a proof pointing at a renamed or
// misspelled test would report green having proven nothing - which is exactly what
// `node --test --test-name-pattern` does, and why the workspace needed proof.mjs.
// The Microsoft Testing Platform does not have that hole; this script measures it
// instead of trusting it.
//
// It asserts exit code 8 specifically, MTP's code for "zero tests ran", not merely
// "non-zero": a build failure also exits non-zero, and must not pass this check.
//
//   dotnet run ci/assert-proof-runner-fails-closed.cs

using System.Diagnostics;

const int ZeroTestsRan = 8;
const string NoSuchTest = "*NoSuchTestName_C38_must_never_exist";

var root = FindRepoRoot();

var startInfo = new ProcessStartInfo("dotnet")
{
    WorkingDirectory = root,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
foreach (var arg in new[]
         {
             "test", "--project", "tests/Api.Tests/Api.Tests.csproj", "--", "--filter-method", NoSuchTest,
         })
{
    startInfo.ArgumentList.Add(arg);
}

using var process = Process.Start(startInfo)
    ?? throw new InvalidOperationException("could not start dotnet test");
var stdout = process.StandardOutput.ReadToEndAsync();
var stderr = process.StandardError.ReadToEndAsync();
await process.WaitForExitAsync();
var output = await stdout + await stderr;

if (process.ExitCode == ZeroTestsRan)
{
    Console.WriteLine($"ok: a filter matching no test exits {ZeroTestsRan} (zero tests ran), so a proof cannot pass without running.");
    return 0;
}

Console.Error.WriteLine(process.ExitCode == 0
    ? "FAIL: the runner exited 0 with no test run. Every proof in checks.md is untrustworthy until this is fixed."
    : $"FAIL: expected exit {ZeroTestsRan} (zero tests ran), got {process.ExitCode}. "
      + "That is a different failure - most likely the build - so this run proves nothing about the runner.");
Console.Error.WriteLine(output);
return 1;

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
