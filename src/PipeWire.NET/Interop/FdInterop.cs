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

    /// <summary><c>SOL_SOCKET</c> from <c>socket.h</c>: the socket-level option namespace.</summary>
    private const int SolSocket = 1;

    /// <summary><c>SO_ACCEPTCONN</c>: non-zero when the socket is listening.</summary>
    private const int SoAcceptConn = 30;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int getsockopt(int fd, int level, int optname, int* optval, int* optlen);

    /// <summary>Whether <paramref name="fd"/> is a socket that is listening for connections.</summary>
    /// <remarks>False for a non-socket (<c>ENOTSOCK</c>) as well as for a connected socket.</remarks>
    internal static unsafe bool IsListeningSocket(int fd)
    {
        int listening = 0;
        int size = sizeof(int);
        if (getsockopt(fd, SolSocket, SoAcceptConn, &listening, &size) < 0)
            return false;

        return listening != 0;
    }

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
            throw new PipeWireInteropException("fcntl(F_DUPFD_CLOEXEC)", -Marshal.GetLastPInvokeError());

        return new SafeFileHandle(duplicate, ownsHandle: true);
    }

    /// <summary>Runs <paramref name="use"/> on <paramref name="handle"/>'s descriptor.</summary>
    /// <remarks>
    /// The reference is what makes reading the descriptor and using it one step rather than two:
    /// a disposal in between would hand the syscall a number that is already closed, or reopened
    /// as something else.
    /// </remarks>
    internal static T Borrow<T>(SafeHandle handle, Func<int, T> use)
    {
        bool referenced = false;
        try
        {
            handle.DangerousAddRef(ref referenced);
            return use((int)handle.DangerousGetHandle());
        }
        finally
        {
            if (referenced) handle.DangerousRelease();
        }
    }

    /// <summary>
    /// Awaits <paramref name="use"/> on both descriptors, holding a reference to each until it
    /// completes.
    /// </summary>
    /// <remarks>
    /// Separate from the synchronous overload because a delegate returning a task returns at its
    /// first await, and releasing there would leave the descriptors unheld for the part of the
    /// operation that actually uses them.
    /// </remarks>
    internal static async Task BorrowAsync(SafeHandle first, SafeHandle second, Func<int, int, Task> use)
    {
        bool firstHeld = false;
        bool secondHeld = false;
        try
        {
            first.DangerousAddRef(ref firstHeld);
            second.DangerousAddRef(ref secondHeld);
            await use((int)first.DangerousGetHandle(), (int)second.DangerousGetHandle())
                .ConfigureAwait(false);
        }
        finally
        {
            if (secondHeld) second.DangerousRelease();
            if (firstHeld) first.DangerousRelease();
        }
    }
}
