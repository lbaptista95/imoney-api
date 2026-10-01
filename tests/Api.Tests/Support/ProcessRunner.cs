using System.Diagnostics;

namespace Api.Tests.Support;

/// <summary>Runs a command to completion and returns its exit code and combined output.</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output);

    public static async Task<Result> RunAsync(
        string workingDirectory,
        string file,
        IEnumerable<string> args,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start {file}");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(cancel.Token);

        return new Result(process.ExitCode, await stdout + await stderr);
    }

    /// <summary>The repository root: the directory holding Api.slnx.</summary>
    public static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Api.slnx")))
                {
                    return dir.FullName;
                }
            }

            throw new DirectoryNotFoundException("Api.slnx not found above the test assembly");
        }
    }
}
