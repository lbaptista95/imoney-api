using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Api.Tests.Support;
using YamlDotNet.RepresentationModel;

namespace Api.Tests;

/// <summary>S6: CI runs build, test and lint without a cloud subscription (C43-C51).</summary>
/// <remarks>In the contract-file collection: C46-C48 read contracts/openapi.json.</remarks>
[Collection(ContractFileCollection.Name)]
public sealed partial class CiTests
{
    private static readonly string Root = ProcessRunner.RepoRoot;

    private static readonly string Workflow = Path.Combine(Root, ".github", "workflows", "ci.yml");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- C43, C44: the workflow itself ---------------------------------------

    [Fact]
    public void CiWorkflowTriggersOnPullRequestAndCallsEveryStep()
    {
        // Parsed, not searched: a step disabled with `#` still contains its text, and
        // a text search would keep passing with the step switched off.
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Workflow)));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;

        var on = (YamlMappingNode)root.Children[new YamlScalarNode("on")];
        var pullRequest = (YamlMappingNode)on.Children[new YamlScalarNode("pull_request")];
        var branches = ((YamlSequenceNode)pullRequest.Children[new YamlScalarNode("branches")])
            .Select(b => ((YamlScalarNode)b).Value)
            .ToList();
        Assert.Contains("main", branches);

        var runs = ((YamlMappingNode)root.Children[new YamlScalarNode("jobs")])
            .Children.Values.Cast<YamlMappingNode>()
            .SelectMany(job => (YamlSequenceNode)job.Children[new YamlScalarNode("steps")])
            .Cast<YamlMappingNode>()
            .Where(step => step.Children.ContainsKey(new YamlScalarNode("run")))
            .Select(step => ((YamlScalarNode)step.Children[new YamlScalarNode("run")]).Value!)
            .ToList();

        Assert.Contains(runs, run => run.StartsWith("dotnet build", StringComparison.Ordinal));
        Assert.Contains(runs, run => run.Contains("dotnet format --verify-no-changes", StringComparison.Ordinal));
        Assert.Contains(runs, run => run.StartsWith("dotnet test", StringComparison.Ordinal));

        // Every script in ci/, enumerated from the directory rather than listed here,
        // so a script added later and never wired into the workflow fails this test.
        var scripts = Directory.GetFiles(Path.Combine(Root, "ci"), "*.cs")
            .Select(path => $"ci/{Path.GetFileName(path)}")
            .ToList();
        Assert.NotEmpty(scripts);

        foreach (var script in scripts)
        {
            Assert.True(
                runs.Any(run => run.Contains(script, StringComparison.Ordinal)),
                $"{script} exists but no step in ci.yml runs it");
        }
    }

    [Fact]
    public void CiChecksContractBeforeAnyBuild()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Workflow)));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;

        foreach (var job in ((YamlMappingNode)root.Children[new YamlScalarNode("jobs")]).Children.Values.Cast<YamlMappingNode>())
        {
            var runs = ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")])
                .Cast<YamlMappingNode>()
                .Select(step => step.Children.TryGetValue(new YamlScalarNode("run"), out var run)
                    ? ((YamlScalarNode)run).Value ?? string.Empty
                    : string.Empty)
                .ToList();

            var firstBuild = runs.FindIndex(run =>
                run.StartsWith("dotnet build", StringComparison.Ordinal)
                || run.StartsWith("dotnet test", StringComparison.Ordinal));
            Assert.True(firstBuild >= 0, "the job never builds");

            // The build regenerates contracts/openapi.json in place, so a check after it
            // compares the file with itself and always passes (F1). Both contract steps
            // must see the committed file, which means running before any build.
            foreach (var script in new[] { "ci/check-contract.cs", "ci/lint-contract.cs" })
            {
                var index = runs.FindIndex(run => run.Contains(script, StringComparison.Ordinal));
                Assert.True(index >= 0, $"{script} is not run");
                Assert.True(index < firstBuild, $"{script} runs at step {index}, after the build at step {firstBuild}");
            }
        }
    }

    [Fact]
    public void CiWorkflowNeedsNoCloudSubscription()
    {
        var text = File.ReadAllText(Workflow);

        Assert.DoesNotContain("azure", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AZURE_", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ACR", text, StringComparison.Ordinal);
        Assert.DoesNotContain("az ", text, StringComparison.Ordinal);
    }

    // ---- C45: the suite brings its own database ------------------------------

    [Fact]
    public void IntegrationSuiteUsesEphemeralTestcontainersPostgres()
    {
        var sources = Directory.GetFiles(Path.Combine(Root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sources);

        // Every environment read in the suite, by the variable it names. Only PATH is
        // allowed, and only to resolve executables; anything else - a host, a
        // connection string - would let the suite depend on a database it did not
        // start. No file is exempt, including this one.
        string[] allowed = ["PATH"];
        foreach (var source in sources)
        {
            foreach (Match read in EnvironmentRead().Matches(File.ReadAllText(source)))
            {
                Assert.True(
                    allowed.Contains(read.Groups[1].Value),
                    $"{Path.GetFileName(source)} reads the environment variable '{read.Groups[1].Value}'");
            }
        }

        var fixture = File.ReadAllText(sources.Single(path => path.EndsWith("PostgresFixture.cs", StringComparison.Ordinal)));
        Assert.Contains("new PostgreSqlBuilder(", fixture, StringComparison.Ordinal);
    }

    // ---- C46-C48: Spectral ---------------------------------------------------

    [Fact]
    public async Task SpectralFailsOnErrorResponseWithoutProblemJson()
    {
        var contract = CommittedContract();
        contract["paths"]!["/v1/transactions"]!["get"]!["responses"]!["400"]!["content"] =
            new JsonObject { ["application/json"] = new JsonObject() };

        var result = await LintAsync(contract);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("imoney-error-responses-are-problem-json", result.Output, StringComparison.Ordinal);
        Assert.Contains("/v1/transactions", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpectralFailsOnUnversionedDataRoute()
    {
        var contract = CommittedContract();
        var copy = contract["paths"]!["/v1/accounts"]!.DeepClone();

        // Its own operationId, so the only rule this copy can break is the versioning
        // one - a duplicate id would fail for an unrelated reason.
        copy["get"]!["operationId"] = "GetAccountsUnversioned";
        contract["paths"]!["/accounts-unversioned"] = copy;

        var result = await LintAsync(contract);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("imoney-data-routes-are-versioned", result.Output, StringComparison.Ordinal);
        Assert.Contains("/accounts-unversioned", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("operation-operationId-unique", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpectralPassesOnCommittedContractWithProjectRules()
    {
        var committed = await ProcessRunner.RunAsync(Root, "dotnet", ["ci/lint-contract.cs"]);
        Assert.True(committed.ExitCode == 0, $"the committed contract should lint clean:\n{committed.Output}");

        // "The project rules were loaded" is measured, not assumed: the same contract
        // linted with spectral:oas alone must report exactly two rules fewer. A ruleset
        // whose custom rules failed to register would pass the first assertion.
        var oasOnly = Path.Combine(Path.GetTempPath(), $"oas-only-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(oasOnly, "extends: [\"spectral:oas\"]\n", Ct);
            var baseline = await ProcessRunner.RunAsync(Root, "dotnet", ["ci/lint-contract.cs", "--ruleset", oasOnly]);

            Assert.Equal(RuleCount(baseline.Output) + 2, RuleCount(committed.Output));
        }
        finally
        {
            File.Delete(oasOnly);
        }
    }

    // ---- C49-C51: gitleaks ---------------------------------------------------

    [Fact]
    public async Task SecretScanFailsOnFakePrivateKeyInDiff()
    {
        var repo = await RepoWithCommittedFileAsync("deploy-key.pem", FakePrivateKey());
        try
        {
            var result = await ScanAsync(repo, "HEAD~1..HEAD");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("deploy-key.pem", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(repo);
        }
    }

    [Fact]
    public async Task SecretScanFailsClosedOnChecksumMismatch()
    {
        var cache = TempDirectory();

        // A well-formed archive in the right format, holding a file with the right
        // name - only the contents are wrong. Random bytes would also fail to extract,
        // so a script that skipped the checksum would still fail, for the wrong
        // reason. This archive extracts cleanly: the checksum is the only thing that
        // can stop it from running.
        var tampered = await TamperedArchiveAsync();

        try
        {
            var result = await ScanAsync(Root, "HEAD~1..HEAD", new Dictionary<string, string?>
            {
                ["GITLEAKS_CACHE_DIR"] = cache,
                ["GITLEAKS_DOWNLOAD_URL"] = tampered,
            });

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("checksum mismatch", result.Output, StringComparison.Ordinal);

            // Discarded, not merely reported: nothing that failed the check is left
            // behind where a later run could pick it up.
            Assert.Empty(Directory.GetFiles(cache, "*", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteTree(cache);
            DeleteTree(Path.GetDirectoryName(tampered)!);
        }
    }

    [Fact]
    public async Task SecretScanFailsClosedWhenGitleaksMissing()
    {
        var cache = TempDirectory();

        var path = MinimalPath();
        Assert.Null(FindOnPath("gitleaks", path));

        try
        {
            var result = await ScanAsync(Root, "HEAD~1..HEAD", new Dictionary<string, string?>
            {
                // gitleaks is off the PATH, nothing is cached, and the download cannot
                // reach anything: every way of obtaining the binary is closed.
                ["PATH"] = path,
                ["GITLEAKS_CACHE_DIR"] = cache,
                ["GITLEAKS_DOWNLOAD_URL"] = "http://127.0.0.1:1/gitleaks.zip",
            });

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("could not obtain gitleaks", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(cache);
        }
    }

    // ---- C67-C69: the remaining exits of the CI scripts ------------------------

    [Fact]
    public async Task LintFailsClosedWhenSpectralCannotRun()
    {
        // Without docker there is no Spectral, which is the same outcome as an image
        // that will not pull: nothing was linted, and that must not read as clean.
        var path = MinimalPath();
        Assert.Null(FindOnPath("docker", path));

        var result = await ProcessRunner.RunAsync(
            Root,
            FindOnPath("dotnet") ?? "dotnet",
            ["ci/lint-contract.cs"],
            environment: new Dictionary<string, string?> { ["PATH"] = path });

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Spectral did not run", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContractCheckFailsClosedWhenNothingIsGenerated()
    {
        // MSBuild reads environment variables as properties, so this turns off this
        // project's GenerateContract target for the script's own build: the build
        // succeeds and produces no contract, which is the failure that must not pass.
        var result = await ProcessRunner.RunAsync(
            Root,
            "dotnet",
            ["ci/check-contract.cs"],
            environment: new Dictionary<string, string?> { ["GenerateContract"] = "false" });

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("could not generate the contract", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecretScanPassesOnCleanRange()
    {
        var repo = await RepoWithCommittedFileAsync("notes.txt", "nothing secret here" + Environment.NewLine);
        try
        {
            var result = await ScanAsync(repo, "HEAD~1..HEAD");

            Assert.True(result.ExitCode == 0, $"a clean range did not pass: {result.Output}");
            Assert.Contains("found no secret", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(repo);
        }
    }

    /// <summary>
    /// A PATH with only what dotnet needs to run a file-based app. gitleaks and docker
    /// live elsewhere, which each test that uses this asserts before relying on it.
    /// </summary>
    private static string MinimalPath() => string.Join(
        Path.PathSeparator,
        new[] { Path.GetDirectoryName(FindOnPath("dotnet")), Environment.SystemDirectory }
            .Where(directory => !string.IsNullOrEmpty(directory)));

    // ---- helpers -------------------------------------------------------------

    private static async Task<string> TamperedArchiveAsync()
    {
        var staging = TempDirectory();
        var binary = OperatingSystem.IsWindows() ? "gitleaks.exe" : "gitleaks";
        await File.WriteAllTextAsync(Path.Combine(staging, binary), "not the real gitleaks", Ct);

        var output = Path.Combine(TempDirectory(), OperatingSystem.IsWindows() ? "gitleaks.zip" : "gitleaks.tar.gz");
        if (OperatingSystem.IsWindows())
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(staging, output);
        }
        else
        {
            await using var file = File.Create(output);
            await using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest);
            await System.Formats.Tar.TarFile.CreateFromDirectoryAsync(staging, gzip, includeBaseDirectory: false, Ct);
        }

        DeleteTree(staging);
        return output;
    }

    private static JsonNode CommittedContract() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "contracts", "openapi.json")))!;

    private static async Task<ProcessRunner.Result> LintAsync(JsonNode contract)
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "openapi.json");
        try
        {
            await File.WriteAllTextAsync(path, contract.ToJsonString(), Ct);
            return await ProcessRunner.RunAsync(Root, "dotnet", ["ci/lint-contract.cs", "--contract", path]);
        }
        finally
        {
            DeleteTree(directory);
        }
    }

    private static int RuleCount(string output)
    {
        var match = FoundRules().Match(output);
        Assert.True(match.Success, $"Spectral did not report its rule count:\n{output}");
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"Found (\d+) rules")]
    private static partial Regex FoundRules();

    /// <summary>A call that reads one named variable; a call with a non-literal name matches with an empty name and fails.</summary>
    [GeneratedRegex(@"GetEnvironmentVariable\(\s*(?:""([^""]*)"")?")]
    private static partial Regex EnvironmentRead();

    private static Task<ProcessRunner.Result> ScanAsync(
        string repo,
        string range,
        IDictionary<string, string?>? environment = null) =>
        ProcessRunner.RunAsync(
            Root,
            FindOnPath("dotnet") ?? "dotnet",
            ["ci/scan-secrets.cs", "--repo", repo, "--range", range],
            environment: environment);

    /// <summary>Resolves an executable the way a shell would, on the given PATH or this process's.</summary>
    private static string? FindOnPath(string name, string? path = null)
    {
        string[] names = OperatingSystem.IsWindows() ? [$"{name}.exe", $"{name}.cmd", name] : [name];

        return (path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(candidate => Path.Combine(directory, candidate)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// A throwaway repository with two commits, the second adding one file. Hooks are
    /// pointed at an empty directory, so the local pre-commit guard cannot stop the
    /// commit before the scan under test gets to see it.
    /// </summary>
    private static async Task<string> RepoWithCommittedFileAsync(string name, string content)
    {
        var repo = TempDirectory();
        var hooks = TempDirectory();

        await Git(repo, "init", "-q");
        await Git(repo, "commit", "-q", "--allow-empty", "-m", "base");
        await File.WriteAllTextAsync(Path.Combine(repo, name), content, Ct);
        await Git(repo, "add", name);
        await Git(repo, "commit", "-q", "-m", "add file");

        DeleteTree(hooks);
        return repo;

        async Task Git(string workingDirectory, params string[] args)
        {
            var result = await ProcessRunner.RunAsync(
                workingDirectory,
                "git",
                ["-c", "user.name=ci-test", "-c", "user.email=ci-test@example.invalid", "-c", $"core.hooksPath={hooks}", .. args]);
            Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)} failed:\n{result.Output}");
        }
    }

    /// <summary>
    /// A syntactically valid but fake PEM private key. The header is assembled from
    /// pieces so this source file never contains it whole: otherwise the repository's
    /// own pre-commit scan would flag the test that proves the CI scan works.
    /// </summary>
    private static string FakePrivateKey()
    {
        var label = string.Join(string.Empty, "PRIV", "ATE", " ", "KEY");
        var body = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(600));
        var lines = Enumerable.Range(0, (body.Length + 63) / 64)
            .Select(i => body.Substring(i * 64, Math.Min(64, body.Length - (i * 64))));

        return $"-----BEGIN RSA {label}-----\n{string.Join('\n', lines)}\n-----END RSA {label}-----\n";
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"imoney-ci-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // git marks its objects read-only on Windows, which Directory.Delete refuses.
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
