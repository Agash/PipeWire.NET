using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using PipeWire.NET.Interop;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// The descriptor plumbing and the exception taxonomy: what a caller catches, and what a
/// duplicated descriptor is.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed partial class DescriptorAndExceptionTests : PipeWireTestBase
{
    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("descriptors are a Linux concept here.");
    }

    /// <summary>
    /// The duplicate must not survive an exec. Checked against the kernel rather than against the
    /// flag we passed, because the flag being wrong is the failure.
    /// </summary>
    [TestMethod]
    public void ADuplicatedPlaneDescriptor_CarriesCloseOnExec()
    {
        RequireLinux();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "x");
        try
        {
            using SafeFileHandle file = File.OpenHandle(path);
            var plane = new VideoPlane(file.DangerousGetHandle(), Offset: 0, Stride: 4, Size: 1);

            using SafeDescriptorHandle copy = plane.DuplicateFd();

            int flags = Fcntl(copy.Descriptor, FGetFd);
            Assert.IsTrue(flags >= 0, "F_GETFD failed on the duplicate");
            Assert.AreEqual(FdCloexec, flags & FdCloexec,
                "the duplicate would be inherited across exec");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ADescriptorHandle_ClosesWhatItOwnsAndRefusesToHandOutAClosedOne()
    {
        RequireLinux();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "x");
        try
        {
            using SafeFileHandle file = File.OpenHandle(path);
            SafeDescriptorHandle owned = new VideoPlane(file.DangerousGetHandle(), 0, 4, 1).DuplicateFd();
            int raw = owned.Descriptor;

            Assert.IsTrue(Fcntl(raw, FGetFd) >= 0, "the duplicate is not open");
            owned.Dispose();

            Assert.IsTrue(owned.IsClosed);
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owned.Descriptor);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ADescriptorHandleOverNothing_IsInvalidAndSafeToDispose()
    {
        using var none = new SafeDescriptorHandle();

        Assert.IsTrue(none.IsInvalid);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = none.Descriptor);
    }

    [TestMethod]
    public void EveryExceptionInTheTaxonomy_IsCaughtAsThePipeWireBase()
    {
        PipeWireException[] all =
        [
            new PipeWireConnectFailedException("pw_context_connect", -2),
            new PipeWireConnectionClosedException("request", -32, 0, "connection error"),
            new PipeWireRequestRefusedException("create", -13, 7, "refused"),
            new PipeWireInteropException("pw_core_sync", -12),
        ];

        foreach (PipeWireException e in all)
            Assert.IsInstanceOfType<PipeWireException>(e, $"{e.GetType().Name} is outside the hierarchy");

        Assert.IsInstanceOfType<PipeWireConnectionException>(all[0]);
        Assert.IsInstanceOfType<PipeWireConnectionException>(all[1]);
        Assert.IsNotInstanceOfType<PipeWireConnectionException>(all[2],
            "a refusal is not a connection failure");
        Assert.IsNotInstanceOfType<PipeWireConnectionException>(all[3],
            "a local call is not a connection failure");
    }

    [TestMethod]
    public void EachExceptionKind_CarriesTheOperationCodeObjectAndMessage()
    {
        var refused = new PipeWireRequestRefusedException("create", -13, 7, "no permission");

        Assert.AreEqual("create", refused.Operation);
        Assert.AreEqual(-13, refused.Result);
        Assert.AreEqual((uint)7, refused.ObjectId);
        Assert.AreEqual("no permission", refused.DaemonMessage);
        Assert.IsTrue(refused.IsPermissionDenied);
        StringAssert.Contains(refused.Message, "EACCES");
        StringAssert.Contains(refused.Message, "object 7");
        StringAssert.Contains(refused.Message, "no permission");

        var closed = new PipeWireConnectionClosedException("request", -32, 0, "connection error");
        Assert.IsTrue(closed.IsDisconnected);
        Assert.IsFalse(closed.IsPermissionDenied);

        var local = new PipeWireInteropException("pw_context_new", -12);
        Assert.AreEqual(-12, local.Result);
        Assert.IsNull(local.ObjectId);
        Assert.IsNull(local.DaemonMessage);
        StringAssert.Contains(local.Message, "ENOMEM");
    }

    /// <summary>
    /// Every type carries the standard shapes, so a caller can rethrow or wrap one the way it
    /// would any other exception.
    /// </summary>
    [TestMethod]
    public void EveryExceptionKind_OffersTheStandardConstructors()
    {
        var inner = new InvalidOperationException("inner");

        foreach (Type type in (Type[])[
            typeof(PipeWireException),
            typeof(PipeWireConnectionException),
            typeof(PipeWireConnectFailedException),
            typeof(PipeWireConnectionClosedException),
            typeof(PipeWireRequestRefusedException),
            typeof(PipeWireInteropException),
        ])
        {
            var bare = (PipeWireException)Activator.CreateInstance(type)!;
            Assert.AreEqual("unknown", bare.Operation, type.Name);

            var described = (PipeWireException)Activator.CreateInstance(type, "boom")!;
            Assert.AreEqual("boom", described.Message, type.Name);
            Assert.AreEqual("unknown", described.Operation, type.Name);

            var wrapped = (PipeWireException)Activator.CreateInstance(type, "boom", inner)!;
            Assert.AreSame(inner, wrapped.InnerException, type.Name);
        }
    }

    /// <summary>A borrowed handle is held for the whole call, not just until the first await.</summary>
    [TestMethod]
    public async Task BorrowAsync_HoldsTheHandleUntilTheWorkCompletes()
    {
        RequireLinux();
        using var listening = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        SafeHandle handle = listening.SafeHandle;

        var released = new TaskCompletionSource();
        bool openDuringAwait = false;

        Task borrow = FdInterop.BorrowAsync(handle, handle, async (first, second) =>
        {
            Assert.AreEqual(first, second);
            await released.Task.ConfigureAwait(false);
            openDuringAwait = Fcntl(first, FGetFd) >= 0;
        });

        released.SetResult();
        await borrow;

        Assert.IsTrue(openDuringAwait, "the descriptor was not open for the whole borrow");
    }

    [TestMethod]
    public void Borrow_ReturnsWhatTheWorkProducedAndReleasesAfterwards()
    {
        RequireLinux();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        int seen = FdInterop.Borrow(socket.SafeHandle, fd => fd);

        Assert.AreEqual((int)socket.SafeHandle.DangerousGetHandle(), seen);
        Assert.IsFalse(socket.SafeHandle.IsClosed, "the borrow must not close what it borrowed");
    }

    private const int FGetFd = 1;
    private const int FdCloexec = 1;

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int fd, int cmd);
}
