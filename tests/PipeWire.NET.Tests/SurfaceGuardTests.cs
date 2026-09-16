using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// What every entry point does with an argument it cannot use, and with nothing to do.
/// </summary>
/// <remarks>
/// <para>
/// These need the runtime but not a daemon: every assertion here is refused before anything reaches
/// the graph. That is deliberate - a guard that only holds once connected is not a guard - and it
/// means the checks run in the no-daemon leg as well. The one exception carries its own category.
/// </para>
/// <para>
/// The value is in the refusal being the documented one. A method that throws
/// <c>NullReferenceException</c> where it promised <c>ArgumentNullException</c> is a contract break
/// that no happy-path test sees.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[SupportedOSPlatform("linux")]
public sealed class SurfaceGuardTests : PipeWireTestBase
{
    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static PipeWireContext Unstarted() =>
        new("pwnet-guards", ConsoleTestLoggerFactory.Instance);

    /// <summary>Every stream refuses a null error message rather than dereferencing it.</summary>
    [TestMethod]
    public async Task SetError_RefusesANullMessage()
    {
        RequireLinux();
        await using PipeWireContext ctx = Unstarted();

        await using var audioIn = new PipeWireAudioCapture(ctx, "pwnet_guard_ai");
        await using var audioOut = new PipeWireAudioOutput(ctx, "pwnet_guard_ao");
        await using var videoIn = new PipeWireVideoCapture(ctx, "pwnet_guard_vi");
        await using var videoOut = new PipeWireVideoOutput(ctx, "pwnet_guard_vo", 64, 64);

        Assert.ThrowsExactly<ArgumentNullException>(() => audioIn.SetError(-5, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => audioOut.SetError(-5, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => videoIn.SetError(-5, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => videoOut.SetError(-5, null!));
    }

    /// <summary>An unconnected stream reports no faults rather than throwing.</summary>
    /// <remarks>
    /// The fault properties are read from a host's own loop, including before anything is connected
    /// and after disposal, so they have to answer rather than fail.
    /// </remarks>
    [TestMethod]
    public async Task FaultProperties_AnswerBeforeConnectAndAfterDisposal()
    {
        RequireLinux();
        await using PipeWireContext ctx = Unstarted();

        var output = new PipeWireAudioOutput(ctx, "pwnet_guard_faults");

        Assert.IsNull(output.LastProcessError);
        Assert.AreEqual(0, output.ProcessErrorCount);

        output.Dispose();

        Assert.IsNull(output.LastProcessError, "a disposed stream threw rather than reporting no fault");
        Assert.AreEqual(0, output.ProcessErrorCount);
    }

    /// <summary>Both outputs refuse a null node, as both captures do.</summary>
    [TestMethod]
    public async Task Connect_RefusesANullNode()
    {
        RequireLinux();
        await using PipeWireContext ctx = Unstarted();

        await using var audioOut = new PipeWireAudioOutput(ctx, "pwnet_guard_cao");
        await using var videoOut = new PipeWireVideoOutput(ctx, "pwnet_guard_cvo", 64, 64);
        await using var audioIn = new PipeWireAudioCapture(ctx, "pwnet_guard_cai");
        await using var videoIn = new PipeWireVideoCapture(ctx, "pwnet_guard_cvi");

        Assert.ThrowsExactly<ArgumentNullException>(() => audioOut.Connect((PipeWireNode)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => videoOut.Connect((PipeWireNode)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => audioIn.Connect((PipeWireNode)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => videoIn.Connect((PipeWireNode)null!));
    }

    /// <summary>Retagging a connection refuses null and does nothing with an empty set.</summary>
    /// <remarks>
    /// "Nothing to do" returning 0 rather than reaching the daemon is what keeps a caller's own
    /// no-op cheap; the count is the daemon's answer everywhere else.
    /// </remarks>
    [TestMethod]
    public async Task UpdateProperties_RefusesNullAndIgnoresAnEmptySet()
    {
        RequireLinux();
        await using PipeWireContext ctx = Unstarted();

        Assert.ThrowsExactly<ArgumentNullException>(() => ctx.UpdateProperties(null!));

        Assert.AreEqual(
            0, ctx.UpdateProperties(new Dictionary<string, string>()),
            "an empty set reported a change");
    }

    /// <summary>An exported node can raise an xrun, and stops being able to once disposed.</summary>
    /// <remarks>
    /// The graph installs the callbacks when the node is exported, not when it is first scheduled,
    /// so a node that has never run a cycle can still report one. After disposal the table is gone
    /// and the answer is <see langword="false"/> rather than a call through freed memory - a report
    /// from a teardown path is legal, so refusing it has to be a return value, not a throw.
    /// </remarks>
    [TestMethod]
    [TestCategory("RequiresDaemon")]
    public async Task ReportXrun_AnswersWhileExportedAndRefusesAfterDisposal()
    {
        RequireLinux();
        await using PipeWireContext ctx = Unstarted();

        await ctx.StartAsync(TestContext.CancellationTokenSource.Token).ConfigureAwait(false);

        PipeWireNodeProvider node = PipeWireNodeProvider.Create(
            ctx, "pwnet_guard_xrun", PipeWireExportedFormat.AudioF32(48000, 1));

        Assert.IsTrue(
            node.ReportXrun(1000, 100),
            "an exported node could not report an xrun to the graph that scheduled it");

        node.Dispose();

        Assert.IsFalse(
            node.ReportXrun(1000, 100),
            "a disposed node called through a callback table it no longer owns");
    }

    /// <summary>Every member of every stream answers on an instance that was never connected.</summary>
    /// <remarks>
    /// <para>
    /// A stream is constructed long before it is connected, and hosts read these from a settings
    /// page or a status panel that is up the whole time. Each one is either a reading that means
    /// "nothing yet" or an order that has nowhere to go, and neither is a caller mistake, so the
    /// answer has to be a value rather than a throw.
    /// </para>
    /// <para>
    /// The exceptions are the three that cannot answer honestly without a stream - the two waits
    /// and the control write - which say so with <see cref="InvalidOperationException"/>. Those are
    /// asserted here too, because "answers" and "refuses clearly" are the same contract seen from
    /// either side; what neither may do is dereference nothing.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task EveryStreamMember_AnswersBeforeConnect()
    {
        RequireLinux();
        await using PipeWireContext ctx = Unstarted();

        var latency = new PipeWireLatency(SpaDirection.Output, 0, 0, 48000, 48000, 0, 10_000_000);
        var retag = new Dictionary<string, string> { ["media.name"] = "guarded" };

        await using var audioIn = new PipeWireAudioCapture(ctx, "pwnet_guard_sai");
        Shared(audioIn.NodeId, audioIn.LastProcessError, audioIn.ProcessErrorCount,
            audioIn.IsDriving, audioIn.Queue, audioIn.IsLazy, audioIn.GraphClock,
            audioIn.RateMatch, audioIn.Controls, audioIn.GetControl(0));
        audioIn.SkipCurrentFrame();
        audioIn.TriggerProcess();
        audioIn.SetError(-5, "no stream");
        audioIn.SetRate(1.0);
        audioIn.AnnounceLatency(latency);
        Assert.AreEqual(0, audioIn.UpdateProperties(retag));
        await audioIn.TriggerProcessAndWaitAsync();
        await Refuses(() => audioIn.WaitForStreamingAsync());
        await Refuses(() => audioIn.WaitForNodeIdAsync());
        Assert.ThrowsExactly<InvalidOperationException>(() => audioIn.SetControl(0, [1f]));

        await using var audioOut = new PipeWireAudioOutput(ctx, "pwnet_guard_sao");
        Shared(audioOut.NodeId, audioOut.LastProcessError, audioOut.ProcessErrorCount,
            audioOut.IsDriving, audioOut.Queue, audioOut.IsLazy, audioOut.GraphClock,
            audioOut.RateMatch, audioOut.Controls, audioOut.GetControl(0));
        audioOut.TriggerProcess();
        audioOut.SetError(-5, "no stream");
        audioOut.SetRate(1.0);
        audioOut.AnnounceLatency(latency);
        Assert.IsFalse(audioOut.DriveAt(TimeSpan.FromMilliseconds(10)));
        Assert.AreEqual(0, audioOut.UpdateProperties(retag));
        await audioOut.TriggerProcessAndWaitAsync();
        await audioOut.DrainAsync();
        await Refuses(() => audioOut.WaitForStreamingAsync());
        await Refuses(() => audioOut.WaitForNodeIdAsync());
        Assert.ThrowsExactly<InvalidOperationException>(() => audioOut.SetControl(0, [1f]));

        await using var videoIn = new PipeWireVideoCapture(ctx, "pwnet_guard_svi");
        Shared(videoIn.NodeId, videoIn.LastProcessError, videoIn.ProcessErrorCount,
            videoIn.IsDriving, videoIn.Queue, videoIn.IsLazy, videoIn.GraphClock,
            videoIn.RateMatch, videoIn.Controls, videoIn.GetControl(0));
        videoIn.SkipCurrentFrame();
        videoIn.TriggerProcess();
        videoIn.SetError(-5, "no stream");
        Assert.AreEqual(0, videoIn.UpdateProperties(retag));
        Assert.IsFalse(videoIn.RequestFormat([PixelFormat.Bgrx], 64, 64));
        Assert.IsFalse(videoIn.TryGetFrame(out PulledVideoFrame? pulled));
        Assert.IsNull(pulled);
        await videoIn.TriggerProcessAndWaitAsync();
        await Refuses(() => videoIn.WaitForStreamingAsync());
        await Refuses(() => videoIn.WaitForNodeIdAsync());
        Assert.ThrowsExactly<InvalidOperationException>(() => videoIn.SetControl(0, [1f]));

        await using var videoOut = new PipeWireVideoOutput(ctx, "pwnet_guard_svo", 64, 64);
        Shared(videoOut.NodeId, videoOut.LastProcessError, videoOut.ProcessErrorCount,
            videoOut.IsDriving, videoOut.Queue, videoOut.IsLazy, videoOut.GraphClock,
            videoOut.RateMatch, videoOut.Controls, videoOut.GetControl(0));
        videoOut.TriggerProcess();
        videoOut.SetError(-5, "no stream");
        videoOut.SetRate(1.0);
        videoOut.AnnounceLatency(latency);
        Assert.IsFalse(videoOut.DriveAt(TimeSpan.FromMilliseconds(10)));
        Assert.AreEqual(0, videoOut.UpdateProperties(retag));
        Assert.IsFalse(videoOut.RequestFormat([PixelFormat.Bgrx], 64, 64));
        await videoOut.TriggerProcessAndWaitAsync();
        await videoOut.DrainAsync();
        await Refuses(() => videoOut.WaitForStreamingAsync());
        await Refuses(() => videoOut.WaitForNodeIdAsync());
        Assert.ThrowsExactly<InvalidOperationException>(() => videoOut.SetControl(0, [1f]));

        static void Shared(
            uint? nodeId, Exception? lastError, long errorCount, bool driving,
            PipeWireStreamQueue? queue, bool lazy, PipeWireGraphClock? clock,
            PipeWireRateMatch? rateMatch, ImmutableArray<PipeWireStreamControl> controls,
            PipeWireStreamControl? control)
        {
            Assert.IsNull(nodeId, "an unconnected stream claimed a node id");
            Assert.IsNull(lastError);
            Assert.AreEqual(0, errorCount);
            Assert.IsFalse(driving, "an unconnected stream claimed to drive the graph");
            Assert.IsNull(queue);
            Assert.IsFalse(lazy);
            Assert.IsNull(clock);
            Assert.IsNull(rateMatch);
            Assert.IsTrue(controls.IsEmpty);
            Assert.IsNull(control);
        }

        static Task Refuses(Func<Task> call) =>
            Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await call());
    }

    /// <summary>A filter refuses everything that needs a connection until it has one.</summary>
    /// <remarks>
    /// The split matters: a reading answers "nothing yet", an order refuses. Both come off the same
    /// <c>_connected</c> flag, so the pair is what proves the flag is actually consulted rather than
    /// the call reaching a handle that is not there.
    /// </remarks>
    [TestMethod]
    [TestCategory("RequiresDaemon")]
    public async Task AFilter_RefusesOrdersBeforeItIsConnected()
    {
        RequireLinux();
        await using PipeWireContext ctx = Unstarted();

        // A filter is built on the context's loop, so an unstarted context has nothing to build on.
        Assert.ThrowsExactly<InvalidOperationException>(
            () => PipeWireFilter.Create(ctx, "pwnet_guard_too_early"));

        await ctx.StartAsync(TestContext.CancellationTokenSource.Token).ConfigureAwait(false);

        using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_guard_filter");

        Assert.IsNull(filter.NodeId, "an unconnected filter claimed a node id");
        Assert.IsFalse(filter.IsDriving, "an unconnected filter claimed to drive the graph");
        Assert.AreEqual(PipeWireFilterState.Unconnected, filter.State);
        Assert.IsNull(filter.LastProcessError);
        Assert.AreEqual(0, filter.ProcessErrorCount);
        Assert.AreEqual(0, filter.Ports.Count);

        Assert.ThrowsExactly<InvalidOperationException>(() => filter.WaitForNodeIdAsync());
        Assert.ThrowsExactly<InvalidOperationException>(() => filter.SetError(-5, "not connected"));
        Assert.ThrowsExactly<InvalidOperationException>(filter.TriggerProcess);
        Assert.ThrowsExactly<InvalidOperationException>(() => filter.SetActive(true));
        Assert.ThrowsExactly<ArgumentNullException>(() => filter.SetError(-5, null!));
    }

    /// <summary>A port refuses the accessors that belong to a different kind of port.</summary>
    /// <remarks>
    /// Every port is the same type and the format decides what it can do, so this is the only thing
    /// stopping a caller reading MIDI out of an audio buffer. It has to be the refusal, not the
    /// reinterpretation.
    /// </remarks>
    [TestMethod]
    [TestCategory("RequiresDaemon")]
    public async Task APort_RefusesTheAccessorsOfAnotherFormat()
    {
        RequireLinux();
        await using PipeWireContext ctx = Unstarted();
        await ctx.StartAsync(TestContext.CancellationTokenSource.Token).ConfigureAwait(false);

        using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_guard_ports");

        PipeWireFilterPort audio = filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");
        PipeWireFilterPort midi = filter.AddMidiPort(PipeWirePortDirection.Out, "events");
        PipeWireFilterPort video = filter.AddVideoPort(PipeWirePortDirection.Out, "frames");

        Assert.AreEqual(3, filter.Ports.Count);

        Assert.ThrowsExactly<InvalidOperationException>(() => audio.GetPixels(16, 16));
        Assert.ThrowsExactly<InvalidOperationException>(() => audio.ReadEvents());
        Assert.ThrowsExactly<InvalidOperationException>(() => audio.WriteEvents([]));

        Assert.ThrowsExactly<InvalidOperationException>(() => midi.GetPixels(16, 16));
        Assert.ThrowsExactly<InvalidOperationException>(() => video.ReadEvents());
        Assert.ThrowsExactly<InvalidOperationException>(() => video.WriteEvents([]));

        // A filter's own removal path, on a port it really owns, without a daemon in the way.
        filter.RemovePort(video);
        Assert.AreEqual(2, filter.Ports.Count);
    }

    /// <summary>The context refuses what it cannot do while it is not connected.</summary>
    [TestMethod]
    public async Task AContext_RefusesConnectionWorkBeforeItStarts()
    {
        RequireLinux();
        PipeWireContext ctx = Unstarted();

        Assert.IsFalse(ctx.IsOnLoopThread, "an unstarted context claimed to be on its own loop");

        Assert.ThrowsExactly<InvalidOperationException>(
            () => ctx.UpdateProperties(new Dictionary<string, string> { ["application.name"] = "x" }),
            "an unconnected context sent properties nowhere and said nothing");

        await ctx.DisposeAsync();
        await ctx.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await ctx.StartAsync(TestContext.CancellationTokenSource.Token),
            "a disposed context was started again");
    }

    /// <summary>The dmabuf entry points refuse what they cannot negotiate, and leave no residue.</summary>
    /// <remarks>
    /// <para>
    /// The sync forms set explicit-sync mode before delegating, so a refusal partway has to put it
    /// back. If it does not, the next connect on the same instance silently negotiates sync
    /// timelines nobody asked for, and every buffer is then declined at allocation - far from the
    /// call that actually went wrong.
    /// </para>
    /// <para>
    /// The stamp guard is the other half: buffer indices come back from handlers, and an index
    /// outside the pool would write past the array that tracks pending points.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task TheDmaBufEntryPoints_RefuseBadOffersAndLeaveNoResidue()
    {
        RequireLinux();
        await using PipeWireContext ctx = Unstarted();

        await using var output = new PipeWireVideoOutput(ctx, "pwnet_guard_dmabuf", 64, 64);

        Assert.ThrowsExactly<ArgumentException>(
            () => output.ConnectDmaBuf(ReadOnlySpan<long>.Empty),
            "a dmabuf connect with no modifiers was allowed to negotiate");

        Assert.ThrowsExactly<ArgumentException>(
            () => output.ConnectDmaBuf(ReadOnlySpan<DmaBufDeviceOffer>.Empty));

        // The same refusal reached through the sync form, which has state to unwind on the way out.
        Assert.ThrowsExactly<ArgumentException>(
            () => output.ConnectDmaBufSync(ReadOnlySpan<long>.Empty));

        Assert.ThrowsExactly<ArgumentException>(
            () => output.ConnectDmaBufSync(ReadOnlySpan<DmaBufDeviceOffer>.Empty));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => output.StampSyncPoints(-1, 1, 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => output.StampSyncPoints(4096, 1, 2));

        // A stamp inside the pool is accepted even before a buffer exists: the point is taken by
        // the next publish of that buffer, whenever the pool is built.
        output.StampSyncPoints(0, 1, 2);
    }

    /// <summary>A stream that is already connected refuses a second connect rather than leaking the first.</summary>
    /// <remarks>
    /// Each of these owns a native stream, and a second connect over the top would drop the handle
    /// the daemon still holds. Refusing is what makes the double call a caller bug rather than a
    /// leak that only shows up as an orphaned node in the graph.
    /// </remarks>
    [TestMethod]
    [TestCategory("RequiresDaemon")]
    public async Task AConnectedStream_RefusesASecondConnect()
    {
        RequireLinux();
        await using PipeWireContext ctx = Unstarted();
        await ctx.StartAsync(TestContext.CancellationTokenSource.Token).ConfigureAwait(false);

        await using var videoOut = new PipeWireVideoOutput(ctx, "pwnet_guard_twice_vo", 64, 64);
        videoOut.FillFrame += (_, pixels, _, _, _, _) => { pixels.Clear(); return true; };
        videoOut.Connect(autoConnect: false);

        Assert.ThrowsExactly<InvalidOperationException>(() => videoOut.Connect(autoConnect: false));
        Assert.ThrowsExactly<InvalidOperationException>(() => videoOut.ConnectDmaBuf([0L]));

        await using var audioOut = new PipeWireAudioOutput(ctx, "pwnet_guard_twice_ao");
        audioOut.FillSamples += (_, samples, _, _, _) => { samples.Clear(); return samples.Length; };
        audioOut.Connect(autoConnect: false);

        Assert.ThrowsExactly<InvalidOperationException>(() => audioOut.Connect(autoConnect: false));

        await using var audioIn = new PipeWireAudioCapture(ctx, "pwnet_guard_twice_ai");
        audioIn.Connect(autoConnect: false);

        Assert.ThrowsExactly<InvalidOperationException>(() => audioIn.Connect(autoConnect: false));

        await using var videoIn = new PipeWireVideoCapture(ctx, "pwnet_guard_twice_vi");
        videoIn.Connect(autoConnect: false);

        Assert.ThrowsExactly<InvalidOperationException>(() => videoIn.Connect(autoConnect: false));
    }

    /// <summary>A DSP port hands back nothing rather than a span past the end of its buffer.</summary>
    /// <remarks>
    /// The size comes from the caller, because a filter with several video ports reads the cycle
    /// geometry once and sizes each port from it. So a caller asking for more than the graph
    /// allocated is an ordinary mistake, and the answer is an empty span with the chunk marked
    /// empty - never a span over memory the port does not own.
    /// </remarks>
    [TestMethod]
    [TestCategory("RequiresDaemon")]
    public async Task ADspPort_RefusesToHandBackMoreThanItsBufferHolds()
    {
        RequireLinux();
        await using PipeWireContext ctx = Unstarted();
        await ctx.StartAsync(TestContext.CancellationTokenSource.Token).ConfigureAwait(false);

        using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_guard_dsp");
        PipeWireFilterPort video = filter.AddVideoPort(PipeWirePortDirection.Out, "frames");
        PipeWireFilterPort midi = filter.AddMidiPort(PipeWirePortDirection.Out, "events");

        await filter.ConnectAsync(PipeWireFilterFlags.RtProcess, TestContext.CancellationTokenSource.Token);

        // Outside a cycle there is no buffer to dequeue at all, which is the same answer by a
        // different route and is what a caller reading the geometry too early actually hits.
        Assert.IsTrue(video.GetPixels(16, 16).IsEmpty);
        Assert.IsTrue(
            video.GetPixels(16384, 16384).IsEmpty,
            "a port handed back a span for a frame far larger than any buffer it has");

        Assert.IsNull(midi.ReadEvents());
        Assert.IsFalse(midi.WriteEvents([]));
    }

    /// <summary>Metadata refuses a NUL in any field rather than writing a truncated one.</summary>
    /// <remarks>
    /// <para>
    /// The daemon reads these as C strings, so a NUL inside one does not fail: it silently ends the
    /// value there, and the rest lands as whatever the next field happens to be. Refusing locally is
    /// the only place that can be caught.
    /// </para>
    /// <para>
    /// Against a store this test serves itself, not the daemon's `settings`. Writing a key into a
    /// store the session manager shares is not a test's business - it is live configuration that
    /// other clients act on - and serving one costs a single call.
    /// </para>
    /// </remarks>
    [TestMethod]
    [TestCategory("RequiresDaemon")]
    public async Task AMetadataWrite_RefusesANulInAnyField()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Two connections, because a store cannot be bound through the connection that serves it -
        // the daemon would wait on an answer from a client that has stopped reading. That refusal
        // is the library's own, and it is what this shape exists to respect.
        await using PipeWireContext serverCtx = Unstarted();
        await serverCtx.StartAsync(cts.Token).ConfigureAwait(false);

        await using var readerCtx = new PipeWireContext("pwnet-guards-reader", ConsoleTestLoggerFactory.Instance);
        await readerCtx.StartAsync(cts.Token).ConfigureAwait(false);

        await using var reg = new PipeWireRegistry(readerCtx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        string storeName = $"pwnet-guard-store-{Environment.ProcessId}-{Random.Shared.Next():x}";

        await using PipeWireMetadataProvider provider =
            PipeWireMetadataProvider.Create(serverCtx, storeName, export: true);

        await provider.ReadyAsync(cts.Token);

        PipeWireMetadataProxy? store = null;
        for (var i = 0; i < 80 && store is null; i++)
        {
            await reg.WaitForInitialEnumerationAsync(cts.Token);
            store = reg.BindMetadata(storeName);
            if (store is null) await Task.Delay(50, cts.Token);
        }

        if (store is null) Assert.Inconclusive("the exported store never came back through the registry.");

        await using (store)
        {
            await store!.ReadyAsync(cts.Token);

            await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await store.SetAsync("pwnet\0key", "v", null, cancellationToken: cts.Token));

            await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await store.SetAsync("pwnet.key", "v\0v", null, cancellationToken: cts.Token));

            await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await store.SetAsync("pwnet.key", "v", "t\0t", cancellationToken: cts.Token));

            // A key the store does not have reads as absent rather than as a default.
            Assert.IsNull(store.Get("pwnet.no.such.key"));

            // The write path, safe now that the store is ours: a value that is not a number reads
            // back as itself, and the typed readers answer null rather than zero for it, because
            // zero is a rate a caller would act on.
            await store.SetAsync("clock.rate", "banana", null, cancellationToken: cts.Token);

            for (var i = 0; i < 100 && store.Get("clock.rate") is null; i++)
                await Task.Delay(50, cts.Token);

            Assert.AreEqual("banana", store.Get("clock.rate"), "the write never came back");
            Assert.IsNull(store.ClockRate, "a value that is not a number was read as one");

            // Clearing a key removes it rather than leaving an empty string behind.
            await store.SetAsync("clock.rate", null, null, cancellationToken: cts.Token);

            for (var i = 0; i < 100 && store.Get("clock.rate") is not null; i++)
                await Task.Delay(50, cts.Token);

            Assert.IsNull(store.Get("clock.rate"), "a cleared key was left in the store");
        }
    }
}
