using System.Reflection;
using System.Text.RegularExpressions;
using Api.Tests.Support;
using Microsoft.Extensions.Configuration.UserSecrets;
using YamlDotNet.RepresentationModel;

namespace Api.Tests;

/// <summary>
/// S7: where the local secrets come from, and who can reach the local stack (C59-C61).
/// The first verification found the token and the database password committed in
/// compose.yml, both ports published on every interface, and no UserSecretsId - so the
/// `dotnet user-secrets` the plan names as the token's source could never be read (F7).
/// </summary>
public sealed partial class LocalSecretsTests
{
    private static readonly string Compose = Path.Combine(ProcessRunner.RepoRoot, "compose.yml");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ApiAssemblyDeclaresUserSecretsId()
    {
        // WebApplication.CreateBuilder adds user-secrets in Development only when the
        // entry assembly carries this attribute, generated from <UserSecretsId>.
        var attribute = typeof(Program).Assembly.GetCustomAttribute<UserSecretsIdAttribute>();

        Assert.NotNull(attribute);
        Assert.False(string.IsNullOrWhiteSpace(attribute!.UserSecretsId));
    }

    [Fact]
    public void ComposeCommitsNoSecretValue()
    {
        var environment = Services()
            .SelectMany(service => EnvironmentOf(service.Value))
            .ToList();
        Assert.NotEmpty(environment);

        foreach (var (name, value) in environment)
        {
            if (SecretName().IsMatch(name))
            {
                Assert.True(
                    RequiredVariable().IsMatch(value),
                    $"{name} commits a value instead of requiring one from the environment: {value}");
            }

            // A connection string carries its own password; that part must be
            // required from the environment too.
            if (value.Contains("Password=", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(
                    PasswordFromVariable().IsMatch(value),
                    $"{name} commits a password inside its connection string");
            }
        }
    }

    [Fact]
    public async Task ComposeRefusesToRenderWithoutSecrets()
    {
        // An empty env file replaces the default .env, so a developer's local values
        // cannot satisfy the requirement on this machine and hide the failure.
        var empty = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.env");
        await File.WriteAllTextAsync(empty, string.Empty, Ct);

        try
        {
            var withoutSecrets = await ComposeConfigAsync(empty, new Dictionary<string, string?>
            {
                ["IMONEY_SHARED_TOKEN"] = null,
                ["IMONEY_DB_PASSWORD"] = null,
            });
            Assert.NotEqual(0, withoutSecrets.ExitCode);

            // The positive control: the same file renders once the values exist, so
            // the failure above is the missing secrets and not a broken compose file.
            var withSecrets = await ComposeConfigAsync(empty, new Dictionary<string, string?>
            {
                ["IMONEY_SHARED_TOKEN"] = "control-token",
                ["IMONEY_DB_PASSWORD"] = "control-password",
            });
            Assert.True(withSecrets.ExitCode == 0, $"compose config failed with every value present:\n{withSecrets.Output}");
        }
        finally
        {
            File.Delete(empty);
        }
    }

    [Fact]
    public void ComposePublishesPortsOnLoopbackOnly()
    {
        var ports = Services()
            .Where(service => service.Value.Children.ContainsKey(new YamlScalarNode("ports")))
            .SelectMany(service => ((YamlSequenceNode)service.Value.Children[new YamlScalarNode("ports")])
                .Select(port => (Service: service.Key, Port: ((YamlScalarNode)port).Value!)))
            .ToList();
        Assert.NotEmpty(ports);

        foreach (var (service, port) in ports)
        {
            Assert.True(
                port.StartsWith("127.0.0.1:", StringComparison.Ordinal),
                $"{service} publishes '{port}' beyond this machine");
        }
    }

    private static IEnumerable<KeyValuePair<string, YamlMappingNode>> Services()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Compose)));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;

        return ((YamlMappingNode)root.Children[new YamlScalarNode("services")])
            .Children
            .Select(entry => new KeyValuePair<string, YamlMappingNode>(
                ((YamlScalarNode)entry.Key).Value!,
                (YamlMappingNode)entry.Value));
    }

    private static IEnumerable<(string Name, string Value)> EnvironmentOf(YamlMappingNode service) =>
        service.Children.TryGetValue(new YamlScalarNode("environment"), out var environment)
            ? ((YamlMappingNode)environment).Children.Select(entry =>
                (((YamlScalarNode)entry.Key).Value!, ((YamlScalarNode)entry.Value).Value ?? string.Empty))
            : [];

    private static Task<ProcessRunner.Result> ComposeConfigAsync(string envFile, IDictionary<string, string?> environment) =>
        ProcessRunner.RunAsync(
            ProcessRunner.RepoRoot,
            "docker",
            ["compose", "--env-file", envFile, "-f", "compose.yml", "config", "--quiet"],
            environment: environment);

    [GeneratedRegex("PASSWORD|TOKEN|SECRET", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    /// <summary>The whole value is one required variable: `${NAME:?message}`.</summary>
    [GeneratedRegex(@"^\$\{[A-Z0-9_]+:\?[^}]*\}$")]
    private static partial Regex RequiredVariable();

    [GeneratedRegex(@"Password=\$\{[A-Z0-9_]+:\?[^}]*\}", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordFromVariable();
}
