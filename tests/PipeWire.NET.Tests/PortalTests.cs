using System.Drawing;
using System.Runtime.InteropServices;
using PipeWire.NET.Portal;
using Tmds.DBus.Protocol;

namespace PipeWire.NET.Tests;

// The portal flows against a stand-in portal on a private bus: the calls the real portal expects, the
// Response signals it answers with, cancellation, the session's end and the descriptor it hands over.
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public sealed class PortalTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ScreenCast_Start_RunsTheThreeStepsAndReadsTheStreams()
    {
        await using FakePortal portal = await StartPortalAsync();
        using DBusConnection bus = await ConnectAsync(portal);
        ScreenCastPortal screenCast = await ScreenCastPortal.CreateAsync(
            bus,
            cancellationToken: Cancel
        );
        Assert.AreEqual(5u, screenCast.Version);
        Assert.AreEqual(
            ScreenCastSources.Monitor | ScreenCastSources.Window,
            screenCast.AvailableSources
        );
        Assert.AreEqual(
            ScreenCastCursorModes.Hidden
                | ScreenCastCursorModes.Embedded
                | ScreenCastCursorModes.Metadata,
            screenCast.AvailableCursorModes
        );

        await using ScreenCastSession session = await screenCast.StartAsync(
            new()
            {
                Sources = ScreenCastSources.Monitor | ScreenCastSources.Window,
                Multiple = true,
                CursorMode = ScreenCastCursorModes.Metadata,
                Persistence = ScreenCastPersistence.UntilRevoked,
                RestoreToken = "restore-0",
            },
            Cancel
        );

        CollectionAssert.AreEqual(
            new[]
            {
                "org.freedesktop.portal.ScreenCast.CreateSession",
                "org.freedesktop.portal.ScreenCast.SelectSources",
                "org.freedesktop.portal.ScreenCast.Start",
            },
            portal
                .Calls.Where(static c => c.Contains("ScreenCast.", StringComparison.Ordinal))
                .ToArray()
        );
        Dictionary<string, VariantValue> selected = portal.OptionsOf("SelectSources");
        Assert.AreEqual(3u, selected["types"].GetUInt32());
        Assert.IsTrue(selected["multiple"].GetBool());
        Assert.AreEqual(4u, selected["cursor_mode"].GetUInt32());
        Assert.AreEqual(2u, selected["persist_mode"].GetUInt32());
        Assert.AreEqual("restore-0", selected["restore_token"].GetString());

        Assert.AreEqual(portal.SessionHandle, session.Handle);
        Assert.AreEqual("restore-1", session.RestoreToken);
        Assert.HasCount(2, session.Streams);
        ScreenCastStream first = session.Streams[0];
        Assert.AreEqual(42u, first.NodeId);
        Assert.AreEqual("0", first.Id);
        Assert.AreEqual(new Point(0, 0), first.Position);
        Assert.AreEqual(new Size(1920, 1080), first.Size);
        Assert.AreEqual(ScreenCastSources.Monitor, first.Source);
        Assert.AreEqual(1042ul, first.Serial);
        ScreenCastStream second = session.Streams[1];
        Assert.AreEqual(43u, second.NodeId);
        Assert.AreEqual(new Point(1920, 0), second.Position);
        Assert.AreEqual(ScreenCastSources.Window, second.Source);
        Assert.IsNull(second.Serial, "A portal before version 6 gives no serial.");
    }

    [TestMethod]
    public async Task ScreenCast_LeavesUnaskedOptionsOut()
    {
        await using FakePortal portal = await StartPortalAsync();
        using DBusConnection bus = await ConnectAsync(portal);
        ScreenCastPortal screenCast = await ScreenCastPortal.CreateAsync(
            bus,
            cancellationToken: Cancel
        );
        await using ScreenCastSession session = await screenCast.StartAsync(new(), Cancel);
        Dictionary<string, VariantValue> selected = portal.OptionsOf("SelectSources");
        Assert.AreEqual(1u, selected["types"].GetUInt32());
        Assert.IsFalse(selected.ContainsKey("cursor_mode"));
        Assert.IsFalse(selected.ContainsKey("persist_mode"));
        Assert.IsFalse(selected.ContainsKey("restore_token"));
    }

    [TestMethod]
    public async Task ScreenCast_FollowsARequestPathThePortalChose()
    {
        await using FakePortal portal = await StartPortalAsync();
        portal.MovesRequests = true;
        using DBusConnection bus = await ConnectAsync(portal);
        ScreenCastPortal screenCast = await ScreenCastPortal.CreateAsync(
            bus,
            cancellationToken: Cancel
        );
        await using ScreenCastSession session = await screenCast.StartAsync(new(), Cancel);
        Assert.HasCount(2, session.Streams);
    }

    [TestMethod]
    public async Task ScreenCast_UserCancelling_ThrowsAndClosesTheSession()
    {
        await using FakePortal portal = await StartPortalAsync();
        portal.StartResponse = 1;
        using DBusConnection bus = await ConnectAsync(portal);
        ScreenCastPortal screenCast = await ScreenCastPortal.CreateAsync(
            bus,
            cancellationToken: Cancel
        );
        PortalException refused = await Assert.ThrowsExactlyAsync<PortalException>(() =>
            screenCast.StartAsync(new(), Cancel)
        );
        Assert.AreEqual(PortalResponse.Cancelled, refused.Response);
        await WaitForAsync(() => portal.Closed.Contains(portal.SessionHandle));
    }

    [TestMethod]
    public async Task ScreenCast_Cancellation_ClosesTheOpenRequest()
    {
        await using FakePortal portal = await StartPortalAsync();
        portal.SelectSourcesResponse = null;
        using DBusConnection bus = await ConnectAsync(portal);
        ScreenCastPortal screenCast = await ScreenCastPortal.CreateAsync(
            bus,
            cancellationToken: Cancel
        );
        using CancellationTokenSource dialog = CancellationTokenSource.CreateLinkedTokenSource(
            Cancel
        );
        Task<ScreenCastSession> starting = screenCast.StartAsync(new(), dialog.Token);
        await WaitForAsync(() =>
            portal.Calls.Contains("org.freedesktop.portal.ScreenCast.SelectSources")
        );
        await dialog.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => starting);
        await WaitForAsync(() =>
            portal.Closed.Any(static p => p.Contains("/request/", StringComparison.Ordinal))
        );
        await WaitForAsync(() => portal.Closed.Contains(portal.SessionHandle));
    }

    [TestMethod]
    public async Task ScreenCast_PortalEndingTheSession_CompletesClosed()
    {
        await using FakePortal portal = await StartPortalAsync();
        using DBusConnection bus = await ConnectAsync(portal);
        ScreenCastPortal screenCast = await ScreenCastPortal.CreateAsync(
            bus,
            cancellationToken: Cancel
        );
        await using ScreenCastSession session = await screenCast.StartAsync(new(), Cancel);
        Assert.IsFalse(session.Closed.IsCompleted);
        portal.CloseSession();
        await session.Closed.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    [TestMethod]
    public async Task ScreenCast_DisposingTheSession_ClosesItOnThePortal()
    {
        await using FakePortal portal = await StartPortalAsync();
        using DBusConnection bus = await ConnectAsync(portal);
        ScreenCastPortal screenCast = await ScreenCastPortal.CreateAsync(
            bus,
            cancellationToken: Cancel
        );
        ScreenCastSession session = await screenCast.StartAsync(new(), Cancel);
        await session.DisposeAsync();
        Assert.Contains(session.Handle, portal.Closed);
        _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            session.OpenPipeWireRemoteAsync(Cancel)
        );
    }

    [TestMethod]
    public async Task OpenPipeWireRemote_HandsOverAWorkingDescriptor()
    {
        await using FakePortal portal = await StartPortalAsync();
        using DBusConnection bus = await ConnectAsync(portal);
        ScreenCastPortal screenCast = await ScreenCastPortal.CreateAsync(
            bus,
            cancellationToken: Cancel
        );
        await using ScreenCastSession session = await screenCast.StartAsync(new(), Cancel);
        using SafeHandle remote = await session.OpenPipeWireRemoteAsync(Cancel);
        await AssertCarriesAsync(portal, remote);
    }

    [TestMethod]
    public async Task Camera_Access_ThenRemote()
    {
        await using FakePortal portal = await StartPortalAsync();
        using DBusConnection bus = await ConnectAsync(portal);
        CameraPortal camera = await CameraPortal.CreateAsync(bus, cancellationToken: Cancel);
        Assert.IsTrue(camera.IsCameraPresent);
        using SafeHandle remote = await camera.OpenCameraRemoteAsync(Cancel);
        CollectionAssert.AreEqual(
            new[]
            {
                "org.freedesktop.portal.Camera.AccessCamera",
                "org.freedesktop.portal.Camera.OpenPipeWireRemote",
            },
            portal
                .Calls.Where(static c => c.Contains("Camera.", StringComparison.Ordinal))
                .ToArray()
        );
        await AssertCarriesAsync(portal, remote);
    }

    [TestMethod]
    public async Task Camera_Refused_Throws()
    {
        await using FakePortal portal = await StartPortalAsync();
        portal.AccessCameraResponse = 2;
        portal.CameraPresent = false;
        using DBusConnection bus = await ConnectAsync(portal);
        CameraPortal camera = await CameraPortal.CreateAsync(bus, cancellationToken: Cancel);
        Assert.IsFalse(camera.IsCameraPresent);
        PortalException refused = await Assert.ThrowsExactlyAsync<PortalException>(() =>
            camera.OpenCameraRemoteAsync(Cancel)
        );
        Assert.AreEqual(PortalResponse.Failed, refused.Response);
        Assert.DoesNotContain("org.freedesktop.portal.Camera.OpenPipeWireRemote", portal.Calls);
    }

    [TestMethod]
    public async Task Connect_WithNoPortalOnTheBus_Throws()
    {
        await using FakePortal bare = await StartPortalAsync(claimName: false);
        using DBusConnection bus = await ConnectAsync(bare);
        _ = await Assert.ThrowsExactlyAsync<PortalException>(() =>
            ScreenCastPortal.CreateAsync(bus, cancellationToken: Cancel)
        );
        _ = await Assert.ThrowsExactlyAsync<PortalException>(() =>
            CameraPortal.CreateAsync(bus, cancellationToken: Cancel)
        );
    }

    private CancellationToken Cancel => TestContext.CancellationToken;

    // The descriptor reads what the stand-in writes into the pipe it handed over.
    private static async Task AssertCarriesAsync(FakePortal portal, SafeHandle remote)
    {
        Assert.IsFalse(remote.IsInvalid);
        byte[] sent = [1, 2, 3, 4];
        await portal.RemoteWriter.WriteAsync(sent);
        await portal.RemoteWriter.FlushAsync();
        using FileStream read = new(
            new Microsoft.Win32.SafeHandles.SafeFileHandle(
                remote.DangerousGetHandle(),
                ownsHandle: false
            ),
            FileAccess.Read
        );
        byte[] received = new byte[sent.Length];
        await read.ReadExactlyAsync(received);
        CollectionAssert.AreEqual(sent, received);
    }

    private static async Task<FakePortal> StartPortalAsync(bool claimName = true) =>
        await FakePortal.StartAsync(claimName)
        ?? throw new AssertInconclusiveException("No dbus-daemon to run a stand-in portal on.");

    private static async Task<DBusConnection> ConnectAsync(FakePortal portal)
    {
        DBusConnection bus = new(portal.Address);
        await bus.ConnectAsync();
        return bus;
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            Cancel
        );
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
