// Hand-maintained half of the generated `NativeLibdrm` partial class: the DRM syncobj timeline
// calls from libdrm that back explicit sync.
//
// Hand-written for the reason NativeLibc.Imports.cs gives, which applies here unchanged: errno is
// load-bearing (a timeline wait that returns -1 with ETIME timed out; any other errno is a real
// failure worth naming), and under DisableRuntimeMarshalling [LibraryImport] is the only form that
// captures errno. ClangSharp cannot emit it, and a P/Invoke's attributes live on its one declaration,
// so the hand-written half owns the whole declaration. The flag values these calls take
// (DRM_SYNCOBJ_*, DRM_CAP_SYNCOBJ_TIMELINE) and the errno values they are read against (ETIME, EINTR)
// are generated, by generate/libdrm.rsp, into the other half of this class.
//
// Signatures are libdrm's xf86drm.h, verbatim in meaning: int fd is the DRM device, handles are
// uint32_t, points uint64_t, and timeout_nsec is an absolute CLOCK_MONOTONIC deadline.
//
// Resolved to libdrm.so.2 (then libdrm.so) by the soname-first resolver in AssemblyInfo.cs.

#pragma warning disable CA1707 // Identifiers should not contain underscores (matches generated style)

using System.Runtime.InteropServices;

namespace PipeWire.NET.Interop;

internal static unsafe partial class NativeLibdrm
{
    /// <summary><c>drmGetCap</c>: asks a device's driver whether it has a capability, such as
    /// <c>DRM_CAP_SYNCOBJ_TIMELINE</c>.</summary>
    [LibraryImport("libdrm", EntryPoint = "drmGetCap", SetLastError = true)]
    internal static partial int drmGetCap(int fd, ulong capability, ulong* value);

    /// <summary><c>drmSyncobjCreate</c>: a new timeline on <paramref name="fd"/>.</summary>
    [LibraryImport("libdrm", EntryPoint = "drmSyncobjCreate", SetLastError = true)]
    internal static partial int drmSyncobjCreate(int fd, uint flags, uint* handle);

    /// <summary><c>drmSyncobjDestroy</c>. No <c>SetLastError</c>: a teardown path, where the errno is
    /// not actionable, for the same reason <c>close</c> has none.</summary>
    [LibraryImport("libdrm", EntryPoint = "drmSyncobjDestroy")]
    internal static partial int drmSyncobjDestroy(int fd, uint handle);

    /// <summary><c>drmSyncobjHandleToFD</c>: exports a handle as a descriptor for another process.</summary>
    [LibraryImport("libdrm", EntryPoint = "drmSyncobjHandleToFD", SetLastError = true)]
    internal static partial int drmSyncobjHandleToFD(int fd, uint handle, int* objFd);

    /// <summary><c>drmSyncobjFDToHandle</c>: imports a syncobj descriptor as a handle on <paramref name="fd"/>.</summary>
    [LibraryImport("libdrm", EntryPoint = "drmSyncobjFDToHandle", SetLastError = true)]
    internal static partial int drmSyncobjFDToHandle(int fd, int objFd, uint* handle);

    /// <summary><c>drmSyncobjTimelineSignal</c>: signals points on timelines from the CPU.</summary>
    [LibraryImport("libdrm", EntryPoint = "drmSyncobjTimelineSignal", SetLastError = true)]
    internal static partial int drmSyncobjTimelineSignal(
        int fd,
        uint* handles,
        ulong* points,
        uint handleCount
    );

    /// <summary><c>drmSyncobjTimelineWait</c>: waits for points, up to an absolute deadline.</summary>
    [LibraryImport("libdrm", EntryPoint = "drmSyncobjTimelineWait", SetLastError = true)]
    internal static partial int drmSyncobjTimelineWait(
        int fd,
        uint* handles,
        ulong* points,
        uint numHandles,
        long timeoutNsec,
        uint flags,
        uint* firstSignaled
    );
}
