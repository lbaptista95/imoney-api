using System.Diagnostics;
using Api.Tests.Support;

namespace Api.Tests;

/// <summary>
/// C1: `docker compose up --wait` brings up the API and a PostgreSQL the API can
/// reach. This runs the real compose file rather than a copy of it, because the
/// claim is about that file: a copy would keep passing after the real one broke.
/// </summary>
public sealed class ComposeTests
{
    private static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "compose.yml")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new DirectoryNotFoundException("compose.yml not found above the test assembly");
        }
    }

    [Fact]
    public async Task ComposeBringsUpApiAndReachablePostgres()
    {
        // A project name of its own, so this never tears down a stack someone is
        // using for development.
        var project = $"imoney-c1-{Guid.NewGuid():N}"[..24];

        try
        {
            var up = await Docker(project, "up", "--wait", "--build");
            Assert.True(up.ExitCode == 0, $"compose up failed:\n{up.Output}");

            // --wait only returns 0 once every service with a healthcheck is healthy,
            // and the API's healthcheck is GET /health, which pings the database by
            // the compose service name. So this exit code is the reachability claim.
            var apiHealth = await Docker(project, "ps", "--format", "{{.Service}} {{.Health}}");
            Assert.Contains("api healthy", apiHealth.Output);
            Assert.Contains("db healthy", apiHealth.Output);

            // Asserted from inside the API container: it is the API's own view of the
            // database that matters, not the host's.
            var query = await Docker(
                project,
                "exec",
                "-T",
                "db",
                "psql",
                "-U",
                "imoney",
                "-d",
                "imoney",
                "-tAc",
                "SELECT to_regclass('public.accounts') IS NOT NULL");
            Assert.Equal(0, query.ExitCode);
            Assert.Contains("t", query.Output);
        }
        finally
        {
            await Docker(project, "down", "-v");
        }
    }

    private static async Task<SeedProcess.Result> Docker(string project, params string[] args)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("compose");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("compose.yml");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(project);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start docker compose");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await process.WaitForExitAsync(timeout.Token);

        return new SeedProcess.Result(process.ExitCode, await stdout + await stderr);
    }
}
