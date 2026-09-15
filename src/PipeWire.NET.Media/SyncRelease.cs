namespace PipeWire.NET.Media;

/// <summary>
/// A release point a consumer has promised to signal and not yet signalled.
/// </summary>
/// <remarks>
/// <para>
/// Explicit sync separates two things a buffer used to carry together: the buffer goes back to the
/// producer at the end of the cycle as always, but its contents stay the consumer's until the release
/// point is signalled, and the producer waits for that before writing into it again. So a consumer
/// that keeps a frame past its cycle - a GPU consumer that imports the DMA-BUF and submits later -
/// holds one of these, and signals it when it has finished reading, which is what upstream's
/// video-play-sync does after it has rendered.
/// </para>
/// <para>
/// Normally a DRM syncobj point. A descriptor the kernel confirms is an eventfd - the stand-in
/// upstream's own video-src-sync example uses - is signalled with a write, as that example expects.
/// A descriptor that is neither yields no promise at all: the consumer then leaves
/// <c>UNSCHEDULED_RELEASE</c> set, so the producer does not wait for a release nobody can send.
/// </para>
/// <para>
/// A value type so that retaining a borrowed frame stays allocation-free. It is moved between slots
/// rather than copied, and whoever holds it signals it exactly once.
/// </para>
/// </remarks>
internal readonly struct SyncRelease
{
    // 0 is "nothing promised", which is what default(SyncRelease) must mean.
    private const byte None = 0, Syncobj = 1, Eventfd = 2;

    private readonly byte _kind;
    private readonly uint _handle;
    private readonly int _eventFd;
    private readonly ulong _point;

    private SyncRelease(byte kind, uint handle, int eventFd, ulong point)
    {
        _kind = kind;
        _handle = handle;
        _eventFd = eventFd;
        _point = point;
    }

    /// <summary>Whether there is a release still to signal.</summary>
    internal bool IsPending => _kind != None;

    /// <summary>
    /// A promise to signal <paramref name="point"/> on the timeline behind <paramref name="releaseFd"/>,
    /// or none when that descriptor is not a timeline this process can signal.
    /// </summary>
    internal static SyncRelease For(int releaseFd, ulong point)
    {
        if (releaseFd < 0 || point == 0) return default;

        return SyncTimeline.Classify(releaseFd, out uint handle, out _) switch
        {
            SyncTimelineKind.Syncobj => new SyncRelease(Syncobj, handle, -1, point),
            SyncTimelineKind.Eventfd => new SyncRelease(Eventfd, 0, releaseFd, point),
            _ => default,
        };
    }

    /// <summary>Signals the release and lets go of the handle. Call once, on a value that was moved out.</summary>
    /// <returns>
    /// False when the signal was refused. The producer is then left waiting until its own release
    /// timeout, which is where that is reported.
    /// </returns>
    internal bool Signal()
    {
        switch (_kind)
        {
            case Syncobj:
                bool signalled = DrmSyncobj.Signal(_handle, _point);
                DrmSyncobj.Destroy(_handle);
                return signalled;

            case Eventfd:
                return Descriptors.SignalEventfd(_eventFd) == 0;

            default:
                return true;
        }
    }
}
