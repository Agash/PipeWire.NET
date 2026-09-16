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

    // - The eventfd stand-in for explicit-sync timelines (sys/eventfd.h). Real timelines are DRM
    // syncobjs (DrmSyncobj); these exist because upstream's video-src-sync example passes eventfds
    // labelled as syncobjs ("just an example, not really syncobj here"). The convention is the
    // example's: write adds one, read waits for nonzero and takes it. A descriptor is only treated
    // this way once the kernel has confirmed it is an eventfd (IsEventfd), and every call reports
    // failure - see SyncTimeline for what went wrong when neither held. CreateEventfd has no
    // production caller of its own; it builds such descriptors for the tests. -

    /// <summary>Creates an eventfd timeline starting at zero, blocking mode like upstream.</summary>
    /// <exception cref="IOException">The kernel refused.</exception>
    internal static int CreateEventfd()
    {
        int fd = NativeLibc.eventfd(0, NativeLibc.O_CLOEXEC);
        if (fd < 0)
            throw new IOException($"eventfd failed with errno {Marshal.GetLastPInvokeError()}.");

        return fd;
    }

    /// <summary>Whether the kernel says <paramref name="fd"/> is an eventfd.</summary>
    /// <remarks>
    /// An eventfd is an anonymous inode, and <c>/proc/self/fd/N</c> names its kind:
    /// <c>anon_inode:[eventfd]</c>. Nothing about a syncobj, a pipe or a DMA-BUF reads that way, so
    /// this is what separates upstream's stand-in from a descriptor that merely failed to import.
    /// </remarks>
    internal static bool IsEventfd(int fd)
    {
        if (fd < 0) return false;

        try
        {
            string? target = new FileInfo($"/proc/self/fd/{fd}").LinkTarget;
            return string.Equals(target, "anon_inode:[eventfd]", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // Deliberately not logged: a descriptor /proc cannot describe is not an eventfd, and the
            // caller reports the descriptor as unusable.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Deliberately not logged, for the same reason.
            return false;
        }
    }

    /// <summary>Signals a timeline (adds one).</summary>
    /// <returns>0, or the errno of the failed write.</returns>
    /// <remarks>
    /// Retried on EINTR: a dropped signal is a peer left waiting until its timeout.
    /// </remarks>
    internal static unsafe int SignalEventfd(int fd)
    {
        ulong one = 1;
        while (NativeLibc.write(fd, &one, 8) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno != NativeLibc.EINTR) return errno;
        }

        return 0;
    }

    /// <summary>Waits for a timeline to become nonzero and takes one count, up to <paramref name="timeout"/>.</summary>
    /// <remarks>
    /// Through <c>poll</c> rather than a bare blocking read, so an eventfd wait has the same
    /// deadline and the same three outcomes as a syncobj wait. Upstream's example blocks without
    /// one; a peer that never signals would then hang the loop thread for good.
    /// </remarks>
    internal static unsafe SyncWait WaitEventfd(int fd, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            var pfd = new PosixPollFd { fd = fd, events = (short)NativeLibc.POLLIN };
            int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);

            int ready = NativeLibc.poll(&pfd, 1, remaining);
            if (ready < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == NativeLibc.EINTR) continue;
                return new SyncWait(SyncWaitOutcome.Failed, errno);
            }

            if (ready == 0) return new SyncWait(SyncWaitOutcome.TimedOut, NativeLibc.ETIME);

            if ((pfd.revents & NativeLibc.POLLNVAL) != 0)
                return new SyncWait(SyncWaitOutcome.Failed, NativeLibc.EBADF);

            if ((pfd.revents & (NativeLibc.POLLERR | NativeLibc.POLLHUP)) != 0)
                return new SyncWait(SyncWaitOutcome.Failed, NativeLibc.EIO);

            ulong taken;
            if (NativeLibc.read(fd, &taken, 8) >= 0) return new SyncWait(SyncWaitOutcome.Reached, 0);

            int readErrno = Marshal.GetLastPInvokeError();
            if (readErrno != NativeLibc.EINTR) return new SyncWait(SyncWaitOutcome.Failed, readErrno);
        }
    }

    internal static void CloseDescriptor(int fd)
    {
        if (fd >= 0) _ = NativeLibc.close(fd);
    }

}
