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

            Document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
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

/// <summary>S5: the OpenAPI contract is generated and committed (C39-C42).</summary>
/// <remarks>
/// One class, so its tests run one after another: C41 edits the real contract file
/// and restores it, and nothing else may read that file in the meantime.
/// </remarks>
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

    private static Task<ProcessRunner.Result> CheckContractAsync() =>
        ProcessRunner.RunAsync(ProcessRunner.RepoRoot, "dotnet", ["ci/check-contract.cs"]);
}
