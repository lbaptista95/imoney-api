// C82: the CI's test step fails when a test fails.
//
// C80 pins the text of every run in ci.yml, but `dotnet test` also reads files the
// pin cannot see: a launchSettings.json with TESTINGPLATFORM_EXITCODE_IGNORE=2, or a
// csproj adding `--ignore-exit-code 2` to the runner's arguments, turns a failing
// suite into a passing step (F1 of the sixth verification). Listing every such file
// is a list that never closes, so this measures the effect instead: it runs the same
// command the CI runs, on a canary test that always fails, and requires exit code 2.
//
// It asserts 2 specifically, MTP's code for "at least one test failed", not merely
// "non-zero": a build failure also exits non-zero and proves nothing about the step.
// The canary must also be reported as failed, so a run that failed for another reason
// with the same code does not pass either.
//
//   dotnet run ci/assert-test-step-fails.cs [--root <repository>]
//   dotnet run ci/assert-test-step-fails.cs --decide <exit code> <canary reported: true|false>
//
// `--decide` runs nothing: it applies the decision table below to the given inputs, so
// every row can be proven where it is decided (`## Test policy` row 2).

using System.Diagnostics;

const int TestFailed = 2;
const string Canary = "TestStepCanaryAlwaysFails";
const int ZeroTestsRan = 8;

if (args.Length == 3 && args[0] == "--decide")
{
    var (passed, message) = Decide(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), bool.Parse(args[2]));
    (passed ? Console.Out : Console.Error).WriteLine(message);
    return passed ? 0 : 1;
}

var root = args.Length == 2 && args[0] == "--root"
    ? Path.GetFullPath(args[1])
    : args.Length == 0
        ? FindRepoRoot()
        : throw new ArgumentException(
            "usage: dotnet run ci/assert-test-step-fails.cs [--root <repository>] | --decide <exit code> <true|false>");

var startInfo = new ProcessStartInfo("dotnet")
{
    WorkingDirectory = root,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};

// The CI's own command, word for word, followed by what selects the canary.
foreach (var arg in new[]
         {
             "test", "--project", "tests/Api.Tests/Api.Tests.csproj",
             "--", "--filter-method", $"*{Canary}", "--explicit", "only",
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

var canaryReported = output.Contains($"CanaryTests.{Canary}", StringComparison.Ordinal);
var (ok, verdict) = Decide(process.ExitCode, canaryReported);

if (ok)
{
    Console.WriteLine(verdict);
    return 0;
}

Console.Error.WriteLine(verdict);
Console.Error.WriteLine(output);
return 1;

// The whole decision: only "a test failed" with the canary among the failures passes.
static (bool Passed, string Message) Decide(int exitCode, bool canaryReported) => (exitCode, canaryReported) switch
{
    (TestFailed, true) => (true, $"ok: the test step exits {TestFailed} when a test fails, so a red suite cannot pass the CI."),
    (0, true) => (false, "FAIL: the canary failed and the test step exited 0. Something the command reads turns test failures "
                         + "into success - look for TESTINGPLATFORM_EXITCODE_IGNORE or --ignore-exit-code in the test project."),
    (0, false) or (ZeroTestsRan, _) => (false, $"FAIL: the canary did not run (exit {exitCode}). Check that {Canary} still "
                         + "exists and is the Explicit test --explicit only selects."),
    (TestFailed, false) => (false, "FAIL: the test step exited 2, but the canary was not reported; something else failed."),
    _ => (false, $"FAIL: expected exit {TestFailed} (a test failed), got {exitCode}. "
                 + "That is a different failure - most likely the build - so this run proves nothing about the step."),
};

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
