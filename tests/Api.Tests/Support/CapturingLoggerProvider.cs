using Microsoft.Extensions.Logging;

namespace Api.Tests.Support;

/// <summary>
/// Captures the application's log output so a test can assert on what was written.
/// The constitution bars amounts and descriptions from any log line, and that is a
/// negative claim: it needs the real lines, at every level, to mean anything.
/// </summary>
public sealed class CapturingLoggerProvider(Action<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, Action<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = $"{logLevel} {category} {formatter(state, exception)}";
            if (exception is not null)
            {
                line += $" {exception}";
            }

            sink(line);
        }
    }
}
