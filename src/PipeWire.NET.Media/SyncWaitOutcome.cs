using PipeWire.NET.Interop;

namespace PipeWire.NET.Media;

/// <summary>How a timeline wait ended.</summary>
internal enum SyncWaitOutcome
{
    /// <summary>The point was reached.</summary>
    Reached,

    /// <summary>The deadline passed first (<c>ETIME</c>).</summary>
    TimedOut,

    /// <summary>The wait itself failed; <see cref="SyncWait.Errno"/> says why.</summary>
    Failed,
}
