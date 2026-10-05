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
        // A Docker daemon nobody can reach: `docker run` fails before Spectral exists,
        // which is the same outcome as an image that will not pull. Nothing was linted,
        // and that must not read as clean. DOCKER_HOST rather than a stripped PATH, so
        // the test means the same on the Linux runner, where dotnet and docker can share
        // a directory.
        var result = await ProcessRunner.RunAsync(
            Root,
            "dotnet",
            ["ci/lint-contract.cs"],
            environment: new Dictionary<string, string?> { ["DOCKER_HOST"] = "tcp://127.0.0.1:1" });

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

    [Fact]
    public void NoCiStepSwallowsItsFailure()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Workflow)));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var continueOnError = new YamlScalarNode("continue-on-error");

        var jobs = ((YamlMappingNode)root.Children[new YamlScalarNode("jobs")]).Children;
        Assert.NotEmpty(jobs);

        foreach (var (jobName, jobNode) in jobs)
        {
            var job = (YamlMappingNode)jobNode;
            Assert.False(job.Children.ContainsKey(continueOnError), $"job {jobName} sets continue-on-error");

            var steps = ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();
            foreach (var step in steps)
            {
                Assert.False(step.Children.ContainsKey(continueOnError), $"a step in {jobName} sets continue-on-error");

                if (!step.Children.TryGetValue(new YamlScalarNode("run"), out var runNode))
                {
                    continue;
                }

                // GitHub expressions are evaluated before the shell sees the line, and
                // their `||` is not a shell operator; only the shell text is judged.
                var shell = Expression().Replace(((YamlScalarNode)runNode).Value ?? string.Empty, "EXPR");

                foreach (var (pattern, meaning) in SwallowingForms)
                {
                    Assert.False(
                        Regex.IsMatch(shell, pattern),
                        $"step `{shell.Trim()}` can hide its own failure: {meaning}");
                }
            }
        }
    }

    /// <summary>
    /// Each shell form that turns a failing command into a passing step. The step's exit
    /// code is the last command's, so anything that runs after a failure and succeeds -
    /// or a pipe whose last stage succeeds - makes the step green.
    /// </summary>
    private static readonly (string Pattern, string Meaning)[] SwallowingForms =
    [
        (@"\|\|", "`||` runs something else when the command fails"),
        (@"(?<![|])\|(?![|])", "a pipe reports the last stage's exit code, not the first's"),
        (@";\s*true\b", "`; true` ends the step with success"),
        (@"\bset\s+\+e\b", "`set +e` keeps going after a failure"),
        (@"\bexit\s+0\b", "`exit 0` ends the step with success"),
    ];

    [GeneratedRegex(@"\$\{\{.*?\}\}", RegexOptions.Singleline)]
    private static partial Regex Expression();

    // ---- C79: the one shape the workflow may take ----------------------------

    /// <summary>
    /// C71 forbids spellings, and four rounds of verification kept finding the next one.
    /// This asserts the shape instead: a closed list of keys at every level and one
    /// dotnet invocation per run, so a form nobody has thought of yet fails too. The
    /// committed workflow passes, and each form already found is rejected for its own
    /// reason - not merely rejected.
    /// </summary>
    [Fact]
    public void CiWorkflowHasOnlyTheAllowedShape()
    {
        var committed = File.ReadAllText(Workflow).ReplaceLineEndings("\n");
        Assert.Empty(ShapeViolations(committed));

        const string format = "        run: dotnet format --verify-no-changes\n";
        Assert.Contains(format, committed);
        const string permissions = "permissions:\n  contents: read\n";
        Assert.Contains(permissions, committed);
        const string rangeLiteral = "'{0}..HEAD'";
        Assert.Contains(rangeLiteral, committed);
        var scan = committed.Split('\n').Single(line => line.Contains("run: dotnet ci/scan-secrets.cs", StringComparison.Ordinal)) + "\n";

        var variants = new (string Name, string Workflow, string Reason)[]
        {
            ("|| true", committed.Replace(format, "        run: dotnet format --verify-no-changes || true\n"), "shell operator `|`"),
            ("&& and another line", committed.Replace(format, "        run: |\n          dotnet format --verify-no-changes && echo formatted\n          echo done\n"), "more than one line"),
            ("if: false", committed.Replace(format, "        if: ${{ false }}\n" + format), "key `if`"),
            ("background &", committed.Replace(format, "        run: dotnet format --verify-no-changes &\n"), "shell operator `&`"),
            ("shell: bash {0}", committed.Replace(format, "        shell: bash {0}\n        run: |\n          dotnet format --verify-no-changes\n          echo done\n"), "key `shell`"),
            ("defaults.run.shell", committed.Replace(permissions, permissions + "\ndefaults:\n  run:\n    shell: bash {0}\n"), "key `defaults`"),
            ("continue-on-error", committed.Replace(format, "        continue-on-error: true\n" + format), "key `continue-on-error`"),
            ("operator in an expression literal", committed.Replace(rangeLiteral, "'{0}..HEAD; true'"), "expression literal"),
            ("line break inside an expression", committed.Replace(scan, FoldedScan.ReplaceLineEndings("\n")), "more than one line"),
            ("not dotnet", committed.Replace(format, "        run: echo dotnet format --verify-no-changes\n"), "single dotnet invocation"),
        };

        foreach (var (name, workflow, reason) in variants)
        {
            Assert.NotEqual(committed, workflow);
            var violations = ShapeViolations(workflow);
            Assert.True(
                violations.Any(violation => violation.Contains(reason, StringComparison.Ordinal)),
                $"the {name} form was not rejected for `{reason}`; violations: [{string.Join("; ", violations)}]");
        }
    }

    /// <summary>
    /// The secret scan as it was before C79: a folded block keeps the breaks of the
    /// more-indented lines, so the expression reaches the parsed value with three.
    /// </summary>
    private const string FoldedScan = """
                run: >-
                  dotnet ci/scan-secrets.cs --range
                  ${{ github.event_name == 'pull_request'
                      && format('origin/{0}..HEAD', github.base_ref)
                      || format('{0}..HEAD', github.event.before) }}

        """;

    private static readonly string[] WorkflowKeys = ["name", "on", "permissions", "jobs"];

    private static readonly string[] JobKeys = ["runs-on", "steps"];

    private static readonly string[] ActionStepKeys = ["uses", "name", "with"];

    private static readonly string[] RunStepKeys = ["name", "run"];

    private static readonly string[] ShellOperators = [";", "&", "|", "<", ">", "`", "$("];

    /// <summary>Every way the workflow text leaves the allowed shape; empty when it is in it.</summary>
    private static List<string> ShapeViolations(string workflow)
    {
        var violations = new List<string>();
        var yaml = new YamlStream();
        yaml.Load(new StringReader(workflow));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;

        Keys(root, WorkflowKeys, "the workflow");

        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        foreach (var (jobName, jobNode) in jobs.Children)
        {
            var job = (YamlMappingNode)jobNode;
            Keys(job, JobKeys, $"job {jobName}");

            foreach (var step in ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>())
            {
                if (step.Children.ContainsKey(new YamlScalarNode("uses")))
                {
                    Keys(step, ActionStepKeys, "an action step");
                    continue;
                }

                Keys(step, RunStepKeys, "a run step");
                if (!step.Children.TryGetValue(new YamlScalarNode("run"), out var runNode))
                {
                    violations.Add("a step has neither `uses` nor `run`");
                    continue;
                }

                Run(((YamlScalarNode)runNode).Value ?? string.Empty);
            }
        }

        return violations;

        void Keys(YamlMappingNode node, string[] allowed, string where)
        {
            foreach (var key in node.Children.Keys.Select(k => ((YamlScalarNode)k).Value!))
            {
                if (!allowed.Contains(key))
                {
                    violations.Add($"{where} has the key `{key}`, outside [{string.Join(", ", allowed)}]");
                }
            }
        }

        void Run(string run)
        {
            // A literal block ends in one newline, which is not a second command. Any
            // other break fails, counted as parsed - even inside an expression.
            var line = run.EndsWith('\n') ? run[..^1] : run;
            if (line.Contains('\n'))
            {
                violations.Add($"run `{line}` spans more than one line");
            }

            // Expressions are evaluated before the shell sees the line, so their own
            // operators are not shell text - but the literals they put into it are.
            foreach (Match expression in Expression().Matches(line))
            {
                foreach (Match literal in ExpressionLiteral().Matches(expression.Value))
                {
                    foreach (var op in ShellOperators.Where(op => literal.Value.Contains(op, StringComparison.Ordinal)))
                    {
                        violations.Add($"expression literal {literal.Value} puts the shell operator `{op}` into run `{line}`");
                    }
                }
            }

            var shell = Expression().Replace(line, "EXPR");

            foreach (var op in ShellOperators.Where(op => shell.Contains(op, StringComparison.Ordinal)))
            {
                violations.Add($"run `{line}` has the shell operator `{op}`");
            }

            if (!shell.StartsWith("dotnet ", StringComparison.Ordinal))
            {
                violations.Add($"run `{line}` is not a single dotnet invocation");
            }
        }
    }

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex ExpressionLiteral();

    // ---- C80: the CI commands, pinned line by line ---------------------------

    /// <summary>
    /// C79 closed the vocabulary and the fifth verification walked through the arguments:
    /// `-- --ignore-exit-code 2` turns a failing suite into a passing step, and an
    /// expression can put into the line a value none of its literals spell. So the
    /// content is pinned instead: every run is exactly one of these lines. Changing the
    /// CI means changing this list too, in the same diff.
    /// </summary>
    private static readonly string[] ListedRuns =
    [
        "dotnet ci/check-contract.cs",
        "dotnet ci/lint-contract.cs",
        "dotnet build",
        "dotnet format --verify-no-changes",
        "dotnet test --project tests/Api.Tests/Api.Tests.csproj",
        "dotnet run ci/assert-test-step-fails.cs",
        "dotnet run ci/assert-proof-runner-fails-closed.cs",
        "dotnet ci/scan-secrets.cs --range ${{ github.event_name == 'pull_request' && format('origin/{0}..HEAD', github.base_ref) || format('{0}..HEAD', github.event.before) }}",
    ];

    [Fact]
    public void CiRunsAreExactlyTheListedCommands()
    {
        var committed = File.ReadAllText(Workflow).ReplaceLineEndings("\n");
        Assert.Empty(PinnedRunViolations(committed));

        const string test = "        run: dotnet test --project tests/Api.Tests/Api.Tests.csproj\n";
        Assert.Contains(test, committed);
        const string build = "      - name: Build\n        run: dotnet build\n";
        Assert.Contains(build, committed);
        const string formatting = "      - name: Formatting\n        run: dotnet format --verify-no-changes\n";
        Assert.Contains(formatting, committed);
        const string rangeLiteral = "'{0}..HEAD'";
        Assert.Contains(rangeLiteral, committed);

        var variants = new (string Name, string Workflow, string Reason)[]
        {
            ("--ignore-exit-code", committed.Replace(test, test[..^1] + " -- --ignore-exit-code 2\n"), "not one of the listed commands"),
            ("fromJSON value", committed.Replace(test, test[..^1] + @" ${{ fromJSON('""\u007c\u007c true""') }}" + "\n"), "not one of the listed commands"),
            ("changed expression", committed.Replace(rangeLiteral, "'{0}..HEAD~1'"), "not one of the listed commands"),
            ("dropped step", committed.Replace(formatting, string.Empty), "missing from ci.yml"),
            ("step without name", committed.Replace(build, "      - run: dotnet build\n"), "has no name"),
        };

        foreach (var (name, workflow, reason) in variants)
        {
            Assert.NotEqual(committed, workflow);
            var violations = PinnedRunViolations(workflow);
            Assert.True(
                violations.Any(violation => violation.Contains(reason, StringComparison.Ordinal)),
                $"the {name} form was not rejected for `{reason}`; violations: [{string.Join("; ", violations)}]");
        }
    }

    // ---- C81: the whole workflow, as reviewed -------------------------------

    /// <summary>
    /// C79 and C80 close the run lines; the sixth verification walked around them through
    /// an action step's `with:` (checkout `ref: main` tests main instead of the PR) and a
    /// trigger filter (`paths-ignore` switches the CI off). Closing each key is a list
    /// that never ends, so the whole file is pinned: any change to the CI fails here
    /// until this copy changes in the same diff, where it is reviewed.
    /// </summary>
    [Fact]
    public void CiWorkflowIsTheReviewedCopy()
    {
        var committed = File.ReadAllText(Workflow).ReplaceLineEndings("\n");
        Assert.Equal(ReviewedWorkflow.ReplaceLineEndings("\n"), committed);
    }

    /// <summary>The CI workflow as last reviewed. Change it only together with ci.yml.</summary>
    private const string ReviewedWorkflow = """
        # CI for imoney-api. Runs on every pull request against main, and on main itself.
        #
        # Every step either is a dotnet command or calls a script in ci/, so the whole job
        # can be reproduced on a developer machine with the same commands. Nothing here
        # needs a cloud subscription: the database is an ephemeral PostgreSQL that the
        # integration tests start themselves through Testcontainers.
        name: ci

        on:
          pull_request:
            branches: [main]
          push:
            branches: [main]

        permissions:
          contents: read

        jobs:
          build-and-test:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v7
                with:
                  # The full history, so the secret scan can walk the pull request's range.
                  fetch-depth: 0

              - uses: actions/setup-dotnet@v6
                with:
                  dotnet-version: "10.0.x"

              # Before any build, on purpose: building regenerates contracts/openapi.json in
              # place, so a check after it would compare the file with itself and always
              # pass. Both contract steps must see the file as committed.
              - name: Contract is current
                run: dotnet ci/check-contract.cs

              - name: Contract lint
                run: dotnet ci/lint-contract.cs

              - name: Build
                run: dotnet build

              - name: Formatting
                run: dotnet format --verify-no-changes

              - name: Unit and integration tests
                run: dotnet test --project tests/Api.Tests/Api.Tests.csproj

              # The pinned command above cannot show what it reads: a launchSettings.json or a
              # csproj property can make it exit 0 with tests failing. This runs it on a canary
              # that always fails and requires the failure to reach the exit code.
              - name: Test step fails on a failing test
                run: dotnet run ci/assert-test-step-fails.cs

              - name: Proof runner fails closed
                run: dotnet run ci/assert-proof-runner-fails-closed.cs

              # Independent of the local pre-commit hook, which `git commit --no-verify`
              # skips. On a pull request the range is what the branch adds to main; on main
              # it is what the push added.
              - name: Secret scan
                # One line on purpose: C79 allows no line break anywhere in a run, not even
                # inside an expression.
                run: dotnet ci/scan-secrets.cs --range ${{ github.event_name == 'pull_request' && format('origin/{0}..HEAD', github.base_ref) || format('{0}..HEAD', github.event.before) }}

        """;

    // ---- C82: the test step's canary catches what turns its exit code off ----

    /// <summary>
    /// The two configurations the sixth verification measured, each applied to a copy of
    /// the tree. The canary script must fail on both, and for the reason that matters -
    /// the step exiting 0 - not because the copy failed to build.
    /// </summary>
    [Fact]
    public async Task TestStepCanaryFailsWhenExitCodeIsIgnored()
    {
        var mutants = new (string Name, Action<string> Apply)[]
        {
            ("launchSettings.json with TESTINGPLATFORM_EXITCODE_IGNORE", copy =>
            {
                var properties = Path.Combine(copy, "tests", "Api.Tests", "Properties");
                Directory.CreateDirectory(properties);
                File.WriteAllText(
                    Path.Combine(properties, "launchSettings.json"),
                    """{ "profiles": { "Api.Tests": { "commandName": "Project", "environmentVariables": { "TESTINGPLATFORM_EXITCODE_IGNORE": "2" } } } }""");
            }),
            ("--ignore-exit-code in the test csproj", copy =>
            {
                var csproj = Path.Combine(copy, "tests", "Api.Tests", "Api.Tests.csproj");
                var text = File.ReadAllText(csproj);
                // Appended to the project's own runner arguments: a second property
                // before it would be overridden and the mutant would change nothing.
                const string anchor = "</TestingPlatformCommandLineArguments>";
                Assert.Contains(anchor, text);
                File.WriteAllText(csproj, text.Replace(anchor, " --ignore-exit-code 2" + anchor, StringComparison.Ordinal));
            }),
        };

        foreach (var (name, apply) in mutants)
        {
            var copy = await WorkingTreeCopyAsync();
            try
            {
                apply(copy);
                var result = await ProcessRunner.RunAsync(
                    Root,
                    "dotnet",
                    ["run", "ci/assert-test-step-fails.cs", "--root", copy],
                    timeout: TimeSpan.FromMinutes(10));

                Assert.True(result.ExitCode != 0, $"the canary passed with {name}:\n{result.Output}");
                Assert.True(
                    result.Output.Contains("the test step exited 0", StringComparison.Ordinal),
                    $"the canary failed with {name}, but not because the step exited 0:\n{result.Output}");
            }
            finally
            {
                DeleteTree(copy);
            }
        }
    }

    /// <summary>
    /// The canary script's decision table, one asserted case per row, where it is decided:
    /// only exit 2 with the canary reported passes. A script simplified to "non-zero"
    /// would let a crashed host or an aborted session pass for a failing test.
    /// </summary>
    [Fact]
    public async Task TestStepCanaryDecidesEveryExitCode()
    {
        var rows = new (int ExitCode, bool CanaryReported, bool Passes, string Reason)[]
        {
            (2, true, true, "ok: the test step exits 2"),
            (0, true, false, "the test step exited 0"),
            (0, false, false, "the canary did not run"),
            (8, false, false, "the canary did not run"),
            (2, false, false, "the canary was not reported"),
            (1, true, false, "expected exit 2 (a test failed), got 1"),
            (3, true, false, "expected exit 2 (a test failed), got 3"),
        };

        foreach (var (exitCode, canaryReported, passes, reason) in rows)
        {
            var result = await ProcessRunner.RunAsync(
                Root,
                "dotnet",
                ["run", "ci/assert-test-step-fails.cs", "--decide", $"{exitCode}", canaryReported ? "true" : "false"]);

            Assert.True(
                (result.ExitCode == 0) == passes,
                $"exit {exitCode} with the canary {(canaryReported ? "reported" : "missing")} should {(passes ? "pass" : "fail")}:\n{result.Output}");
            Assert.True(
                result.Output.Contains(reason, StringComparison.Ordinal),
                $"exit {exitCode} with the canary {(canaryReported ? "reported" : "missing")} was not decided for `{reason}`:\n{result.Output}");
        }
    }

    // ---- C83, and C38's second proof: a test that does not run ------------------

    /// <summary>
    /// `--fail-skips on` does not reach Explicit tests, which the runner counts as not
    /// run: a check's test marked Explicit leaves its proof at exit 0 having run nothing.
    /// So Explicit is kept to the one test that needs it, the C82 canary.
    /// </summary>
    [Fact]
    public void OnlyTheCanaryIsExplicit()
    {
        var tests = Path.Combine(Root, "tests", "Api.Tests");
        var explicitFiles = Directory.GetFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => ExplicitTrue().Matches(File.ReadAllText(path)).Select(_ => Path.GetRelativePath(tests, path)))
            .ToList();

        Assert.Equal(["CanaryTests.cs"], explicitFiles);
    }

    [GeneratedRegex(@"\bExplicit\s*=\s*true\b")]
    private static partial Regex ExplicitTrue();

    /// <summary>
    /// C38 proves a filter matching nothing fails; this proves a matching test left with
    /// `Skip` fails its proof too, on a copy of the tree, through the proof's own command.
    /// Without `--fail-skips on` in the test project, it exits 0 having run nothing.
    /// </summary>
    [Fact]
    public async Task NamedTestThatDoesNotRunFailsItsProof()
    {
        var copy = await WorkingTreeCopyAsync();
        try
        {
            var file = Path.Combine(copy, "tests", "Api.Tests", "CiTests.cs");
            var text = File.ReadAllText(file);
            const string fact = "    [Fact]\n    public void CiWorkflowNeedsNoCloudSubscription()";
            var normalized = text.ReplaceLineEndings("\n");
            Assert.Contains(fact, normalized);
            File.WriteAllText(file, normalized.Replace(
                fact,
                "    [Fact(Skip = \"left behind by accident\")]\n    public void CiWorkflowNeedsNoCloudSubscription()",
                StringComparison.Ordinal));

            var result = await ProcessRunner.RunAsync(
                copy,
                "dotnet",
                ["test", "--project", "tests/Api.Tests/Api.Tests.csproj", "--", "--filter-method", "*CiWorkflowNeedsNoCloudSubscription"],
                timeout: TimeSpan.FromMinutes(10));

            Assert.True(result.ExitCode == 2, $"the proof of a skipped test exited {result.ExitCode}, not 2 (a test failed):\n{result.Output}");
        }
        finally
        {
            DeleteTree(copy);
        }
    }

    /// <summary>
    /// The working tree as git sees it - tracked and untracked-but-not-ignored files - so
    /// nothing built here (bin/, obj/) reaches the copy and every build in it is fresh.
    /// </summary>
    private static async Task<string> WorkingTreeCopyAsync()
    {
        var listed = await ProcessRunner.RunAsync(Root, "git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"]);
        Assert.True(listed.ExitCode == 0, $"git ls-files failed:\n{listed.Output}");

        var copy = TempDirectory();
        foreach (var relative in listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var source = Path.Combine(Root, relative);
            if (!File.Exists(source))
            {
                continue;
            }

            var target = Path.Combine(copy, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }

        return copy;
    }

    /// <summary>Every way the workflow's runs differ from the listed commands; empty when they match.</summary>
    private static List<string> PinnedRunViolations(string workflow)
    {
        var violations = new List<string>();
        var yaml = new YamlStream();
        yaml.Load(new StringReader(workflow));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;

        var runs = new List<string>();
        foreach (var (jobName, jobNode) in ((YamlMappingNode)root.Children[new YamlScalarNode("jobs")]).Children)
        {
            foreach (var step in ((YamlSequenceNode)((YamlMappingNode)jobNode).Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>())
            {
                if (!step.Children.TryGetValue(new YamlScalarNode("run"), out var runNode))
                {
                    continue;
                }

                var run = ((YamlScalarNode)runNode).Value ?? string.Empty;
                runs.Add(run);

                if (!step.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode)
                    || string.IsNullOrWhiteSpace(((YamlScalarNode)nameNode).Value))
                {
                    violations.Add($"the step running `{run}` in job {jobName} has no name");
                }
            }
        }

        foreach (var run in runs.Where(run => !ListedRuns.Contains(run, StringComparer.Ordinal)))
        {
            violations.Add($"run `{run}` is not one of the listed commands");
        }

        foreach (var listed in ListedRuns.Where(listed => runs.Count(run => run == listed) != 1))
        {
            violations.Add($"`{listed}` is missing from ci.yml or appears more than once");
        }

        return violations;
    }

    [Fact]
    public async Task CachedGitleaksIsReplacedFromTheVerifiedArchive()
    {
        var cache = TempDirectory();
        var repo = await RepoWithCommittedFileAsync("notes.txt", "nothing secret here" + Environment.NewLine);
        var environment = new Dictionary<string, string?> { ["GITLEAKS_CACHE_DIR"] = cache };

        try
        {
            // A first run fills the cache with the verified archive and its binary.
            var first = await ScanAsync(repo, "HEAD~1..HEAD", environment);
            Assert.True(first.ExitCode == 0, $"the first scan did not pass: {first.Output}");

            // Then the binary is replaced in place, as anyone with access to the temp
            // directory could. The archive stays genuine, so its checksum still matches.
            var binary = Directory
                .GetFiles(cache, OperatingSystem.IsWindows() ? "gitleaks.exe" : "gitleaks", SearchOption.AllDirectories)
                .Single();
            await File.WriteAllTextAsync(binary, "not the real gitleaks", Ct);

            // Had the swapped file run, it would not execute and the scan would exit 2.
            // It passes only because the binary is extracted again from the verified
            // archive before running.
            var second = await ScanAsync(repo, "HEAD~1..HEAD", environment);
            Assert.True(second.ExitCode == 0, $"the swapped binary was not replaced: {second.Output}");
            Assert.Contains("found no secret", second.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(repo);
            DeleteTree(cache);
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
