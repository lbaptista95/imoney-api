using System.Diagnostics;

namespace Api.Tests.Support;

/// <summary>
/// Runs the real API assembly as its own process. In-process would settle row counts
/// but not the claims that are about a process: its exit code, and what actually
/// reaches stdout and stderr.
/// </summary>
public static class SeedProcess
{
    public sealed record Result(int ExitCode, string Output, bool TimedOut = false);

    /// <summary>Runs the `seed` command against <paramref name="connectionString"/>.</summary>
    public static Task<Result> RunAsync(string connectionString) =>
        RunApiAsync(connectionString, ["seed"], TimeSpan.FromMinutes(2));

    /// <summary>
    /// Starts the API itself in Development. A process that should exit but does not
    /// is the failure here, so the timeout kills it and reports TimedOut instead of
    /// hanging the suite or leaving a server behind.
    /// </summary>
    public static Task<Result> BootAsync(string connectionString, TimeSpan timeout) =>
        RunApiAsync(connectionString, [], timeout);

    private static async Task<Result> RunApiAsync(string connectionString, string[] args, TimeSpan timeout)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "Api.dll");
        if (!File.Exists(assembly))
        {
            throw new FileNotFoundException(
                $"The API assembly is not next to the tests, so it cannot be run: {assembly}");
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assembly);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__Default"] = connectionString;
        startInfo.Environment["Auth__SharedToken"] = "process-run-token";

        // An ephemeral port, so a boot that wrongly succeeds cannot collide with
        // anything already listening.
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start the API process");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return new Result(process.ExitCode, await stdout + await stderr, TimedOut: true);
        }

        return new Result(process.ExitCode, await stdout + await stderr);
    }
}
