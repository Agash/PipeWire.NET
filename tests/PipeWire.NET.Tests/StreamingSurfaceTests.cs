using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The parts of the streaming surface that only mean anything against a live graph: what the
/// daemon reports back, what it accepts, and what it does when asked to change mid-stream.
/// </summary>
/// <remarks>
/// These exist because the interesting failures are not compile errors. A queue depth that always
/// reads zero, a retained frame with no timestamp, a property update the daemon quietly drops - each
/// looks like working code and produces a stream nobody can synchronise.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[TestCategory("RequiresGStreamer")]
[SupportedOSPlatform("linux")]
public sealed class StreamingSurfaceTests
{
    private const string Pipeline =
        "videotestsrc is-live=true pattern=smpte ! video/x-raw,format=BGRA,width=320,height=240,framerate=30/1";

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<(PipeWireContext Ctx, GstTestSource Src)> SourceAsync(string name)
    {
        GstTestSource.RequireGStreamer();
        var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync();
        GstTestSource src = await GstTestSource.StartAsync(
            ctx, name + "-src", Pipeline, mediaClass: "Video/Source");
        return (ctx, src);
    }

    /// <summary>
    /// A retained frame carries the timestamp a consumer aligns on and the fourcc an importer binds
    /// with. Retention producing frames without those would be pointless.
    /// </summary>
    [TestMethod]
    public async Task AnOwnedFrame_CarriesWhatAConsumerPullsItFor()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        (PipeWireContext ctx, GstTestSource src) = await SourceAsync("pwnet-surface-owned");
        await using (ctx)
        await using (src)
        {
            await using var cap = new PipeWireVideoCapture(ctx, "pwnet-surface-owned-sink");
            cap.Retention = FrameRetention.Owned;
            cap.Connect(src.NodeId);
            await cap.WaitForStreamingAsync(cts.Token);

            PulledVideoFrame? frame = null;
            for (int i = 0; i < 100; i++)
            {
                if (cap.TryGetFrame(out frame)) break;
                await Task.Delay(50, cts.Token);
            }

            Assert.IsNotNull(frame, "retention was on but nothing was ever retained");
            using (frame)
            {
                Assert.AreEqual(320, frame.Width);
                Assert.AreEqual(240, frame.Height);
                Assert.AreNotEqual(0UL, frame.SequenceNumber);
                Assert.AreNotEqual(
                    DrmFormat.Invalid, frame.DrmFourcc, "a frame with no fourcc cannot be imported");
            }

            // Taking clears it, so the same frame must not come back a second time.
            bool twice = cap.TryGetFrame(out PulledVideoFrame? again);
            again?.Dispose();
            Assert.IsFalse(twice, "a taken frame was handed out again");
        }
    }

    /// <summary>Borrowed retention allocates nothing, so it must still deliver a usable frame.</summary>
    [TestMethod]
    public async Task ABorrowedFrame_ArrivesAndDescribesItself()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        (PipeWireContext ctx, GstTestSource src) = await SourceAsync("pwnet-surface-borrow");
        await using (ctx)
        await using (src)
        {
            await using var cap = new PipeWireVideoCapture(ctx, "pwnet-surface-borrow-sink");
            cap.Retention = FrameRetention.Borrowed;
            cap.Connect(src.NodeId);
            await cap.WaitForStreamingAsync(cts.Token);

            BorrowedVideoFrame frame = default;
            bool got = false;
            for (int i = 0; i < 100 && !got; i++)
            {
                got = cap.TryGetBorrowedFrame(out frame);
                if (!got) await Task.Delay(50, cts.Token);
            }

            Assert.IsTrue(got, "borrowed retention was on but nothing was ever retained");
            Assert.AreEqual(320, frame.Width);
            Assert.AreEqual(240, frame.Height);
            Assert.AreNotEqual(DrmFormat.Invalid, frame.DrmFourcc);
            Assert.IsFalse(cap.TryGetBorrowedFrame(out _), "a taken frame was handed out again");
        }
    }

    /// <summary>
    /// The queue depth a rate controller reads. It has to answer while streaming: a null or
    /// permanently empty reading is what an error term computed from it would silently inherit.
    /// </summary>
    [TestMethod]
    public async Task AStreamingCapture_ReportsItsQueueAndClock()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        (PipeWireContext ctx, GstTestSource src) = await SourceAsync("pwnet-surface-queue");
        await using (ctx)
        await using (src)
        {
            var pts = new List<long>();
            await using var cap = new PipeWireVideoCapture(ctx, "pwnet-surface-queue-sink");
            cap.FrameReady += (_, f) =>
            {
                lock (pts) { if (pts.Count < 64 && f.PresentationTimestampNs is { } t) pts.Add(t); }
            };
            cap.Connect(src.NodeId);
            await cap.WaitForStreamingAsync(cts.Token);
            await Task.Delay(600, cts.Token);

            Assert.IsNotNull(cap.Queue, "a streaming stream must be able to report its queue");
            Assert.IsNotNull(cap.GraphClock, "io_changed should have delivered the position area");

            // Time has to be moving, but in the frames, not in the graph clock. This source is
            // GStreamer's pipewiresink driving a video graph, and it deliberately publishes no clock
            // for video ("skip update time for video ... The video buffers get timestamp from the
            // SPA_META_Header anyway", gstpipewiresink.c), so the graph clock legitimately stands
            // still here. What upstream provides for video is a per-frame header timestamp.
            long[] seen;
            lock (pts) seen = [.. pts];

            Assert.IsTrue(seen.Length > 1, $"only {seen.Length} frames carried a presentation time");
            Assert.IsTrue(
                seen[^1] > seen[0],
                $"the frames' presentation times did not advance while streaming ({seen[0]} then {seen[^1]})");

            // An ordinary consumer is not lazily scheduled, and if it were it would still have to
            // report a queue - the two answers must at least be consistent with each other.
            if (cap.IsLazy) Assert.IsNotNull(cap.Queue, "a lazy stream must still report its queue");
        }
    }

    /// <summary>Retagging a live stream, rather than tearing it down to rename it.</summary>
    [TestMethod]
    public async Task UpdatingPropertiesOnALiveStream_IsAccepted()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        (PipeWireContext ctx, GstTestSource src) = await SourceAsync("pwnet-surface-props");
        await using (ctx)
        await using (src)
        {
            await using var cap = new PipeWireVideoCapture(ctx, "pwnet-surface-props-sink");
            cap.Connect(src.NodeId);
            await cap.WaitForStreamingAsync(cts.Token);

            int changed = cap.UpdateProperties(new Dictionary<string, string>
            {
                ["media.name"] = "renamed-while-running",
            });

            Assert.IsTrue(changed > 0, "the daemon reported no property change");

            // Still delivering afterwards: a retag must not disturb the link. Counting frames
            // across the change is what shows that, where reading the queue only shows the object
            // is still alive.
            var after = 0;
            cap.FrameReady += (_, _) => Interlocked.Increment(ref after);
            await Task.Delay(500, cts.Token);

            Assert.IsTrue(
                Volatile.Read(ref after) > 0,
                "no frame arrived after the stream was retagged");
        }
    }

    /// <summary>
    /// Skipping returns the buffer unused. The stream has to survive it: a consumer dropping frames
    /// under load must not be dropping the connection with them.
    /// </summary>
    [TestMethod]
    public async Task SkippingFrames_DoesNotStopTheStream()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        (PipeWireContext ctx, GstTestSource src) = await SourceAsync("pwnet-surface-skip");
        await using (ctx)
        await using (src)
        {
            int seen = 0;
            await using var cap = new PipeWireVideoCapture(ctx, "pwnet-surface-skip-sink");
            cap.FrameReady += (s, _) =>
            {
                // Every other frame goes back unused.
                if (Interlocked.Increment(ref seen) % 2 == 0) s.SkipCurrentFrame();
            };

            cap.Connect(src.NodeId);
            await cap.WaitForStreamingAsync(cts.Token);
            await Task.Delay(700, cts.Token);

            Assert.IsTrue(Volatile.Read(ref seen) > 4, "too few frames arrived while skipping half");

            // And it is still delivering after all that skipping, which returning a buffer unused
            // must not disturb.
            int atRest = Volatile.Read(ref seen);
            await Task.Delay(400, cts.Token);

            Assert.IsTrue(
                Volatile.Read(ref seen) > atRest,
                "frames stopped arriving after buffers were skipped");
        }
    }

    /// <summary>
    /// Asking the producer for a different size mid-stream. What is pinned is that the offer goes
    /// out and the stream survives it; whether the peer accepts is the peer's business.
    /// </summary>
    [TestMethod]
    public async Task RequestingADifferentFormat_IsSentAndSurvived()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        (PipeWireContext ctx, GstTestSource src) = await SourceAsync("pwnet-surface-reneg");
        await using (ctx)
        await using (src)
        {
            await using var cap = new PipeWireVideoCapture(ctx, "pwnet-surface-reneg-sink");
            cap.Connect(src.NodeId);
            await cap.WaitForStreamingAsync(cts.Token);

            var afterReneg = 0;
            cap.FrameReady += (_, _) => Interlocked.Increment(ref afterReneg);

            PixelFormat[] formats = [PixelFormat.Bgra];
            Assert.IsTrue(
                cap.RequestFormat(formats, 160, 120), "the renegotiation offer was refused outright");

            await Task.Delay(600, cts.Token);

            Assert.IsTrue(
                Volatile.Read(ref afterReneg) > 0,
                "no frame arrived after a renegotiation request, so the stream did not survive it");
        }
    }

    /// <summary>A consumer that drives: the trigger and the driver role are both reachable.</summary>
    /// <summary>
    /// A MemFd frame maps to its own pixels at its offset, the way upstream maps it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not page alignment: buffer.h documents mapoffset as page aligned, but upstream's shared pool
    /// sets it to each buffer's offset inside one memfd (buffers.c), so 64 or 8320 are normal. What a
    /// consumer relies on is that mapping the fd the way pw_map_range_init does - round the offset
    /// down to the page, then step in by the remainder - lands on the frame's own pixels.
    /// </para>
    /// <para>
    /// Fed by GStreamer because that is where MemFd frames come from. pipewiresink offers MemFd only
    /// and allocates the memfds itself (gstpipewiresink.c); this library's producer offers MemPtr,
    /// and buffers.c picks MemPtr whenever both sides allow it, so a pool between two of this
    /// library's streams is never MemFd and cannot exercise this path.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AMemFdFrame_MapsToItsOwnPixelsAtItsOffset()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        (PipeWireContext ctx, GstTestSource src) = await SourceAsync("pwnet-surface-memfd");
        await using (ctx)
        await using (src)
        {
            var mapped = 0;
            var other = 0;
            var failures = new List<string>();

            await using var cap = new PipeWireVideoCapture(ctx, "pwnet-surface-memfd-sink");
            cap.FrameReady += (_, f) =>
            {
                if (f.Pixels.IsEmpty) return;
                if (f.BufferType != PipeWireBufferType.MemFd || f.Fd < 0)
                {
                    Interlocked.Increment(ref other);
                    return;
                }

                string? wrong = GraphSurfaceTests.CompareMappedPixels(f);
                if (wrong is null) Interlocked.Increment(ref mapped);
                else lock (failures) failures.Add(wrong);
            };

            cap.Connect(src.NodeId);
            await cap.WaitForStreamingAsync(cts.Token);
            await Task.Delay(1000, cts.Token);

            lock (failures) Assert.AreEqual(0, failures.Count, string.Join("; ", failures.Distinct()));
            Assert.IsTrue(
                Volatile.Read(ref mapped) > 5,
                $"only {mapped} MemFd frames were mapped ({other} frames of another type), so GStreamer's memfd path was not exercised");
        }
    }

    [TestMethod]
    public async Task APullModeCapture_CanTriggerItsOwnCycles()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        (PipeWireContext ctx, GstTestSource src) = await SourceAsync("pwnet-surface-pull");
        await using (ctx)
        await using (src)
        {
            var commands = new ConcurrentBag<SpaNodeCommand>();
            int frames = 0;

            await using var cap = new PipeWireVideoCapture(ctx, "pwnet-surface-pull-sink");

            // Subscribed before connecting on purpose: that ordering silently failed when the hook
            // was installed on subscription rather than at core creation.
            cap.CommandReceived += commands.Add;
            cap.FrameReady += (_, _) => Interlocked.Increment(ref frames);

            cap.Connect(src.NodeId, pullMode: true);
            await cap.WaitForStreamingAsync(cts.Token);

            // Pulled by hand, the way upstream's pull example does from a timer.
            for (int i = 0; i < 10; i++)
            {
                cap.TriggerProcess();
                await Task.Delay(40, cts.Token);
            }

            Assert.IsTrue(Volatile.Read(ref frames) > 0, "no frames arrived while pulling");

            // The subscription above collected commands and nothing looked at them, which meant the
            // dispatch could have delivered nothing at all and this still passed. A stream that
            // reached Streaming has been commanded to start.
            SpaNodeCommand[] seen = [.. commands];
            Assert.IsTrue(
                seen.Contains(SpaNodeCommand.Start),
                "a streaming pull-mode capture reported no Start command; got: "
                + string.Join(", ", seen.Select(c => c.ToString()).Distinct()));
        }
    }
}
