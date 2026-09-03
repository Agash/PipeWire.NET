using System.Runtime.Versioning;
using PipeWire.NET.Interop;

namespace PipeWire.NET;

/// <summary>How much PipeWire's own library logging says.</summary>
/// <remarks>
/// These are the levels the native library uses, not the ones <c>ILogger</c> uses. They control
/// what libpipewire writes about itself, which is separate from what this library logs.
/// </remarks>
public enum PipeWireLogLevel
{
    /// <summary>Nothing at all.</summary>
    None = 0,

    /// <summary>Failures only.</summary>
    Error = 1,

    /// <summary>Failures and things that will probably become failures.</summary>
    Warn = 2,

    /// <summary>Lifecycle: connections, formats, the shape of the session.</summary>
    Info = 3,

    /// <summary>Enough to follow what the library is doing.</summary>
    Debug = 4,

    /// <summary>Everything, including per-buffer activity on the realtime path.</summary>
    Trace = 5,
}

/// <summary>Controls the native library's own logging.</summary>
/// <remarks>
/// <para>
/// libpipewire writes its own diagnostics to stderr, on its own schedule, whatever this library
/// does with <c>ILogger</c>. When a negotiation fails or a daemon refuses something, the reason is
/// often only in that output, so being able to turn it up from code is the difference between
/// diagnosing a field report and asking the user to set an environment variable and try again.
/// </para>
/// <para>
/// Redirecting it into an <c>ILogger</c> rather than merely levelling it is not offered, and cannot
/// be from C# alone: <c>pw_log_set</c> takes an <c>spa_log</c> whose vtable is a C variadic
/// function, plus a <c>va_list</c> variant. Neither is expressible as a reverse P/Invoke, so it
/// would take a native shim. The environment variable <c>PIPEWIRE_DEBUG</c> does the same job from
/// outside the process.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public static class PipeWireLog
{
    /// <summary>Sets how much the native library logs to stderr.</summary>
    /// <param name="level">The level to set.</param>
    /// <remarks>
    /// Process-wide, because the native library's log level is: every context in this process is
    /// affected, and so is anything else in it that uses libpipewire. It can be called before any
    /// context exists.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a defined level.</exception>
    public static void SetLevel(PipeWireLogLevel level)
    {
        // Bounds, not Enum.IsDefined: that one is reflection and does not survive trimming, which
        // this library claims to support. The values are contiguous, so the bounds are the
        // definition.
        if (level is < PipeWireLogLevel.None or > PipeWireLogLevel.Trace)
            throw new ArgumentOutOfRangeException(nameof(level));

        Native.pw_log_set_level((int)level);
    }
}
