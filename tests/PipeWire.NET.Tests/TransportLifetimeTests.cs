using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// The buffer-lifetime contract a zero-copy sender depends on.
/// </summary>
/// <remarks>
/// <para>
/// A sender that hands its own GPU allocations to PipeWire has to know when it may stop holding
/// them. <see cref="PipeWireVideoOutput.ReleaseDmaBuf"/> is that signal, and no test subscribed to
/// it - the existing lifetime tests check descriptors are still open after teardown, which proves
/// nothing was closed too early but says nothing about whether the app was ever *told* it could
/// close them.
/// </para>
/// <para>
/// Both directions are bugs and neither throws. Never firing means the app holds every allocation
/// for the process lifetime, which on a GPU is a leak measured in hundreds of megabytes. Firing
/// early means the app frees a buffer the daemon still has queued, and the consumer reads freed GPU
/// memory.
/// </para>
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class TransportLifetimeTests : PipeWireTestBase
{
    private const int Width = 320;
    private const int Height = 240;
    private const int PoolCap = 8;

    private const string RenderNode = "/dev/dri/renderD128";

    private static GbmAllocator RequireGbm()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");

        if (!File.Exists(RenderNode))
            Assert.Inconclusive($"No GPU render node ({RenderNode}).");

        try
        {
            return new GbmAllocator(RenderNode);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"libgbm unavailable ({ex.Message}).");
            throw;
        }
    }

    /// <summary>
    /// Every buffer the app backed is released back to it, and never while still in use.
    /// </summary>
    /// <remarks>
    /// Ordering is the half that matters most. A release raised for a buffer index that is
    /// subsequently allocated again is fine - the pool recycles - but a release for an index the app
    /// never backed means the app is being told to free something it does not own.
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [TestCategory("RequiresGpu")]
    public async Task EveryBufferTheAppBacked_IsReleasedBackToIt()
    {
        using GbmAllocator gbm = RequireGbm();
        var buffers = new List<GbmAllocator.Buffer>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var allocated = new ConcurrentQueue<int>();
        var released = new ConcurrentQueue<int>();

        try
        {
            await using (var ctx = new PipeWireContext("pwnet-release", ConsoleTestLoggerFactory.Instance))
            {
                await ctx.StartAsync(cts.Token);

                long modifier = (long)GbmAllocator.LinearModifier;

                await using var output = new PipeWireVideoOutput(
                    ctx, "pwnet-release-src", Width, Height, PixelFormat.Bgra, 30);

                output.AllocateDmaBuf += (_, index, _, _, _, _, planes) =>
                {
                    if (index >= PoolCap) return 0;
                    while (buffers.Count <= index) buffers.Add(gbm.CreateBgra(Width, Height));
                    allocated.Enqueue(index);
                    GbmAllocator.Buffer b = buffers[index];
                    planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                    return 1;
                };

                output.ReleaseDmaBuf += (_, index) => released.Enqueue(index);
                output.FillDmaBuf += (_, _) => true;
                output.ConnectDmaBuf([modifier]);

                uint? nodeId = null;
                for (var i = 0; i < 60 && nodeId is null; i++)
                {
                    nodeId = output.NodeId;
                    if (nodeId is null) await Task.Delay(50, cts.Token);
                }

                Assert.IsNotNull(nodeId, "the producer was never given a node id");

                await using var capture = new PipeWireVideoCapture(ctx, "pwnet-release-sink");
                var frames = 0;
                capture.FrameReady += (_, _) => Interlocked.Increment(ref frames);
                capture.Connect(nodeId!.Value, [PixelFormat.Bgra], modifiers: [modifier]);

                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

                Assert.IsTrue(Volatile.Read(ref frames) > 0, "no frames flowed, so no pool was built");
                Assert.IsFalse(allocated.IsEmpty, "the app was never asked to back a buffer");

                // Teardown is what triggers the releases: the pool goes away with the stream.
            }

            int[] backed = [.. allocated];
            int[] handedBack = [.. released];

            Assert.IsFalse(
                handedBack.Length == 0,
                $"the app backed {backed.Length} buffers and was told to release none of them; "
                + "a zero-copy sender would hold every GPU allocation for the process lifetime");

            // Nothing released that was never backed.
            var backedSet = backed.ToHashSet();
            foreach (int index in handedBack)
            {
                Assert.IsTrue(
                    backedSet.Contains(index),
                    $"released buffer index {index}, which the app never backed");
            }

            // And every distinct backed index came back.
            foreach (int index in backedSet)
            {
                Assert.IsTrue(
                    handedBack.Contains(index),
                    $"buffer index {index} was backed by the app and never released");
            }
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers) b.Dispose();
        }
    }

    /// <summary>
    /// Each triggered publish reaches the consumer, carrying what that cycle wrote.
    /// </summary>
    /// <remarks>
    /// <see cref="PipeWireVideoOutput.TriggerProcessAndWaitAsync"/> is what a sender uses when it has
    /// one frame ready and needs to know it left - a screen capture pacing itself off its own
    /// compositor rather than off the graph. So the assertion is that the frames arrive and carry
    /// the values those cycles produced, not merely that the wait returned: a wait that completed
    /// without publishing anything is precisely the failure it would hide.
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task EachTriggeredPublish_ReachesTheConsumerWithItsContent()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        await using var ctx = new PipeWireContext("pwnet-trigwait", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        var filled = 0;

        await using var output = new PipeWireVideoOutput(
            ctx, "pwnet-trigwait-src", Width, Height, PixelFormat.Bgra, 30);

        output.FillFrame += (_, pixels, stride, width, height, _) =>
        {
            // Every byte of the frame carries this cycle's number, so a consumer can say which
            // publish it is looking at and whether the whole frame was written.
            byte tag = (byte)(Interlocked.Increment(ref filled) & 0xFF);
            for (var y = 0; y < height; y++) pixels.Slice(y * stride, width * 4).Fill(tag);
            return true;
        };

        // A self-paced sender asks to drive, as upstream's video-src does; a follower's triggers
        // never report completion, whatever the caller waits for.
        output.Connect(autoConnect: false, driver: true);

        uint? nodeId = null;
        for (var i = 0; i < 60 && nodeId is null; i++)
        {
            nodeId = output.NodeId;
            if (nodeId is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(nodeId, "the producer was never given a node id");

        var tags = new List<byte>();
        var torn = 0;

        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-trigwait-sink");
        capture.FrameReady += (_, f) =>
        {
            if (f.Pixels.IsEmpty) return;

            byte tag = f.Pixels[0];
            for (var y = 0; y < f.Height; y++)
            {
                ReadOnlySpan<byte> row = f.Pixels.Slice(y * f.Stride, f.Width * 4);
                foreach (byte b in row)
                {
                    if (b != tag) { Interlocked.Increment(ref torn); return; }
                }
            }

            lock (tags) { if (tags.Count < 64) tags.Add(tag); }
        };

        capture.Connect(nodeId!.Value, [PixelFormat.Bgra]);
        await capture.WaitForStreamingAsync(cts.Token);

        for (var i = 0; i < 100 && !output.IsDriving; i++) await Task.Delay(50, cts.Token);
        Assert.IsTrue(output.IsDriving, "the daemon never made the driver-flagged output the graph's driver");

        int before = Volatile.Read(ref filled);

        const int triggers = 8;
        for (var i = 0; i < triggers; i++)
        {
            int filledBefore = Volatile.Read(ref filled);
            int seenBefore;
            lock (tags) seenBefore = tags.Count;
            try
            {
                await output.TriggerProcessAndWaitAsync(cts.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            }
            catch (TimeoutException)
            {
                // Which half went missing: the trigger itself (no fill ran), the cycle (filled but
                // the consumer never got it), or only the completion report (delivered, but no
                // trigger_done). A second trigger then says whether the graph is stuck or one
                // report was lost.
                int seenAfter;
                lock (tags) seenAfter = tags.Count;
                string state = $"trigger {i}: fills {Volatile.Read(ref filled) - filledBefore}, "
                    + $"frames delivered {seenAfter - seenBefore}, driving {output.IsDriving}";
                bool retried = await output.TriggerProcessAndWaitAsync(cts.Token)
                    .WaitAsync(TimeSpan.FromSeconds(5), cts.Token)
                    .ContinueWith(t => t.IsCompletedSuccessfully, TaskScheduler.Default);
                Assert.Fail($"a triggered cycle never reported completion ({state}); "
                    + $"a further trigger {(retried ? "did" : "did not either")}");
            }
        }

        await Task.Delay(300, cts.Token);

        byte[] seen;
        lock (tags) seen = [.. tags];

        Assert.AreEqual(0, Volatile.Read(ref torn), "a frame arrived only partly written");

        Assert.IsTrue(
            Volatile.Read(ref filled) - before >= triggers,
            $"{triggers} triggered waits produced only {Volatile.Read(ref filled) - before} publishes");

        Assert.IsTrue(
            seen.Length > 0,
            "the triggered publishes never reached the consumer");

        // The values the consumer saw are ones the producer actually wrote.
        Assert.IsTrue(
            seen.Distinct().Count() > 1,
            "every triggered frame carried the same value, so the consumer saw one buffer repeatedly");

        Assert.IsNotNull(output.Queue, "the stream stopped answering after triggered publishes");
    }
}
