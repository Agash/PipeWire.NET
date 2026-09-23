// Hand-maintained half of the generated `NativeLibc` partial class: the C library entry points
// this library calls directly.
//
// generate/generate.sh wipes generated/ on every run, so anything hand-written lives here. The
// class is the same one generate/libc.rsp emits, so a caller sees these alongside the generated
// errno values and the generated PosixPollFd without knowing which half declared what.
//
// Why these are not generated (the EINTR they retry on is, from asm-generic/errno-base.h): this
// assembly sets DisableRuntimeMarshalling, and errno is load-bearing - Descriptors.SignalEventfd
// and WaitEventfd retry on EINTR by reading Marshal.GetLastPInvokeError() straight after the call.
// ClangSharp 21.1.8 can spell that two ways and both are wrong here:
//   - SetLastError = true on a DllImport is CA1420: it requires runtime marshalling.
//   - --generate setslastsystemerror-attribute emits ClangSharp's own internal
//     [SetsLastSystemError], which is [Conditional("DEBUG")] - a marker with no runtime effect -
//     and it suppresses SetLastError, so errno would never be captured at all.
// It cannot emit [LibraryImport], which is the only form that both works under
// DisableRuntimeMarshalling and captures errno. A P/Invoke declaration also carries its
// attributes on the single method declaration, so this cannot be a generated signature that a
// partial decorates afterwards - the hand-written half has to own the whole declaration.

#pragma warning disable CA1707 // Identifiers should not contain underscores (matches generated style)

using System.Runtime.InteropServices;

namespace PipeWire.NET.Interop;

internal static unsafe partial class NativeLibc
{
    /// <summary><c>fcntl(2)</c>. Used with <c>F_DUPFD_CLOEXEC</c> to duplicate a descriptor.</summary>
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    internal static partial int fcntl(int fd, int cmd, int arg);

    /// <summary><c>getsockopt(2)</c>, for asking whether a descriptor is a listening socket.</summary>
    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    internal static partial int getsockopt(int fd, int level, int optname, int* optval, int* optlen);

    /// <summary><c>eventfd(2)</c>, the counter behind an explicit-sync timeline.</summary>
    [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
    internal static partial int eventfd(uint initval, int flags);

    /// <summary>
    /// <c>poll(2)</c>, so an eventfd timeline can be waited on with a deadline, like a syncobj one.
    /// <paramref name="nfds"/> is <c>nfds_t</c>, an unsigned long.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    internal static partial int poll(PosixPollFd* fds, nuint nfds, int timeout);

    /// <summary><c>read(2)</c>. Returns a negative value on failure, with errno set.</summary>
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    internal static partial nint read(int fd, void* buf, nuint count);

    /// <summary><c>write(2)</c>. Returns a negative value on failure, with errno set.</summary>
    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    internal static partial nint write(int fd, void* buf, nuint count);

    /// <summary>
    /// <c>close(2)</c>. No <c>SetLastError</c>: every caller here closes on a teardown path where
    /// the errno is not actionable, and retrying a failed close is itself a bug.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "close")]
    internal static partial int close(int fd);

}
