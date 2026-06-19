using Microsoft.Extensions.Logging;

namespace PipeWire.NET.Tests;

/// <summary>
/// A trivial <see cref="ILoggerFactory"/> that writes every level to <see cref="Console.Error"/>. MSTest
/// captures per-test console output and surfaces it on failure, so passing this to a
/// <see cref="PipeWireContext"/> makes a stuck negotiation (state transitions, format/buffer params,
/// underruns) visible in the failing test's output instead of being a silent timeout.
/// </summary>
internal sealed class ConsoleTestLoggerFactory : ILoggerFactory
{
    public static readonly ConsoleTestLoggerFactory Instance = new();

    public void AddProvider(ILoggerProvider provider) { }

    public ILogger CreateLogger(string categoryName) => new ConsoleTestLogger(categoryName);

    public void Dispose() { }

    private sealed class ConsoleTestLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Console.Error.WriteLine($"[{level}] {category}: {formatter(state, exception)}");
    }
}
