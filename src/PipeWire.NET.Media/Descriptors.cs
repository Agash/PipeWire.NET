using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PipeWire.NET;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Media;

/// <summary>Duplicating a borrowed dmabuf descriptor so it can outlive the handler that saw it.</summary>
internal static partial class Descriptors
{
    /// <summary>Duplicates <paramref name="fd"/>, or returns -1 when it is not a descriptor.</summary>
    /// <exception cref="IOException">The kernel refused to duplicate it.</exception>
    /// <remarks>
    /// Close-on-exec, so a dmabuf a caller kept does not turn up in a process it spawns later.
    /// <c>dup</c> would not set it.
    /// </remarks>
    internal static SafeDescriptorHandle Duplicate(long fd)
    {
        if (fd < 0) return new SafeDescriptorHandle();

        // Range-checked before the narrowing cast. A descriptor is an int on Linux, so a value
        // outside that range did not come from the kernel and truncating it names a different file.
        if (fd > int.MaxValue)
            throw new IOException($"descriptor {fd} is not a file descriptor this process can hold.");

        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("descriptors are a Linux concept here.");

        try
        {
            using SafeFileHandle copy = FdInterop.DuplicateWithCloseOnExec((int)fd);
            var owned = new SafeDescriptorHandle((int)copy.DangerousGetHandle());
            copy.SetHandleAsInvalid();
            return owned;
        }
        catch (PipeWireInteropException e)
        {
            throw new IOException($"dup of descriptor {fd} failed with errno {-e.Result}.", e);
        }
    }

    // - eventfd timelines for explicit sync (sys/eventfd.h). A timeline descriptor behaves like a
    // semaphore counter: write adds, read waits for nonzero and takes. The acquire timeline is
    // signalled when the frame is ready, the release timeline when the consumer is done with it,
    // which is exactly the wait/signal pairing explicit sync needs without any GPU involved. -

    private const int EFD_CLOEXEC = 0x80000; // == O_CLOEXEC: close-on-exec, never inherited

    /// <summary>Creates an eventfd timeline starting at zero, blocking mode like upstream.</summary>
    /// <exception cref="IOException">The kernel refused.</exception>
    internal static int CreateEventfd()
    {
        int fd = Eventfd(0, EFD_CLOEXEC);
        if (fd < 0)
            throw new IOException($"eventfd failed with errno {Marshal.GetLastPInvokeError()}.");

        return fd;
    }

    /// <summary>Signals a timeline (adds one).</summary>
    /// <remarks>
    /// Retried on EINTR: nothing waiting on a timeline times out, so a dropped signal is a peer
    /// that waits forever.
    /// </remarks>
    internal static unsafe void SignalEventfd(int fd)
    {
        ulong one = 1;
        while (Write(fd, &one, 8) < 0 && Marshal.GetLastPInvokeError() == EIntr) { }
    }

    /// <summary>Waits for a timeline to become nonzero and takes one count.</summary>
    /// <remarks>
    /// Blocks the calling thread, like upstream's consumer and producer: only call it where a
    /// missing signal is a peer bug rather than a slow peer, because nothing here will time out.
    /// </remarks>
    internal static unsafe void WaitEventfd(int fd)
    {
        ulong taken;
        while (Read(fd, &taken, 8) < 0 && Marshal.GetLastPInvokeError() == EIntr) { }
    }

    internal static void CloseEventfd(int fd)
    {
        if (fd >= 0) _ = Close(fd);
    }

    [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
    private static partial int Eventfd(uint initval, int flags);

    /// <summary><c>EINTR</c>: interrupted by a signal, so the call is retried rather than lost.</summary>
    private const int EIntr = 4;

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint Read(int fd, void* buf, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint Write(int fd, void* buf, nuint count);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
}
