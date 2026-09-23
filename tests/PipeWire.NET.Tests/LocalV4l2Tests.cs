using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// <c>local-v4l2</c>: a real camera device, enumerated by PipeWire and captured from.
/// </summary>
/// <remarks>
/// <para>
/// The camera is a <c>v4l2loopback</c> device fed by ffmpeg, provisioned by the test itself. That
/// keeps this honest on a headless box: everything upstream's example depends on is present -
/// a <c>/dev/video*</c> node, the v4l2 SPA plugin enumerating it, and a graph node to capture from -
/// without needing hardware nobody can guarantee.
/// </para>
/// <para>
/// What it exercises that no other test does is the device path: frames arriving from a kernel
/// driver through SPA's v4l2 plugin rather than from another PipeWire client. Format negotiation
/// runs against what the driver offers rather than what a cooperating producer chose.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[TestCategory("RequiresCamera")]
[SupportedOSPlatform("linux")]
public sealed class LocalV4l2Tests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    /// <summary>
    /// A v4l2 camera reaches the graph, and frames captured from it carry real pixels.
    /// </summary>
    /// <remarks>
    /// The node appearing is the SPA v4l2 plugin's doing; the frames are the driver's. Asserting
    /// that the pixels are not uniformly zero is what separates a working capture from one that
    /// negotiated a format and then delivered empty buffers, which is what a wrong stride or a
    /// mishandled <c>mapoffset</c> produces.
    /// </remarks>
    [TestMethod]
    public async Task AV4l2Camera_IsEnumeratedAndCapturedFrom()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using VirtualCamera camera = await VirtualCamera.StartAsync(cts.Token);

        await using var ctx = new PipeWireContext("pwnet-v4l2", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // The session manager's v4l2 monitor creates the node; it is named after the card.
        PipeWireNode? cam = null;
        for (var i = 0; i < 150 && cam is null; i++)
        {
            cam = reg.Current.Nodes.FirstOrDefault(n =>
                n.Media == PipeWireMediaKind.Video
                && n.Flow == PipeWireMediaFlow.Source
                && (
                    (
                        n.Description?.Contains(
                            VirtualCamera.CardLabel,
                            StringComparison.OrdinalIgnoreCase
                        ) ?? false
                    ) || (n.NodeName?.Contains("v4l2", StringComparison.OrdinalIgnoreCase) ?? false)
                )
            );

            if (cam is null)
                await Task.Delay(100, cts.Token);
        }

        if (cam is null)
        {
            Assert.Inconclusive(
                $"no v4l2 video source appeared for {camera.DevicePath}; the session manager's "
                    + "v4l2 monitor may not be running."
            );
        }

        var frames = 0;
        var nonBlank = 0;
        var geometry = (W: 0, H: 0);

        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-v4l2-sink");
        capture.FrameReady += (_, f) =>
        {
            Interlocked.Increment(ref frames);
            geometry = (f.Width, f.Height);

            if (f.Pixels.IsEmpty)
                return;

            foreach (byte b in f.Pixels)
            {
                if (b != 0)
                {
                    Interlocked.Increment(ref nonBlank);
                    return;
                }
            }
        };

        capture.Connect(cam!.NodeId);
        await capture.WaitForStreamingAsync(cts.Token);
        await Task.Delay(1500, cts.Token);

        Assert.IsTrue(
            Volatile.Read(ref frames) > 0,
            "the camera node was linked but delivered no frames"
        );

        Assert.IsTrue(
            geometry.W > 0 && geometry.H > 0,
            $"frames arrived with a degenerate geometry {geometry.W}x{geometry.H}"
        );

        Assert.IsTrue(
            Volatile.Read(ref nonBlank) > 0,
            "every frame from the camera was entirely zero, so the capture negotiated a format and "
                + "then received nothing"
        );
    }

    /// <summary>
    /// The v4l2 monitor factory can be exported directly, which is the other half of the example.
    /// </summary>
    /// <remarks>
    /// <c>local-v4l2</c> loads the v4l2 SPA factory itself rather than relying on the session
    /// manager to have done it. That is the same mechanism as <c>export-spa-device</c>: the plugin
    /// provides a device interface, and this process publishes it.
    /// </remarks>
    [TestMethod]
    public async Task TheV4l2MonitorFactory_CanBeExportedDirectly()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext(
            "pwnet-v4l2-export",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        PipeWireDeviceProvider device;
        try
        {
            // The library is named outright: a client's context.spa-libs map has no api.v4l2.* entry,
            // so resolving by factory name alone fails even though libspa-v4l2 is installed.
            device = PipeWireDeviceProvider.FromSpaFactory(
                ctx,
                "api.v4l2.enum.udev",
                libraryName: "v4l2/libspa-v4l2"
            );
        }
        catch (InvalidOperationException ex)
        {
            Assert.Inconclusive($"the v4l2 SPA monitor factory is unavailable: {ex.Message}");
            return;
        }

        using (device)
        {
            // Exporting a monitor does not itself create a node - it enumerates hardware and the
            // graph creates nodes for what it finds. Surviving the export is the contract here.
            await Task.Delay(500, cts.Token);

            Assert.IsFalse(ctx.IsDisposed, "exporting the v4l2 monitor tore down the context");

            // The connection is still usable afterwards, which a bad export would not leave it.
            await using var reg = new PipeWireRegistry(ctx);
            await reg.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsTrue(reg.Current.Nodes.Any(), "the graph was unreachable after the export");
        }
    }
}
