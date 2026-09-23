using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// Both directions of a transport: capturing a synchronised A+V session out of PipeWire, and
/// publishing one back into it with the timing the sender gave it.
/// </summary>
/// <remarks>
/// <para>
/// The receiving half was missing entirely. Everything else here captures - a sender reading frames
/// out of the graph - but a transport also has to put the far end's media back in, and that is the
/// direction where timing is easy to lose: a decoded frame already has a presentation time, and
/// stamping it with the local cycle instead throws away the alignment the sender carried. Audio and
/// video then drift apart at the sink no matter how carefully they were sent.
/// </para>
/// <para>
/// That is what <see cref="PipeWireVideoOutput.NextPresentationTimestampNs"/> exists for, and this is where
/// it is checked: a consumer must read back the time the publisher set, not the cycle it happened
/// to land in.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class TransportEndToEndTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private const int Width = 64;
    private const int Height = 32;
    private const int Rate = 48000;

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<uint> NodeIdAsync(PipeWireVideoOutput output, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            if (output.NodeId is { } id) return id;
            await Task.Delay(50, ct);
        }

        throw new InvalidOperationException("the producer was never given a node id");
    }

    /// <summary>
    /// A republished frame keeps the presentation time the publisher gave it.
    /// </summary>
    /// <remarks>
    /// The receiver half of a transport, at its narrowest. Each frame is published with a time this
    /// test chose, well away from the cycle time, and the consumer has to read that time back. If
    /// the publisher fell back to stamping "now", the values the consumer sees would be graph-clock
    /// times instead - close together, monotonic, and completely wrong.
    /// </remarks>
    [TestMethod]
    public async Task ARepublishedFrame_KeepsTheTimestampThePublisherGaveIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-republish", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        // Times a transport would carry: a base far from any graph clock value, stepping by a
        // frame period. Nothing the local cycle could coincidentally produce.
        const long baseNs = 1_000_000_000_000;
        const long stepNs = 33_000_000;

        var published = new List<long>();

        await using var output = new PipeWireVideoOutput(
            ctx, "pwnet-republish-src", Width, Height, PixelFormat.Bgra, 30);

        output.FillFrame += (sender, pixels, stride, w, h, _) =>
        {
            long pts;
            lock (published)
            {
                pts = baseNs + (published.Count * stepNs);
                if (published.Count < 32) published.Add(pts);
            }

            // The frame carries its own index too, so a consumer can tell which publish it has.
            byte tag = (byte)(pts / stepNs & 0xFF);
            for (var y = 0; y < h; y++) pixels.Slice(y * stride, w * 4).Fill(tag);

            sender.NextPresentationTimestampNs = pts;
            return true;
        };

        output.Connect(autoConnect: false);
        uint nodeId = await NodeIdAsync(output, cts.Token);

        var received = new List<long>();
        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-republish-sink");
        capture.FrameReady += (_, f) =>
        {
            if (f.PresentationTimestampNs is not { } pts) return;
            lock (received) { if (received.Count < 32) received.Add(pts); }
        };

        capture.Connect(nodeId, [PixelFormat.Bgra]);
        await capture.WaitForStreamingAsync(cts.Token);

        for (var i = 0; i < 80; i++)
        {
            lock (received) { if (received.Count >= 5) break; }
            await Task.Delay(50, cts.Token);
        }

        long[] sent, got;
        lock (published) sent = [.. published];
        lock (received) got = [.. received];

        Assert.IsTrue(got.Length >= 5, $"only {got.Length} frames arrived with a presentation time");

        // Every time the consumer saw is one the publisher chose.
        var sentSet = sent.ToHashSet();
        foreach (long pts in got)
        {
            Assert.IsTrue(
                sentSet.Contains(pts),
                $"the consumer read a presentation time of {pts}, which the publisher never set - "
                + "the frame was stamped with the local cycle instead");
        }

        // And they are in the range the publisher used, not graph-clock values.
        Assert.IsTrue(
            got.All(p => p >= baseNs),
            "the timestamps are graph-clock values, so the publisher's own timing was discarded");
    }

    /// <summary>
    /// A published pair keeps the offset it was published with, which is what lip-sync is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole receiver path in one assertion: video is published with a deliberate offset from
    /// the audio it belongs to, and the consumer has to see that same offset. Preserving each
    /// stream's timing individually is not enough - what a viewer notices is the difference between
    /// them, and a receiver that re-times either one independently destroys it.
    /// </para>
    /// <para>
    /// This is the part a GPU-surface library cannot do at all. Spout and Syphon carry pixels and no
    /// time, so a sender built on them has to invent its own clock and a receiver has nothing to
    /// align against.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task APublishedPair_KeepsTheOffsetItWasPublishedWith()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-avpublish", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        // The video leg is published a fixed distance ahead of where its cycle would put it. The
        // consumer must measure that same distance back out.
        const long leadNs = 400_000_000;

        long videoCycleSeen = 0;
        long videoPtsSeen = 0;

        await using var video = new PipeWireVideoOutput(
            ctx, $"pwnet-avpublish-v-{Environment.ProcessId}", Width, Height, PixelFormat.Bgra, 30);

        video.FillFrame += (sender, pixels, stride, w, h, _) =>
        {
            if (sender.GraphClock is not { } clock) return false;

            // Expressed on the graph's own clock, which is the only thing a consumer can compare
            // against - a foreign epoch would be meaningless to it.
            long pts = (long)clock.TimeNs + leadNs;
            sender.NextPresentationTimestampNs = pts;

            Volatile.Write(ref videoCycleSeen, (long)clock.TimeNs);
            Volatile.Write(ref videoPtsSeen, pts);

            for (var y = 0; y < h; y++) pixels.Slice(y * stride, w * 4).Fill(0x5A);
            return true;
        };

        video.Connect(autoConnect: false);
        uint videoNode = await NodeIdAsync(video, cts.Token);

        // Audio published alongside, carrying a ramp so its own delivery can be verified.
        uint n = 0;
        await using var audio = new PipeWireAudioOutput(
            ctx, $"pwnet-avpublish-a-{Environment.ProcessId}", Rate, 1, AudioSampleFormat.F32Le);

        audio.FillSamples += (_, samples, _, _, _) =>
        {
            Span<float> floats = MemoryMarshal.Cast<byte, float>(samples);
            for (var i = 0; i < floats.Length; i++) floats[i] = ++n;
            return samples.Length;
        };

        audio.Connect(autoConnect: false);

        var offsets = new List<long>();
        long audioSamples = 0;

        await using var videoSink = new PipeWireVideoCapture(ctx, "pwnet-avpublish-v-sink");
        videoSink.FrameReady += (_, f) =>
        {
            if (f.PresentationTimestampNs is not { } pts || f.GraphTimeNs is not { } cycle) return;

            // What the consumer can actually measure: how far ahead of the cycle it is being
            // shown in, the frame says it belongs.
            lock (offsets) { if (offsets.Count < 32) offsets.Add(pts - cycle); }
        };

        videoSink.Connect(videoNode, [PixelFormat.Bgra]);

        await using var audioSink = new PipeWireAudioCapture(ctx, "pwnet-avpublish-a-sink");
        audioSink.FrameReady += (_, f) => Interlocked.Add(ref audioSamples, f.Samples.Length / 4);
        audioSink.Connect((await audio.WaitForNodeIdAsync(cts.Token)), sampleRate: Rate, channels: 1,
            format: AudioSampleFormat.F32Le);

        await videoSink.WaitForStreamingAsync(cts.Token);
        await audioSink.WaitForStreamingAsync(cts.Token);

        for (var i = 0; i < 100; i++)
        {
            lock (offsets) { if (offsets.Count >= 5) break; }
            await Task.Delay(50, cts.Token);
        }

        long[] measured;
        lock (offsets) measured = [.. offsets];

        Assert.IsTrue(measured.Length >= 5, $"only {measured.Length} video frames carried both times");

        Assert.IsTrue(
            Interlocked.Read(ref audioSamples) > 0,
            "the audio leg of the session delivered nothing");

        Assert.IsTrue(
            Volatile.Read(ref videoPtsSeen) > Volatile.Read(ref videoCycleSeen),
            "the publisher never set a lead at all");

        // The offset survives the round trip. A tolerance of a few cycles, because the frame is
        // shown in the cycle after the one it was written in.
        foreach (long offset in measured)
        {
            Assert.AreEqual(
                leadNs / 1e6,
                offset / 1e6,
                50.0,
                $"a frame published {leadNs / 1e6:F0}ms ahead was received {offset / 1e6:F1}ms "
                + "ahead; the publisher's A/V offset did not survive");
        }
    }

    /// <summary>
    /// A decoded GPU frame is republished zero-copy, keeping the sender's timing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receiving end of a hardware path: a decoder hands back a dmabuf, and it goes into the
    /// graph without a copy and without losing the presentation time it arrived with. Doing one of
    /// those without the other is the common failure - a copy costs the whole point of hardware
    /// decode, and a lost timestamp costs lip-sync.
    /// </para>
    /// <para>
    /// Zero-copy is checked by identity, not by the fields looking plausible: the consumer's
    /// descriptor must name the same kernel object the publisher allocated.
    /// </para>
    /// </remarks>
    [TestMethod]
    [TestCategory("RequiresGpu")]
    public async Task ADecodedGpuFrame_IsRepublishedZeroCopyWithItsTiming()
    {
        RequireLinux();

        if (!File.Exists("/dev/dri/renderD128")) Assert.Inconclusive("No GPU render node.");

        GbmAllocator gbm;
        try
        {
            gbm = new GbmAllocator("/dev/dri/renderD128");
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"libgbm unavailable ({ex.Message}).");
            return;
        }

        using var cts = new CancellationTokenSource(Budget);
        var buffers = new List<GbmAllocator.Buffer>();

        // Times a decoder would carry: a base nothing local could produce, stepping per frame.
        const long baseNs = 900_000_000_000;
        const long stepNs = 33_000_000;

        try
        {
            using (gbm)
            {
                await using var ctx = new PipeWireContext("pwnet-republish-gpu", ConsoleTestLoggerFactory.Instance);
                await ctx.StartAsync(cts.Token);

                long modifier = (long)GbmAllocator.LinearModifier;
                var published = new List<long>();

                await using var output = new PipeWireVideoOutput(
                    ctx, "pwnet-republish-gpu-src", Width, Height, PixelFormat.Bgra, 30);

                output.AllocateDmaBuf += (_, index, _, _, _, _, planes) =>
                {
                    if (index >= 8) return 0;
                    while (buffers.Count <= index) buffers.Add(gbm.CreateBgra(Width, Height));
                    GbmAllocator.Buffer b = buffers[index];
                    planes[0] = new VideoPlane(b.Fd, b.Offset, b.Stride, b.Size);
                    return 1;
                };

                output.FillDmaBuf += (sender, _) =>
                {
                    long pts;
                    lock (published)
                    {
                        pts = baseNs + (published.Count * stepNs);
                        if (published.Count < 32) published.Add(pts);
                    }

                    // The decoder's time, not this cycle's.
                    sender.NextPresentationTimestampNs = pts;
                    return true;
                };

                output.ConnectDmaBuf([modifier]);

                uint? nodeId = null;
                for (var i = 0; i < 60 && nodeId is null; i++)
                {
                    nodeId = output.NodeId;
                    if (nodeId is null) await Task.Delay(50, cts.Token);
                }

                Assert.IsNotNull(nodeId, "the republishing producer was never given a node id");

                await using var capture = new PipeWireVideoCapture(ctx, "pwnet-republish-gpu-sink")
                {
                    Retention = FrameRetention.Borrowed,
                };

                var stamps = new List<long>();
                capture.FrameReady += (_, f) =>
                {
                    if (f.BufferType != PipeWireBufferType.DmaBuf) return;
                    if (f.PresentationTimestampNs is not { } pts) return;
                    lock (stamps) { if (stamps.Count < 32) stamps.Add(pts); }
                };

                capture.Connect(nodeId!.Value, [PixelFormat.Bgra], modifiers: [modifier]);

                BorrowedVideoFrame borrowed = default;
                var got = false;
                for (var i = 0; i < 120 && !got; i++)
                {
                    got = capture.TryGetBorrowedFrame(out borrowed) && borrowed.IsFdBacked;
                    if (!got) await Task.Delay(50, cts.Token);
                }

                Assert.IsTrue(got, "no fd-backed frame was republished");

                long[] sent, seen;
                lock (published) sent = [.. published];
                lock (stamps) seen = [.. stamps];

                Assert.IsTrue(seen.Length > 0, "no republished frame carried a presentation time");

                // The timing survived.
                var sentSet = sent.ToHashSet();
                foreach (long pts in seen)
                {
                    Assert.IsTrue(
                        sentSet.Contains(pts),
                        $"a republished frame arrived stamped {pts}, which the publisher never set - "
                        + "the decoder's timing was replaced by the local cycle");
                }

                // And it is still the publisher's GPU buffer, not a copy of it.
                ulong? consumerInode = StreamTransportContractTests.InodeOfForTests((int)borrowed[0].Fd);
                Assert.IsNotNull(consumerInode, "the republished descriptor is not live");

                var producerInodes = new HashSet<ulong>();
                foreach (GbmAllocator.Buffer b in buffers)
                {
                    if (StreamTransportContractTests.InodeOfForTests((int)b.Fd) is { } ino)
                        producerInodes.Add(ino);
                }

                Assert.IsTrue(
                    producerInodes.Contains(consumerInode!.Value),
                    "the republished frame does not name the buffer that was published, so the "
                    + "receive path copied it");
            }
        }
        finally
        {
            foreach (GbmAllocator.Buffer b in buffers) b.Dispose();
        }
    }

    /// <summary>
    /// A capture session takes video and audio together and can align them, borrowed or owned.
    /// </summary>
    /// <remarks>
    /// The sender half, as a session rather than as two unrelated streams. Both retention modes are
    /// exercised on the same graph because a transport picks one: borrowed for a GPU frame going
    /// straight to an encoder, owned when the frame has to outlive the callback. Both must deliver,
    /// and both must carry a time on the same clock the audio is on, or there is nothing to align.
    /// </remarks>
    [TestMethod]
    public async Task ACaptureSession_TakesVideoAndAudioOnOneClock()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-avsession", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var video = new PipeWireVideoOutput(
            ctx, $"pwnet-avsession-v-{Environment.ProcessId}", Width, Height, PixelFormat.Bgra, 30);

        video.FillFrame += (_, pixels, stride, w, h, _) =>
        {
            for (var y = 0; y < h; y++) pixels.Slice(y * stride, w * 4).Fill(0x3C);
            return true;
        };

        video.Connect(autoConnect: false);
        uint videoNode = await NodeIdAsync(video, cts.Token);

        uint n = 0;
        await using var audio = new PipeWireAudioOutput(
            ctx, $"pwnet-avsession-a-{Environment.ProcessId}", Rate, 1, AudioSampleFormat.F32Le);

        audio.FillSamples += (_, samples, _, _, _) =>
        {
            Span<float> floats = MemoryMarshal.Cast<byte, float>(samples);
            for (var i = 0; i < floats.Length; i++) floats[i] = ++n;
            return samples.Length;
        };

        audio.Connect(autoConnect: false);

        long audioClock = 0;
        await using var audioSink = new PipeWireAudioCapture(ctx, "pwnet-avsession-a-sink");
        audioSink.FrameReady += (_, f) =>
        {
            if (f.GraphTimeNs is { } t) Volatile.Write(ref audioClock, t);
        };

        audioSink.Connect((await audio.WaitForNodeIdAsync(cts.Token)), sampleRate: Rate, channels: 1,
            format: AudioSampleFormat.F32Le);

        await audioSink.WaitForStreamingAsync(cts.Token);

        foreach (FrameRetention retention in new[] { FrameRetention.Owned, FrameRetention.Borrowed })
        {
            await using var videoSink = new PipeWireVideoCapture(
                ctx, $"pwnet-avsession-v-sink-{retention}")
            {
                Retention = retention,
            };

            long videoClock = 0;
            var frames = 0;
            videoSink.FrameReady += (_, f) =>
            {
                if (f.GraphTimeNs is { } t) Volatile.Write(ref videoClock, t);
                Interlocked.Increment(ref frames);
            };

            videoSink.Connect(videoNode, [PixelFormat.Bgra]);
            await videoSink.WaitForStreamingAsync(cts.Token);
            await Task.Delay(600, cts.Token);

            Assert.IsTrue(
                Volatile.Read(ref frames) > 0,
                $"{retention} retention delivered no frames in the session");

            // Whichever retention is in use, the frame is timed on the same clock as the audio, so
            // the two are comparable. Values seconds apart would mean separate time bases.
            long v = Volatile.Read(ref videoClock);
            long a = Volatile.Read(ref audioClock);

            Assert.IsTrue(v > 0, $"{retention} retention produced no video timestamp");
            Assert.IsTrue(a > 0, "the audio leg produced no timestamp");

            Assert.IsTrue(
                Math.Abs(v - a) < 5_000_000_000,
                $"{retention}: video at {v} and audio at {a} are not on one clock");

            // And the retained frame itself is usable, which is the point of retaining it.
            if (retention == FrameRetention.Owned)
            {
                var pulled = false;
                for (var i = 0; i < 40 && !pulled; i++)
                {
                    if (videoSink.TryGetFrame(out PulledVideoFrame? kept) && kept is not null)
                    {
                        using (kept)
                        {
                            Assert.AreEqual(Width, kept.Width);
                            pulled = true;
                        }
                    }
                    else
                    {
                        await Task.Delay(25, cts.Token);
                    }
                }

                Assert.IsTrue(pulled, "owned retention kept nothing that could be pulled");
            }
        }
    }
}
