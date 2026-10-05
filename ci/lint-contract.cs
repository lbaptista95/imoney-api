// AC 26: lint the OpenAPI contract with Spectral, failing on any error-level rule.
//
// Spectral runs from its pinned Docker image, not from Node: Docker is already
// required here and in CI, because the integration tests start PostgreSQL through
// Testcontainers, so this adds no runtime to the api runner.
//
//   dotnet ci/lint-contract.cs [--contract <path>] [--ruleset <path>]
//
// Exit codes: 0 clean, 1 an error-level violation, 2 could not lint. 2 is a failure
// too - a lint that cannot run must not pass.

// Tag and digest both: the tag says which version this is, the digest makes sure a
// re-pushed tag cannot change what runs.
const string Image = "stoplight/spectral:6.16.3@sha256:a07aa4455367b9501b574423b684ab0ef3ee42556089013583318ec015249050";

var root = FindRepoRoot();
var contract = Path.GetFullPath(Option(args, "--contract") ?? Path.Combine(root, "contracts", "openapi.json"));
var ruleset = Path.GetFullPath(Option(args, "--ruleset") ?? Path.Combine(root, ".spectral.yaml"));

foreach (var (label, path) in new[] { ("contract", contract), ("ruleset", ruleset) })
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"FAIL: the {label} {path} does not exist, so nothing can be linted.");
        return 2;
    }
}

// Each file is bind-mounted read-only on its own, so a contract or ruleset outside
// the repository (a test's temporary copy) is linted exactly like the committed one.
var result = await Run(
    "docker",
    "run", "--rm",
    "--mount", $"type=bind,source={Path.GetDirectoryName(contract)},target=/in/contract,readonly",
    "--mount", $"type=bind,source={Path.GetDirectoryName(ruleset)},target=/in/ruleset,readonly",
    Image,
    "lint", $"/in/contract/{Path.GetFileName(contract)}",
    "--ruleset", $"/in/ruleset/{Path.GetFileName(ruleset)}",
    "--fail-severity", "error",
    "--verbose");

Console.WriteLine(result.Output);

return result.ExitCode switch
{
    0 => 0,
    1 => 1,
    _ => Fail(result.ExitCode),
};

static int Fail(int code)
{
    Console.Error.WriteLine($"FAIL: Spectral did not run (exit {code}). Is Docker running?");
    return 2;
}

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static async Task<(int ExitCode, string Output)> Run(string file, params string[] arguments)
{
    var startInfo = new System.Diagnostics.ProcessStartInfo(file)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    try
    {
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
        // docker is not installed at all: a distinct "could not lint", never a pass.
        return (127, ex.Message);
    }
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
