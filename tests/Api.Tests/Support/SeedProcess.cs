using System.Diagnostics;

namespace Api.Tests.Support;

/// <summary>
/// Runs the real `seed` command as its own process. In-process would settle the row
/// counts but not the two claims that are about a process: the exit code AC 8 asks
/// for, and what actually reaches stdout and stderr.
/// </summary>
public static class SeedProcess
{
    public sealed record Result(int ExitCode, string Output);

    public static async Task<Result> RunAsync(string connectionString)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "Api.dll");
        if (!File.Exists(assembly))
        {
            throw new FileNotFoundException(
                $"The API assembly is not next to the tests, so the seed cannot be run: {assembly}");
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add("seed");
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__Default"] = connectionString;
        startInfo.Environment["Auth__SharedToken"] = "seed-run-token";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start the seed process");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);

        return new Result(process.ExitCode, await stdout + await stderr);
    }
}
