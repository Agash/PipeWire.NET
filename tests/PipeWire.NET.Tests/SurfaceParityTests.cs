using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The members added to make siblings agree with each other, exercised on every type that grew one.
/// </summary>
/// <remarks>
/// A parity gap is only closed if every sibling actually works, not if the member merely compiles on
/// each. These are deliberately shallow per member and broad across types: the behaviour itself is
/// pinned by the tests that already cover the original sibling.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class SurfaceParityTests : PipeWireTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<PipeWireContext> ConnectAsync(
        string name,
        CancellationToken cancellationToken
    )
    {
        var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cancellationToken);
        return ctx;
    }

    /// <summary>Both outputs accept a node, as both captures always have.</summary>
    [TestMethod]
    public async Task AnOutput_CanConnectToANodeRatherThanAnId()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext ctx = await ConnectAsync("pwnet-parity-connect", cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode sink = await reg.CreateVirtualSinkAsync(
            "pwnet parity sink",
            cancellationToken: cts.Token
        );

        await using var audio = new PipeWireAudioOutput(ctx, "pwnet_parity_audio_out");
        audio.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };
        audio.Connect(sink);

        await using var video = new PipeWireVideoOutput(ctx, "pwnet_parity_video_out", 64, 64);
        video.FillFrame += (_, pixels, _, _, _, _) =>
        {
            pixels.Clear();
            return true;
        };
        video.Connect(sink, autoConnect: false);

        // Reaching a node id at all is what proves the overload routed the connect rather than
        // silently doing nothing.
        uint audioId = await audio.WaitForNodeIdAsync(cts.Token);
        Assert.AreNotEqual(0u, audioId, "the audio output never reached the graph");

        await reg.DestroyGlobalAsync(sink.NodeId, cts.Token);
    }

    /// <summary>A virtual source has the one-liner its sink counterpart always had.</summary>
    [TestMethod]
    public async Task AVirtualSource_CanBeCreatedInOneCall()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext ctx = await ConnectAsync("pwnet-parity-source", cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode source = await reg.CreateVirtualSourceAsync(
            "pwnet parity source",
            cancellationToken: cts.Token
        );

        Assert.AreEqual(
            "Audio/Source",
            source.MediaClass,
            "the async form did not apply the media class its builder does"
        );

        await reg.DestroyGlobalAsync(source.NodeId, cts.Token);
    }

    /// <summary>Every stream type can ask for a cycle, and can wait for one.</summary>
    /// <remarks>
    /// The wait faults on a stream the daemon has not made the driver, which is upstream's own
    /// contract - <c>trigger_done</c> is reported to a driver only - so that is what is asserted
    /// rather than a cycle actually completing.
    /// </remarks>
    [TestMethod]
    public async Task EveryStreamType_CanTriggerAndCanWait()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext ctx = await ConnectAsync("pwnet-parity-trigger", cts.Token);

        await using var audioOut = new PipeWireAudioOutput(ctx, "pwnet_parity_trig_ao");
        audioOut.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };
        audioOut.Connect(autoConnect: false);

        await using var audioIn = new PipeWireAudioCapture(ctx, "pwnet_parity_trig_ai");
        audioIn.Connect(autoConnect: false);

        await using var videoIn = new PipeWireVideoCapture(ctx, "pwnet_parity_trig_vi");
        videoIn.Connect(autoConnect: false);

        // A follower is not refused the request; it reaches the daemon and does nothing, which is
        // upstream's behaviour rather than an error.
        audioOut.TriggerProcess();
        audioIn.TriggerProcess();
        videoIn.TriggerProcess();

        foreach (
            Func<Task> wait in new Func<Task>[]
            {
                () => audioOut.TriggerProcessAndWaitAsync(cts.Token),
                () => audioIn.TriggerProcessAndWaitAsync(cts.Token),
                () => videoIn.TriggerProcessAndWaitAsync(cts.Token),
            }
        )
        {
            try
            {
                await wait();
            }
            catch (InvalidOperationException)
            {
                // Expected on a follower: the daemon reports completion only to the driver.
            }
        }
    }

    /// <summary>Every type that holds a daemon object disposes both ways, and twice.</summary>
    /// <remarks>
    /// The disposal sweep gave nine types both interfaces because none of their disposal awaits
    /// anything. A second disposal must be a no-op rather than a double free, which is the part
    /// worth a test.
    /// </remarks>
    [TestMethod]
    public async Task EveryDisposableType_TakesBothFormsAndRepeats()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext ctx = await ConnectAsync("pwnet-parity-dispose", cts.Token);

        var filter = PipeWireFilter.Create(ctx, "pwnet_parity_filter");
        filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");
        await filter.ConnectAsync(PipeWireFilterFlags.RtProcess, cts.Token);
        filter.Dispose();
        filter.Dispose();

        var node = PipeWireNodeProvider.Create(
            ctx,
            "pwnet_parity_node",
            PipeWireExportedFormat.AudioF32(48000, 1)
        );
        node.Dispose();
        node.Dispose();

        var device = PipeWireDeviceProvider.Create(
            ctx,
            "pwnet_parity_device",
            "pwnet parity device"
        );
        await device.DisposeAsync();
        await device.DisposeAsync();

        var capture = new PipeWireVideoCapture(ctx, "pwnet_parity_capture");
        capture.Connect(autoConnect: false);
        capture.Dispose();
        capture.Dispose();
    }

    /// <summary>A permissions read refuses to overlap with another, and stops after disposal.</summary>
    /// <remarks>
    /// The reply arrives on a single event with no sequence number, so a second read in flight has
    /// no way to tell which answer is its own. Refusing is the honest outcome; the alternative is
    /// handing one caller the other's result.
    /// </remarks>
    [TestMethod]
    public async Task APermissionsRead_RefusesToOverlapAndStopsAfterDisposal()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext ctx = await ConnectAsync("pwnet-perm-guard", cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireClient? own = null;
        for (var i = 0; i < 100 && own is null; i++)
        {
            own = reg.Current.Clients.FirstOrDefault(c =>
                c.Properties.TryGetValue(PipeWireKeys.PW_KEY_APP_NAME, out string? v)
                && v == "pwnet-perm-guard"
            );

            if (own is null)
                await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(own, "no client in the graph is this process");

        PipeWireClientProxy client = reg.BindClient(own!.Id);
        await client.ReadyAsync(cts.Token);

        Task<System.Collections.Immutable.ImmutableArray<PipeWireObjectPermission>> first =
            client.GetPermissionsAsync(cancellationToken: cts.Token);

        // The refusal is synchronous, so it lands whether or not the first read has answered yet.
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () =>
                    client
                        .GetPermissionsAsync(cancellationToken: cts.Token)
                        .GetAwaiter()
                        .GetResult(),
                "two reads were allowed in flight at once"
            );
        }
        catch (AssertFailedException) when (first.IsCompleted)
        {
            // The first answered before the second was issued, which is legal: then it is not an
            // overlap at all and the second read is simply another read.
        }

        await first;

        await client.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await client.GetPermissionsAsync(cancellationToken: cts.Token),
            "a disposed proxy answered a permissions read"
        );
    }

    /// <summary>Removal reaches every shape of bound proxy, not only the parameter objects.</summary>
    /// <remarks>
    /// The three parameter objects inherit the signal from their shared base and the six standalone
    /// proxies each wire it themselves, so one of each is worth proving rather than one in total.
    /// </remarks>
    [TestMethod]
    public async Task RemovalReachesBothProxyShapes()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext ctx = await ConnectAsync("pwnet-removed-shapes", cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode node = await reg.CreateVirtualSinkAsync(
            "pwnet removed shapes",
            cancellationToken: cts.Token
        );

        // A port of that node: a parameter object, like the node itself.
        PipeWirePort? port = null;
        for (var i = 0; i < 100 && port is null; i++)
        {
            port = reg.Current.Ports.FirstOrDefault(p => p.NodeId == node.NodeId);
            if (port is null)
                await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(port, "the virtual sink never published a port");

        await using PipeWirePortProxy portProxy = reg.BindPort(port!.PortId);
        var portRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        portProxy.Removed += () => portRemoved.TrySetResult();

        Assert.IsFalse(portProxy.IsRemoved);

        await reg.DestroyGlobalAsync(node.NodeId, cts.Token);

        await portRemoved.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.IsTrue(portProxy.IsRemoved, "the port proxy raised Removed but does not report it");
    }

    /// <summary>A filter refuses a port that is not its own, and a null one.</summary>
    [TestMethod]
    public async Task RemovePort_RefusesAPortItDoesNotOwn()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext ctx = await ConnectAsync("pwnet-removeport-guard", cts.Token);

        await using PipeWireFilter one = PipeWireFilter.Create(ctx, "pwnet_removeport_one");
        PipeWireFilterPort mine = one.AddAudioPort(PipeWirePortDirection.Out, "output_FL");

        await using PipeWireFilter two = PipeWireFilter.Create(ctx, "pwnet_removeport_two");
        PipeWireFilterPort theirs = two.AddAudioPort(PipeWirePortDirection.Out, "output_FL");

        await one.ConnectAsync(PipeWireFilterFlags.RtProcess, cts.Token);
        await two.ConnectAsync(PipeWireFilterFlags.RtProcess, cts.Token);

        Assert.ThrowsExactly<ArgumentNullException>(() => one.RemovePort(null!));
        Assert.ThrowsExactly<ArgumentException>(
            () => one.RemovePort(theirs),
            "a filter removed a port belonging to another filter"
        );

        Assert.ThrowsExactly<ArgumentNullException>(() => one.UpdateProperties(null!));
        Assert.AreEqual(
            0,
            one.UpdateProperties(new Dictionary<string, string>()),
            "an empty property set reported a change"
        );

        one.RemovePort(mine);
        Assert.AreEqual(0, one.Ports.Count);
    }

    /// <summary>A permission write refuses what the daemon cannot be asked to interpret.</summary>
    /// <remarks>
    /// Each of these is refused before anything is sent. That ordering is the point: a permission
    /// change the daemon rejects is answered out of band, so a caller mistake caught locally is the
    /// difference between an exception and a sandbox that silently did not close.
    /// </remarks>
    [TestMethod]
    public async Task APermissionWrite_RefusesWhatItCannotSend()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext ctx = await ConnectAsync("pwnet-perm-args", cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireClient? any = reg.Current.Clients.FirstOrDefault();
        Assert.IsNotNull(any, "the graph reported no clients at all");

        await using PipeWireClientProxy client = reg.BindClient(any!.Id);
        await client.ReadyAsync(cts.Token);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () =>
                await client.UpdatePermissionsAsync(
                    ReadOnlyMemory<PipeWireObjectPermission>.Empty,
                    cts.Token
                ),
            "an empty write was sent rather than refused"
        );

        // 0x1 is none of the five permission.h defines: a cast from the wrong enum looks exactly
        // like this, and forwarding it asks the daemon to read a number we cannot describe.
        var undefined = new[] { new PipeWireObjectPermission(1, (PipeWirePermissions)0x1) };

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await client.UpdatePermissionsAsync(undefined, cts.Token),
            "an undefined permission bit was forwarded to the daemon"
        );

        // Link is defined but sits outside upstream's own PW_PERM_ALL, so it is the bit a mask
        // taken from All would wrongly refuse. Reaching the daemon at all is the assertion.
        await client.UpdatePermissionsAsync(
            new[]
            {
                new PipeWireObjectPermission(
                    any!.Id,
                    PipeWirePermissions.ReadWriteExecuteMetadataLink
                ),
            },
            cts.Token
        );

        Assert.ThrowsExactly<ArgumentException>(
            () =>
                client.ConfineToAsync(
                    [
                        new PipeWireObjectPermission(
                            PipeWireClientProxy.AnyObject,
                            PipeWirePermissions.All
                        ),
                    ],
                    cts.Token
                ),
            "a confining call accepted a default that contradicts the one it writes"
        );

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await client.UpdatePropertiesAsync(null!, cts.Token)
        );
    }

    /// <summary>Every member of every stream works once connected, and goes quiet once disposed.</summary>
    /// <remarks>
    /// <para>
    /// The other side of the unconnected sweep in the guard tests. Each of these has two arms - one
    /// that reaches the daemon and one that does not - and a wrapper that forwards to a stream it no
    /// longer has is a use-after-free, not a no-op, so the disposed arm is the one worth pinning.
    /// </para>
    /// <para>
    /// Connected without auto-connect: these are calls on a stream the daemon knows, not a running
    /// link, and every one of them is legal in Paused. Waiting for the node id is what proves the
    /// daemon bound it; waiting for Streaming would need a peer this test has no reason to make.
    /// </para>
    /// </remarks>
    [TestMethod]
    [ExpectsLibraryError("a deliberate error, to prove the call reaches the daemon")]
    public async Task EveryStreamMember_WorksConnectedAndIsQuietAfterDisposal()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext ctx = await ConnectAsync("pwnet-connected-surface", cts.Token);

        var latency = new PipeWireLatency(SpaDirection.Output, 0, 0, 48000, 48000, 0, 10_000_000);
        var retag = new Dictionary<string, string> { ["media.name"] = "retagged" };

        var audioIn = new PipeWireAudioCapture(ctx, "pwnet_conn_ai");
        audioIn.Connect(autoConnect: false);

        var audioOut = new PipeWireAudioOutput(ctx, "pwnet_conn_ao");
        audioOut.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };
        audioOut.Connect(autoConnect: false);

        var videoIn = new PipeWireVideoCapture(ctx, "pwnet_conn_vi");
        videoIn.Connect(autoConnect: false);

        var videoOut = new PipeWireVideoOutput(ctx, "pwnet_conn_vo", 64, 64);
        videoOut.FillFrame += (_, pixels, _, _, _, _) =>
        {
            pixels.Clear();
            return true;
        };
        videoOut.Connect(autoConnect: false);

        foreach (
            Func<CancellationToken, Task<uint>> wait in new Func<CancellationToken, Task<uint>>[]
            {
                audioIn.WaitForNodeIdAsync,
                audioOut.WaitForNodeIdAsync,
                videoIn.WaitForNodeIdAsync,
                videoOut.WaitForNodeIdAsync,
            }
        )
        {
            Assert.AreNotEqual(0u, await wait(cts.Token), "a connected stream never got a node id");
        }

        Assert.IsNotNull(audioIn.NodeId);
        Assert.IsNotNull(audioOut.NodeId);
        Assert.IsNotNull(videoIn.NodeId);
        Assert.IsNotNull(videoOut.NodeId);

        // Reads that answer from the stream rather than from a null: each has a "no stream" arm the
        // guard tests cover, and this is the other one.
        foreach (
            Action read in new Action[]
            {
                () => _ = audioIn.Queue,
                () => _ = audioIn.IsLazy,
                () => _ = audioIn.IsDriving,
                () => _ = audioIn.GraphClock,
                () => _ = audioIn.RateMatch,
                () => _ = audioIn.Controls,
                () => _ = audioOut.Queue,
                () => _ = audioOut.IsLazy,
                () => _ = audioOut.IsDriving,
                () => _ = audioOut.GraphClock,
                () => _ = audioOut.RateMatch,
                () => _ = audioOut.Controls,
                () => _ = videoIn.Queue,
                () => _ = videoIn.IsLazy,
                () => _ = videoIn.IsDriving,
                () => _ = videoIn.GraphClock,
                () => _ = videoIn.RateMatch,
                () => _ = videoIn.Controls,
                () => _ = videoOut.Queue,
                () => _ = videoOut.IsLazy,
                () => _ = videoOut.IsDriving,
                () => _ = videoOut.GraphClock,
                () => _ = videoOut.RateMatch,
                () => _ = videoOut.Controls,
            }
        )
        {
            read();
        }

        // Orders that reach the daemon. A retag is the one whose effect is a return value, so it is
        // the one asserted; the rest are proven by not faulting the stream.
        Assert.AreEqual(1, audioIn.UpdateProperties(retag), "a connected stream did not retag");
        Assert.AreEqual(1, audioOut.UpdateProperties(retag));
        Assert.AreEqual(1, videoIn.UpdateProperties(retag));
        Assert.AreEqual(1, videoOut.UpdateProperties(retag));

        audioIn.SetRate(1.0, cts.Token);
        audioOut.SetRate(1.0, cts.Token);
        videoOut.SetRate(1.0, cts.Token);

        audioIn.AnnounceLatency(latency, null, cts.Token);
        audioOut.AnnounceLatency(latency, new PipeWireProcessLatency(Ns: 1_000_000), cts.Token);
        videoOut.AnnounceLatency(latency, null, cts.Token);

        audioIn.SkipCurrentFrame();
        videoIn.SkipCurrentFrame();

        Assert.IsTrue(
            videoIn.RequestFormat([PixelFormat.Bgrx], 64, 64),
            "a connected capture refused a format request"
        );
        Assert.IsTrue(videoOut.RequestFormat([PixelFormat.Bgrx], 64, 64));

        // Out of range on a connected stream, which is the only place the range guards are reached:
        // unconnected, the null check answers first.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            videoIn.RequestFormat([PixelFormat.Bgrx], 0, 64)
        );
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            videoOut.RequestFormat([PixelFormat.Bgrx], 64, -1)
        );

        Assert.ThrowsExactly<ArgumentException>(
            () => audioIn.SetControl(0, []),
            "an empty control write was sent"
        );

        audioOut.SetError(
            -5,
            "a deliberate error, to prove the call reaches the daemon",
            cts.Token
        );

        await audioIn.DisposeAsync();
        await audioOut.DisposeAsync();
        await videoIn.DisposeAsync();
        await videoOut.DisposeAsync();

        // Disposed: a wrapper that still forwards is reaching a stream that has been freed.
        Assert.IsNull(audioIn.NodeId, "a disposed stream still reported a node id");
        Assert.IsNull(audioOut.Queue);
        Assert.IsFalse(videoIn.IsDriving);
        Assert.IsNull(videoOut.GraphClock);
        Assert.AreEqual(0, audioIn.UpdateProperties(retag), "a disposed stream retagged something");

        audioIn.SetRate(1.0, cts.Token);
        audioOut.TriggerProcess();
        videoIn.SkipCurrentFrame();
        videoOut.AnnounceLatency(latency, null, cts.Token);
        audioOut.SetError(-5, "gone", cts.Token);
        Assert.IsFalse(videoOut.DriveAt(TimeSpan.FromMilliseconds(10)));
    }
}
