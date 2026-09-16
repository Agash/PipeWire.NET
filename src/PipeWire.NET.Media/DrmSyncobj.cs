using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Media;

/// <summary>
/// DRM syncobj timelines: the descriptors behind PipeWire's explicit sync.
/// </summary>
/// <remarks>
/// <para>
/// <c>SPA_DATA_SyncObj</c> is "a syncobj" (spa/buffer/buffer.h) - a DRM syncobj timeline, which is
/// what compositor screencasts and gamescope hand over. A frame's acquire point is signalled on one
/// timeline when its contents are ready, and the consumer signals the release point on the other
/// when it has finished reading. Both are DRM ioctls on a device descriptor, reached here through
/// libdrm.
/// </para>
/// <para>
/// This replaced an eventfd emulation borrowed from upstream's video-src-sync example, whose own
/// comment calls it "just an example, not really syncobj here". The emulation only worked against a
/// producer that made the same substitution: against a real syncobj, reading eight bytes fails, so
/// the consumer went on without waiting for the acquire point, and the release it wrote was lost,
/// leaving any producer that had been promised one waiting forever.
/// </para>
/// <para>
/// Syncobj handles belong to a device descriptor, but a syncobj is a DRM-core object and its own
/// descriptor is not tied to the device that made it: it imports into the handle table of any
/// device whose driver supports syncobjs, and the fences on its timeline are <c>dma_fence</c>s,
/// waitable from any device. So one render node serves every timeline in the process - but it has
/// to be one whose driver supports timeline operations (<c>DRM_CAP_SYNCOBJ_TIMELINE</c>), which
/// is a per-driver capability. Taking the first node that opens gets that wrong on a machine
/// whose first node belongs to a driver without it, and every import there fails.
/// </para>
/// <para>
/// libdrm reports failure as -1 with errno set, and errno matters: a wait that returns
/// <c>ETIME</c> timed out, anything else is a real fault worth naming. The imports are the
/// hand-written <c>[LibraryImport(SetLastError = true)]</c> half of <c>NativeConstants</c>, the same
/// pattern as the libc ones, because that is the only form that captures errno here.
/// </para>
/// </remarks>
internal static unsafe class DrmSyncobj
{
    private static readonly Lazy<SafeFileHandle?> RenderNode = new(OpenRenderNode);

    /// <summary>Whether a DRM render node is available, and with it explicit sync.</summary>
    internal static bool IsAvailable => Device >= 0;

    private static int Device => RenderNode.Value is { IsInvalid: false } h ? (int)h.DangerousGetHandle() : -1;

    private static SafeFileHandle? OpenRenderNode()
    {
        // The render nodes are 128..191. The first whose driver does timeline syncobjs is taken;
        // which GPU that is does not matter, for the reason the class remarks give.
        for (int minor = 128; minor < 192; minor++)
        {
            string path = $"/dev/dri/renderD{minor}";
            if (!File.Exists(path)) continue;

            try
            {
                SafeFileHandle node = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
                if (SupportsTimelines(node)) return node;

                node.Dispose();
            }
            catch (IOException)
            {
                // Deliberately not logged: the next node is tried, and having none is reported by
                // IsAvailable being false where explicit sync would be negotiated.
            }
            catch (UnauthorizedAccessException)
            {
                // Deliberately not logged, for the same reason: a node this user cannot open is
                // skipped, not fatal.
            }
        }

        return null;
    }

    private static bool SupportsTimelines(SafeFileHandle node)
    {
        ulong value = 0;
        return NativeLibdrm.drmGetCap(
                   (int)node.DangerousGetHandle(), NativeLibdrm.DRM_CAP_SYNCOBJ_TIMELINE, &value) == 0
               && value != 0;
    }

    /// <summary>Imports a syncobj descriptor, returning its handle, or 0 when it does not import.</summary>
    /// <remarks>0 is never a valid handle, which is what makes it usable as the failure value.</remarks>
    internal static uint Import(int fd) => Import(fd, out _);

    /// <summary>Imports a syncobj descriptor, with the errno when it does not import.</summary>
    /// <param name="fd">The descriptor.</param>
    /// <param name="errno">Why it did not import: <c>ENODEV</c> when there is no usable render node.</param>
    internal static uint Import(int fd, out int errno)
    {
        errno = 0;
        if (fd < 0)
        {
            errno = NativeLibc.EBADF;
            return 0;
        }

        if (Device < 0)
        {
            errno = NativeLibc.ENODEV;
            return 0;
        }

        uint handle = 0;
        if (NativeLibdrm.drmSyncobjFDToHandle(Device, fd, &handle) == 0) return handle;

        errno = Marshal.GetLastPInvokeError();
        return 0;
    }

    /// <summary>Creates a timeline and exports it, for a producer that owns its own.</summary>
    /// <returns>The handle and its exported descriptor, or (0, -1) when no device is available.</returns>
    internal static (uint Handle, int Fd) Create()
    {
        if (Device < 0) return (0, -1);

        uint handle = 0;
        if (NativeLibdrm.drmSyncobjCreate(Device, 0, &handle) != 0) return (0, -1);

        int fd = -1;
        if (NativeLibdrm.drmSyncobjHandleToFD(Device, handle, &fd) != 0)
        {
            _ = NativeLibdrm.drmSyncobjDestroy(Device, handle);
            return (0, -1);
        }

        return (handle, fd);
    }

    /// <summary>Releases a handle. The descriptor it came from, if any, is closed separately.</summary>
    internal static void Destroy(uint handle)
    {
        if (handle != 0 && Device >= 0) _ = NativeLibdrm.drmSyncobjDestroy(Device, handle);
    }

    /// <summary>Signals a timeline point from the CPU.</summary>
    /// <returns>False when the signal was refused.</returns>
    internal static bool Signal(uint handle, ulong point)
    {
        if (handle == 0 || Device < 0) return false;
        return NativeLibdrm.drmSyncobjTimelineSignal(Device, &handle, &point, 1) == 0;
    }

    /// <summary>
    /// Waits until a timeline reaches <paramref name="point"/>, or until <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// <c>WAIT_FOR_SUBMIT</c> because the point may not have a fence attached yet: a producer that
    /// has not submitted its render does not make the wait fail, it makes it wait. The deadline is
    /// absolute CLOCK_MONOTONIC, which is what the ioctl takes; passing a duration there waits until
    /// the machine's uptime passes it, which is to say not at all.
    /// </remarks>
    internal static SyncWait Wait(uint handle, ulong point, TimeSpan timeout)
    {
        if (handle == 0 || Device < 0) return new SyncWait(SyncWaitOutcome.Failed, 0);

        long deadline = MonotonicNowNs() + (long)(timeout.TotalMilliseconds * 1_000_000);
        uint flags = NativeLibdrm.DRM_SYNCOBJ_WAIT_FLAGS_WAIT_FOR_SUBMIT;

        if (NativeLibdrm.drmSyncobjTimelineWait(Device, &handle, &point, 1, deadline, flags, null) == 0)
            return new SyncWait(SyncWaitOutcome.Reached, 0);

        int errno = Marshal.GetLastPInvokeError();
        return errno == NativeLibc.ETIME
            ? new SyncWait(SyncWaitOutcome.TimedOut, errno)
            : new SyncWait(SyncWaitOutcome.Failed, errno);
    }

    private static long MonotonicNowNs() =>
        (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000_000 / Stopwatch.Frequency);
}
