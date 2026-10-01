using System.Text.Json;
using Api.Tests.Support;

namespace Api.Tests;

/// <summary>
/// Generates the contract once, from a clean build, into a temporary file. C39 and
/// C40 inspect that output rather than the committed file: their claim is about
/// what a build generates, and C41/C42 separately prove the committed file matches.
/// </summary>
public sealed class GeneratedContractFixture : IAsyncLifetime
{
    public JsonDocument Document { get; private set; } = null!;

    /// <summary>The generated file exactly as written, for claims about its bytes.</summary>
    public string RawText { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openapi-fixture-{Guid.NewGuid():N}.json");
        try
        {
            var build = await ProcessRunner.RunAsync(
                ProcessRunner.RepoRoot,
                "dotnet",
                ["build", "src/Api/Api.csproj", "-nologo", "-v", "quiet", $"-p:ContractPath={path}"]);

            if (build.ExitCode != 0 || !File.Exists(path))
            {
                throw new InvalidOperationException($"the clean build did not generate a contract:\n{build.Output}");
            }

            RawText = await File.ReadAllTextAsync(path);
            Document = JsonDocument.Parse(RawText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    public ValueTask DisposeAsync()
    {
        Document?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Every test that reads or writes contracts/openapi.json, serialized. C41 edits the
/// real file and restores it, so no other test may read it in the meantime. Keeping
/// those tests in one class was not enough: CiTests, a different class, read the file
/// in parallel and failed intermittently while C41 had it tampered.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ContractFileCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "contracts/openapi.json";
}

/// <summary>S5: the OpenAPI contract is generated and committed (C39-C42).</summary>
[Collection(ContractFileCollection.Name)]
public sealed class ContractTests(GeneratedContractFixture contract) : IClassFixture<GeneratedContractFixture>
{
    private const string ContractPath = "contracts/openapi.json";

    /// <summary>The plan's `## Surface`: every route and the statuses it lists.</summary>
    private static readonly Dictionary<string, string[]> Surface = new()
    {
        ["/health"] = ["200", "503"],
        ["/v1/accounts"] = ["200", "401", "500"],
        ["/v1/transactions"] = ["200", "400", "401", "500"],
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void GeneratedContractDescribesExactlyTheThreeRoutes()
    {
        var paths = contract.Document.RootElement.GetProperty("paths")
            .EnumerateObject()
            .Select(p => p.Name)
            .Order()
            .ToList();

        // Equality, not containment: a fourth route nobody planned is as much a
        // breach of "exactly" as a missing one.
        Assert.Equal(Surface.Keys.Order(), paths);

        foreach (var path in paths)
        {
            var methods = contract.Document.RootElement.GetProperty("paths").GetProperty(path)
                .EnumerateObject()
                .Select(m => m.Name)
                .ToList();
            Assert.Equal(["get"], methods);
        }
    }

    [Fact]
    public void GeneratedContractDeclaresEveryDocumentedStatus()
    {
        foreach (var (path, statuses) in Surface)
        {
            var declared = contract.Document.RootElement
                .GetProperty("paths").GetProperty(path).GetProperty("get").GetProperty("responses")
                .EnumerateObject()
                .Select(r => r.Name)
                .Order()
                .ToList();

            Assert.True(
                statuses.Order().SequenceEqual(declared),
                $"{path} declares [{string.Join(", ", declared)}], the Surface lists [{string.Join(", ", statuses)}]");
        }
    }

    [Fact]
    public async Task ContractCheckFailsNamingStaleFile()
    {
        var contractFile = Path.Combine(ProcessRunner.RepoRoot, ContractPath);
        var original = await File.ReadAllBytesAsync(contractFile, Ct);

        try
        {
            // A hand edit that keeps the file valid JSON, so the failure is drift and
            // not a parse error.
            using var document = JsonDocument.Parse(original);
            var tampered = document.RootElement.GetRawText().Replace(
                "\"/v1/accounts\"",
                "\"/v1/accounts-renamed\"",
                StringComparison.Ordinal);
            Assert.NotEqual(document.RootElement.GetRawText(), tampered);
            await File.WriteAllTextAsync(contractFile, tampered, Ct);

            var result = await CheckContractAsync();

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(ContractPath, result.Output, StringComparison.Ordinal);
            Assert.Contains("out of date", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllBytesAsync(contractFile, original, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ContractCheckPassesWhenContractIsCurrent()
    {
        var result = await CheckContractAsync();

        Assert.True(result.ExitCode == 0, $"expected the committed contract to be current:\n{result.Output}");
    }

    [Fact]
    public void GeneratedContractHasNoCarriageReturn()
    {
        // Physical or escaped: the generator once joined XML comment lines with the
        // OS newline, so a Windows build wrote an escaped CR-LF inside a string and the
        // Linux CI check failed against it (F1 of the third verification).
        Assert.DoesNotContain('\r', contract.RawText);
        Assert.DoesNotContain("\\r", contract.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContractCheckPassesOnLinux()
    {
        // The CI runner and production are Linux; every other proof here runs on the
        // author's Windows. The tree is copied into the container without bin/, obj/
        // or .git, so nothing built on Windows leaks into the Linux build.
        const string script = "mkdir -p /work && cd /src && "
            + "tar --exclude=./src/Api/bin --exclude=./src/Api/obj "
            + "--exclude=./tests/Api.Tests/bin --exclude=./tests/Api.Tests/obj --exclude=./.git "
            + "-cf - . | tar -xf - -C /work && cd /work && dotnet ci/check-contract.cs";

        var result = await ProcessRunner.RunAsync(
            ProcessRunner.RepoRoot,
            "docker",
            [
                "run", "--rm",
                "--mount", $"type=bind,source={ProcessRunner.RepoRoot},target=/src,readonly",
                "mcr.microsoft.com/dotnet/sdk:10.0",
                "sh", "-c", script,
            ],
            timeout: TimeSpan.FromMinutes(10));

        Assert.True(result.ExitCode == 0, $"the contract check failed on Linux: {result.Output}");
    }

    private static Task<ProcessRunner.Result> CheckContractAsync() =>
        ProcessRunner.RunAsync(ProcessRunner.RepoRoot, "dotnet", ["ci/check-contract.cs"]);
}
