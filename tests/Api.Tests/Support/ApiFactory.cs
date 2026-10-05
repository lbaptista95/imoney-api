using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Tests.Support;

/// <summary>
/// Assembles the real application, so startup behaviour is observed rather than
/// re-described: the migration gate, the health check and the configuration guards
/// are all properties of this assembly, not of the test.
/// </summary>
public sealed class ApiFactory(
    string connectionString,
    string environment = "Development",
    string? sharedToken = "test-token",
    IDictionary<string, string?>? extraSettings = null)
    : WebApplicationFactory<Program>
{
    private readonly List<string> _logLines = [];

    /// <summary>Every log line the application emitted, captured down to Trace for C37.</summary>
    public IReadOnlyList<string> LogLines
    {
        get
        {
            lock (_logLines)
            {
                return _logLines.ToList();
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
            };

            if (sharedToken is not null)
            {
                settings["Auth:SharedToken"] = sharedToken;
            }

            foreach (var (key, value) in extraSettings ?? new Dictionary<string, string?>())
            {
                settings[key] = value;
            }

            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureLogging(logging =>
        {
            // SetMinimumLevel alone is not enough: appsettings.json carries a
            // `Logging:LogLevel:Default` rule, and a configured rule overrides the
            // minimum level, so the capture silently stopped at Information. A
            // provider-specific rule outranks it. C37 found this by letting an
            // amount logged at Debug through.
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter<CapturingLoggerProvider>(category: null, level: LogLevel.Trace);
            logging.AddProvider(new CapturingLoggerProvider(line =>
            {
                lock (_logLines)
                {
                    _logLines.Add(line);
                }
            }));
        });
    }
}
