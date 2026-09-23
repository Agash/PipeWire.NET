using PipeWire.NET.Interop;

namespace PipeWire.NET.Media;

/// <summary>The outcome of a timeline wait, and the errno when it did not succeed.</summary>
internal readonly record struct SyncWait(SyncWaitOutcome Outcome, int Errno)
{
    /// <summary>Whether the point was reached.</summary>
    public bool Reached => Outcome == SyncWaitOutcome.Reached;
}
