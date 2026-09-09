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

    /// <summary>Errors logged while collection is on, from whichever thread logged them.</summary>
    /// <remarks>
    /// Static, not per-thread: the library logs from the loop thread, so a collection scoped to
    /// the thread running the test would never see the entries worth failing on. Tests in this
    /// assembly run sequentially, which is what makes one shared list correct.
    /// </remarks>
    private static readonly List<string> Errors = [];

    private static bool _collecting;

    /// <summary>Starts collecting library errors, discarding anything from before.</summary>
    public static void StartCollectingErrors()
    {
        lock (Errors)
        {
            Errors.Clear();
            _collecting = true;
        }
    }

    /// <summary>Stops collecting and returns what arrived.</summary>
    public static IReadOnlyList<string> StopCollectingErrors()
    {
        lock (Errors)
        {
            _collecting = false;
            return [.. Errors];
        }
    }

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
            Func<TState, Exception?, string> formatter)
        {
            string line = $"[{level}] {category}: {formatter(state, exception)}";
            if (level >= LogLevel.Error)
            {
                lock (Errors)
                {
                    if (_collecting) Errors.Add(line);
                }
            }

            Console.Error.WriteLine(line);
        }
    }
}
