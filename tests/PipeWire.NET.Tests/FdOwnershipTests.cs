using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// fd ownership contract of <see cref="PipeWireContext.StartAsync(SafeFileHandle, CancellationToken)"/>.
///
/// The caller's handle is only BORROWED: the library duplicates the descriptor
/// (FD_CLOEXEC, like PipeWire's own impl_steal_fd does) and hands the duplicate to
/// pw_context_connect_fd, which owns it from a successful connect on. Pinned here:
/// 1. the caller's handle is never closed by the library - after the context is
///    disposed the original descriptor is still fully usable;
/// 2. the library's duplicate does not outlive the connection: after disposal no
///    descriptor of this process points at the caller's file beyond the caller's own;
/// 3. a failed attempt returns the context to a startable state - a start after
///    a failed one succeeds;
/// 4. null/invalid handles are rejected before any descriptor work.
///
/// How the connect behaves over a descriptor that is not a PipeWire socket is a
/// native detail: PipeWire wraps file fds in an idle source (their epoll_ctl fails
/// with EPERM, which the loop treats as always-ready), so the connect itself
/// succeeds and any failure only surfaces later as a protocol error. The ownership
/// contract does not depend on it, and neither do these tests.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class FdOwnershipTests
{
    [TestMethod]
    public async Task StartAsync_BorrowOnly_LeavesTheCallerHandleUsableAndLeaksNoDuplicate()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        using FileStream stream = OpenTempFile(path);

        await using PipeWireContext context = new("fd-ownership-test");

        // A regular file never reaches a daemon handshake - whatever the connect makes of it,
        // no assertion here depends on the outcome, only on what happens to the descriptors.
        await context.StartAsync(stream.SafeFileHandle);

        Assert.IsFalse(stream.SafeFileHandle.IsClosed,
            "the caller's handle is only borrowed and must never be closed by the library");

        await context.DisposeAsync();

        // Whichever pw path disposed of the duplicate - the io source that owned it after the
        // connect, or the connection teardown the context disposal ran - none of it may
        // survive the context, and the caller's own descriptor is the only one left pointing
        // at the file.
        Assert.AreEqual(1, FdsPointingTo(path),
            "the library-owned duplicate must not outlive the connection");
        stream.Seek(0, SeekOrigin.Begin);
        stream.WriteByte(0x2A);
        stream.Flush();
    }

    [TestMethod]
    public async Task StartAsync_NullHandle_ThrowsArgumentNull_BeforeAnyDescriptorWork()
    {
        await using PipeWireContext context = new("fd-ownership-test");

        Assert.ThrowsExactly<ArgumentNullException>(
            () => context.StartAsync((SafeFileHandle)null!));
    }

    [TestMethod]
    public async Task StartAsync_InvalidHandle_ThrowsArgument_BeforeAnyDescriptorWork()
    {
        await using PipeWireContext context = new("fd-ownership-test");
        using SafeFileHandle invalid = new(new IntPtr(-1), ownsHandle: true);

        Assert.ThrowsExactly<ArgumentException>(
            () => context.StartAsync(invalid).GetAwaiter().GetResult());
    }

    /// <summary>
    /// A failed start must return the context to a startable state, whichever start form failed.
    /// </summary>
    /// <remarks>
    /// The failing attempt is a resolved-daemon start pointed at a remote name whose socket does
    /// not exist - deterministic with or without a daemon on the machine, because the name is
    /// resolved at connect time. The recovery is exercised with the fd form over a regular file,
    /// whose connect succeeds structurally without a daemon: PipeWire wraps a file fd in an idle
    /// source, so no daemon is involved on this path at all.
    /// </remarks>
    [TestMethod]
    public async Task StartAsync_AfterAFailedAttempt_TheContextIsStartableAgain()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        using FileStream stream = OpenTempFile(path);

        await using PipeWireContext context = new("fd-ownership-test");

        // Environment.SetEnvironmentVariable does not reach the native environment, and it is
        // native code that reads PIPEWIRE_REMOTE at connect time; setenv goes through libc
        // directly. Restored before this test returns.
        NativeTestEnv.Set("PIPEWIRE_REMOTE", "fd-ownership-test-absent");
        try
        {
            await Assert.ThrowsExactlyAsync<PipeWireException>(() => context.StartAsync());
        }
        finally
        {
            NativeTestEnv.Unset("PIPEWIRE_REMOTE");
        }

        // The failed attempt fell back to Created; an fd start succeeds over the same context.
        await context.StartAsync(stream.SafeFileHandle);

        // And the recovered context is live rather than wedged: a second start is idempotent.
        await context.StartAsync(stream.SafeFileHandle);
    }

    /// <summary>Process environment access that native code (getenv) observes.</summary>
    private static class NativeTestEnv
    {
        [DllImport("libc")]
        private static extern int setenv(string name, string value, int overwrite);

        [DllImport("libc")]
        private static extern int unsetenv(string name);

        internal static void Set(string name, string value) => setenv(name, value, overwrite: 1);

        internal static void Unset(string name) => _ = unsetenv(name);
    }

    /// <summary>
    /// A real portal-shaped connection: a fd already connected to the daemon socket, handed to
    /// the library as a borrowed handle, must come back untouched and leave a working connection
    /// behind it.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task StartAsync_OverAConnectedDaemonSocket_ConnectsAndLeavesTheSocketUsable()
    {
        RequireLinux();
        using CancellationTokenSource cts = new(Budget);

        using Socket socket = await ConnectDaemonSocketAsync(cts.Token);
        using SafeFileHandle borrowed = new(socket.SafeHandle.DangerousGetHandle(), ownsHandle: false);

        await using PipeWireContext context = new("fd-ownership-test", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(borrowed, cts.Token);

        // The connection is live: the registry enumerated the graph over it.
        await using PipeWireRegistry registry = new(context);
        await registry.WaitForInitialEnumerationAsync(cts.Token);
        Assert.IsTrue(registry.Current.Version >= 0, "an enumerated graph carries a snapshot version");

        // Borrow-only: while the connection is up, the caller's socket still refers to a live
        // descriptor the caller may act on.
        Assert.IsFalse(borrowed.IsClosed);
        _ = socket.Poll(0, SelectMode.SelectRead);

        await context.DisposeAsync();
        socket.Dispose();

        // And it survives the teardown: the connection owned a duplicate, not this descriptor.
        Assert.IsFalse(borrowed.IsClosed);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task StartAsync_OverARawDescriptor_ConnectsAndTakesOwnership()
    {
        RequireLinux();
        using CancellationTokenSource cts = new(Budget);

        using Socket socket = await ConnectDaemonSocketAsync(cts.Token);
        int fd = (int)socket.SafeHandle.DangerousGetHandle();

        await using PipeWireContext context = new("fd-ownership-test", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(fd, cts.Token);

        await using PipeWireRegistry registry = new(context);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        // Ownership transferred on success: disposing the context tears the connection down,
        // which closes the descriptor. The socket object on the caller's side is a passenger
        // from here - dispose it without touching the descriptor again.
        await context.DisposeAsync();
        socket.Dispose();
    }

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static FileStream OpenTempFile(string path) =>
        new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 1,
            FileOptions.DeleteOnClose);

    /// <summary>A descriptor already connected to the daemon socket, no handshake attempted.</summary>
    /// <remarks>
    /// pw_context_connect_fd performs the PipeWire handshake itself; the fd only has to be
    /// connected. That is the shape a portal OpenPipeWireRemote fd arrives in.
    /// </remarks>
    private static async Task<Socket> ConnectDaemonSocketAsync(CancellationToken ct)
    {
        string runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            ?? throw new AssertFailedException("XDG_RUNTIME_DIR is not set; where is the daemon socket?");
        string socketPath = Path.Combine(runtimeDir, "pipewire-0");
        if (!File.Exists(socketPath))
            Assert.Inconclusive($"no PipeWire daemon socket at {socketPath}.");

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        return socket;
    }

    /// <summary>Descriptors of this process whose link target is <paramref name="path"/>.</summary>
    private static int FdsPointingTo(string path) =>
        Directory.GetFiles("/proc/self/fd")
            .Select(fd => new FileInfo(fd).LinkTarget)
            .Count(target => string.Equals(target, path, StringComparison.Ordinal));
}
