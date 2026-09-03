namespace PipeWire.NET.Media;

/// <summary>A DMA-BUF frame's explicit synchronisation points.</summary>
/// <remarks>
/// Explicit sync replaces the implicit fences a driver would otherwise attach to a buffer. The
/// producer names a point on an acquire timeline that it will signal once the data is written, and a
/// point on a release timeline that the consumer signals once it has stopped reading. Neither is a
/// descriptor: the two timeline descriptors arrive as extra entries in the buffer's data array,
/// which is why asking for this meta changes the buffer layout.
/// </remarks>
/// <param name="Flags">
/// Producer flags. <see cref="UnscheduledRelease"/> set means the producer has not scheduled the
/// release point and the consumer is expected to clear it by promising to signal.
/// </param>
/// <param name="AcquirePoint">The point to wait for before reading the frame.</param>
/// <param name="ReleasePoint">The point to signal once the frame is no longer being read.</param>
public readonly record struct VideoSyncTimeline(uint Flags, ulong AcquirePoint, ulong ReleasePoint)
{
    /// <summary>The producer has not scheduled the release point.</summary>
    public const uint UnscheduledRelease = 1 << 0;

    /// <summary>True when the producer has not scheduled the release point.</summary>
    public bool ReleaseIsUnscheduled => (Flags & UnscheduledRelease) != 0;
}
