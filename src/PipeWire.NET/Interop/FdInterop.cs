using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PipeWire.NET.Interop;

/// <summary>
/// Descriptor plumbing for starting a context over an already-connected socket fd.
/// </summary>
/// <remarks>
/// This is the xdg-desktop-portal ScreenCast <c>OpenPipeWireRemote</c> shape: the portal hands out
/// a fd that is already connected to the daemon, and the client connects over it without ever
/// seeing the daemon socket itself. The library duplicates such a descriptor before handing it to
/// <c>pw_context_connect_fd</c>. The duplication is the one libc call here, because no managed API
/// exists for it - the runtime keeps its own fcntl wrappers internal and declined to expose one
/// (dotnet/runtime#46832; Kestrel P/Invokes fcntl for the same systemd-socket reason) - and the
/// duplicate's lifetime is a plain <see cref="SafeFileHandle"/>: disposal closes it, and PipeWire
/// adopting it on a successful connect is expressed with
/// <see cref="SafeHandle.SetHandleAsInvalid"/> rather than a second close path.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static partial class FdInterop
{
    /// <summary><c>F_DUPFD_CLOEXEC</c> from <c>fcntl.h</c>: duplicate with close-on-exec set.</summary>
    private const int FDupfdCloexec = 1030;

    /// <summary>The lowest descriptor a duplication may return, just above stdio.</summary>
    private const int LowestDuplicate = 3;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int fd, int cmd, int arg);

    /// <summary>Duplicates <paramref name="fd"/> with the close-on-exec flag set on the duplicate.</summary>
    /// <param name="fd">The descriptor to duplicate. Must stay open for the duration of the call.</param>
    /// <returns>
    /// The duplicate, wrapped in an owning <see cref="SafeFileHandle"/> from the moment of return:
    /// disposal closes it, and once the connect adopts it into PipeWire,
    /// <see cref="SafeHandle.SetHandleAsInvalid"/> makes that disposal inert.
    /// </returns>
    /// <exception cref="PipeWireException">The duplication failed; the result is the negated errno.</exception>
    /// <remarks>
    /// <para>
    /// The close-on-exec flag is the point of the duplication, not an extra: a descriptor that
    /// crossed a portal boundary must not leak into child processes this one spawns afterwards.
    /// It mirrors PipeWire's own <c>impl_steal_fd</c> in module-protocol-native.c, which
    /// duplicates with <c>F_DUPFD_CLOEXEC</c> before handing a connection's fd back out.
    /// </para>
    /// <para>
    /// The floor of 3 keeps stdio (0-2) out of the returned range, so a duplicate can never
    /// land on a number a library or a spawned child assumes it may take over itself.
    /// </para>
    /// </remarks>
    internal static SafeFileHandle DuplicateWithCloseOnExec(int fd)
    {
        int duplicate = fcntl(fd, FDupfdCloexec, LowestDuplicate);
        if (duplicate < 0)
            throw new PipeWireException("fcntl(F_DUPFD_CLOEXEC)", -Marshal.GetLastPInvokeError());

        return new SafeFileHandle(duplicate, ownsHandle: true);
    }
}
