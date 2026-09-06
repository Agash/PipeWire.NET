using Microsoft.Extensions.Logging;

namespace PipeWire.NET.Tests;

/// <summary>
/// A trivial <see cref="ILoggerFactory"/> that writes to <see cref="Console.Error"/>. MSTest
/// captures per-test console output and surfaces it on failure, so passing this to a
/// <see cref="PipeWireContext"/> makes a stuck negotiation (state transitions, format/buffer params,
/// underruns) visible in the failing test's output instead of being a silent timeout.
/// </summary>
/// <remarks>
/// The minimum level comes from <c>PWNET_TEST_LOG_LEVEL</c> (a <see cref="LogLevel"/> name,
/// default <see cref="LogLevel.Trace"/>). CI sets <c>Information</c>: per-global registry traces
/// are the bulk of daemon-leg logs by an order of magnitude, and twenty megabytes of them crash
/// the web log renderer, while state transitions and warnings still show on failure.
/// </remarks>
internal sealed class ConsoleTestLoggerFactory : ILoggerFactory
{
    public static readonly ConsoleTestLoggerFactory Instance = new();

    private static readonly LogLevel MinimumLevel = ReadMinimumLevel();

    public void AddProvider(ILoggerProvider provider) { }

    public ILogger CreateLogger(string categoryName) => new ConsoleTestLogger(categoryName);

    public void Dispose() { }

    private static LogLevel ReadMinimumLevel() =>
        Enum.TryParse<LogLevel>(
            Environment.GetEnvironmentVariable("PWNET_TEST_LOG_LEVEL"),
            ignoreCase: true,
            out LogLevel level)
            ? level
            : LogLevel.Trace;

    private sealed class ConsoleTestLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinimumLevel;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Console.Error.WriteLine($"[{level}] {category}: {formatter(state, exception)}");
    }
}
