using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PipeWire.NET.Tests;

/// <summary>
/// Declares that a test provokes library errors on purpose, and which ones.
/// </summary>
/// <remarks>
/// The substring is matched against the logged line. Anything a test logs at Error that it has not
/// declared fails it: a fault swallowed into a log entry otherwise passes every assertion while
/// doing nothing, which is how a parser that dropped every report it was given stayed green.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
internal sealed class ExpectsLibraryErrorAttribute(string substring) : Attribute
{
    public string Substring { get; } = substring;
}

/// <summary>
/// Base for tests that touch the library, failing any that make it log an unexpected error.
/// </summary>
/// <remarks>
/// The collection is static rather than per-thread: the library logs from the loop thread, so a
/// scope tied to the thread running the test would never see the entries that matter. Tests in
/// this assembly run sequentially, which is what makes one shared collection correct.
/// </remarks>
public abstract class PipeWireTestBase
{
    /// <summary>Set by MSTest; names the test being run.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Where an ordered record of the run goes, when one is asked for.
    /// </summary>
    /// <remarks>
    /// Set PWNET_TEST_TRACE to a path and every test appends its name and outcome as it finishes.
    /// The runner only names a test in its progress output once the test is slow enough to be worth
    /// mentioning, so there is otherwise no way to say what ran immediately before a failure - which
    /// is the whole question when one test leaves the session unfit for the next.
    /// </remarks>
    private static readonly string? TracePath =
        Environment.GetEnvironmentVariable("PWNET_TEST_TRACE");

    private static readonly Lock TraceGate = new();

    private long _startedTicks;

    [TestInitialize]
    public void BeginLibraryErrorWatch()
    {
        _startedTicks = Environment.TickCount64;
        ConsoleTestLoggerFactory.StartCollectingErrors();
    }

    private void Trace(string outcome)
    {
        if (TracePath is null) return;

        // Best effort by design: a trace that throws would fail tests that are otherwise fine, and
        // this exists to explain failures rather than to cause them.
        try
        {
            lock (TraceGate)
            {
                File.AppendAllText(TracePath,
                    $"{DateTime.Now:HH:mm:ss.fff} {Environment.TickCount64 - _startedTicks,6}ms "
                    + $"{outcome,-7} {TestContext?.TestName}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [TestCleanup]
    public void EndLibraryErrorWatch()
    {
        IReadOnlyList<string> seen = ConsoleTestLoggerFactory.StopCollectingErrors();
        Trace(TestContext?.CurrentTestOutcome.ToString() ?? "?");
        if (seen.Count == 0) return;

        string[] allowed = [.. Expected()];
        List<string> unexpected =
            [.. seen.Where(line => !allowed.Any(a => line.Contains(a, StringComparison.Ordinal)))];

        if (unexpected.Count > 0)
        {
            throw new AssertFailedException(
                $"the library logged {unexpected.Count} error(s) this test did not declare. "
                + $"Add [ExpectsLibraryError(\"...\")] if they are the point of the test:"
                + Environment.NewLine + string.Join(Environment.NewLine, unexpected.Distinct()));
        }
    }

    private IEnumerable<string> Expected()
    {
        Type type = GetType();
        foreach (ExpectsLibraryErrorAttribute a in
                 type.GetCustomAttributes(typeof(ExpectsLibraryErrorAttribute), inherit: true)
                     .Cast<ExpectsLibraryErrorAttribute>())
        {
            yield return a.Substring;
        }

        // The name MSTest reports carries the data-row suffix for parameterised tests, so the
        // method is matched on the part before it.
        string name = TestContext?.TestName ?? string.Empty;
        int paren = name.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0) name = name[..paren];

        foreach (System.Reflection.MethodInfo m in type.GetMethods())
        {
            if (!string.Equals(m.Name, name, StringComparison.Ordinal)) continue;
            foreach (ExpectsLibraryErrorAttribute a in
                     m.GetCustomAttributes(typeof(ExpectsLibraryErrorAttribute), inherit: true)
                      .Cast<ExpectsLibraryErrorAttribute>())
            {
                yield return a.Substring;
            }
        }
    }
}
