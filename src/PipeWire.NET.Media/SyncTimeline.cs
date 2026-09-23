using PipeWire.NET.Interop;

namespace PipeWire.NET.Media;

/// <summary>
/// Waiting on a timeline descriptor whatever kind it is, and never mistaking a failure for success.
/// </summary>
/// <remarks>
/// <para>
/// <c>SPA_DATA_SyncObj</c> is "a syncobj" (spa/buffer/buffer.h), and a real producer - a
/// compositor, gamescope - passes a DRM syncobj timeline. Upstream's own examples pass an eventfd
/// there instead ("just an example, not really syncobj here"), and this library still reads those,
/// so a descriptor is classified before it is used: a syncobj if it imports, an eventfd if the
/// kernel says it is one, and otherwise neither.
/// </para>
/// <para>
/// Neither is a failure, not a fallback. The earlier rule - anything that did not import was
/// treated as an eventfd - turned a syncobj this process could not import into an eight-byte read
/// that failed with EINVAL, a failure that was then discarded and reported as the point being
/// reached. The acquire point is when the data may be read, so that handed out frames the GPU might
/// still have been writing.
/// </para>
/// </remarks>
internal static class SyncTimeline
{
    /// <summary>Classifies a descriptor, importing it when it is a syncobj.</summary>
    /// <param name="fd">The descriptor.</param>
    /// <param name="handle">The imported handle, for a syncobj; the caller destroys it.</param>
    /// <param name="errno">Why a descriptor that is neither kind did not import.</param>
    internal static SyncTimelineKind Classify(int fd, out uint handle, out int errno)
    {
        handle = DrmSyncobj.Import(fd, out errno);
        if (handle != 0)
            return SyncTimelineKind.Syncobj;

        if (Descriptors.IsEventfd(fd))
        {
            errno = 0;
            return SyncTimelineKind.Eventfd;
        }

        return SyncTimelineKind.Unknown;
    }

    /// <summary>Waits for <paramref name="point"/> on whatever timeline <paramref name="fd"/> is.</summary>
    /// <remarks>
    /// For an eventfd the point is not a value - upstream's convention is one count per frame - so
    /// the wait is for the next count. A descriptor of neither kind fails with the import's errno
    /// (<c>ENODEV</c> when there is no render node that does timeline syncobjs).
    /// </remarks>
    internal static SyncWait Wait(int fd, ulong point, TimeSpan timeout)
    {
        switch (Classify(fd, out uint handle, out int errno))
        {
            case SyncTimelineKind.Syncobj:
                try
                {
                    return DrmSyncobj.Wait(handle, point, timeout);
                }
                finally
                {
                    DrmSyncobj.Destroy(handle);
                }

            case SyncTimelineKind.Eventfd:
                return Descriptors.WaitEventfd(fd, timeout);

            default:
                return new SyncWait(SyncWaitOutcome.Failed, errno != 0 ? errno : NativeLibc.EINVAL);
        }
    }
}
