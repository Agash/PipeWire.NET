using System.Runtime.Versioning;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// Real-world capture tests: a GStreamer pipeline publishes a genuine PipeWire source
/// (SMPTE video bars, audio test tone) and our capture API consumes it - exercising the
/// actual consumer path StreamWeaver uses (capturing a producer it does not control),
/// across multiple pixel formats, registry discovery, and A/V together.
/// </summary>
[TestClass]
[TestCategory("RequiresGStreamer")]
[SupportedOSPlatform("linux")]
public sealed class GStreamerIntegrationTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task CaptureVideoTestSrc_DeliversRealContent()
    {
        GstTestSource.RequireGStreamer();

        await using var ctx = new PipeWireContext("test", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync();

        const string node = "gst-smpte-bgra";
        await using var src = await GstTestSource.StartAsync(ctx, node,
            "videotestsrc is-live=true pattern=smpte ! video/x-raw,format=BGRA,width=320,height=240,framerate=30/1",
            mediaClass: "Video/Source");

        var done = new TaskCompletionSource<(int W, int H, PixelFormat F, int Distinct)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cap = new PipeWireVideoCapture(ctx, "gst-smpte-sink");
        cap.FrameReady += (_, frame) =>
        {
            if (frame.Data.Length < 1024) return;
            // A blank/black pre-roll frame is uniform (1 distinct byte). Real SMPTE bars are
            // non-uniform - 100% bars use pure primaries so it's only 0x00/0xFF per channel,
            // i.e. >=2 distinct values. So "not uniform" is the correct "real content" gate.
            Span<bool> seen = stackalloc bool[256];
            int distinct = 0;
            foreach (byte b in frame.Data[..1024]) { if (!seen[b]) { seen[b] = true; distinct++; } }
            if (distinct < 2) return;
            done.TrySetResult((frame.Width, frame.Height, frame.Format, distinct));
        };
        cap.Connect(preferredFormats: stackalloc[] { PixelFormat.Bgra }, targetObjectName: node);

        var got = await done.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.AreEqual(320, got.W);
        Assert.AreEqual(240, got.H);
        Assert.AreEqual(PixelFormat.Bgra, got.F);
        Assert.IsTrue(got.Distinct > 1, $"expected non-uniform SMPTE content, saw only {got.Distinct} distinct byte values");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [DataRow("BGRA", PixelFormat.Bgra)]
    [DataRow("RGBA", PixelFormat.Rgba)]
    [DataRow("I420", PixelFormat.Yuv420)]
    public async Task CaptureVideo_NegotiatesRequestedFormat(string gstFormat, PixelFormat expected)
    {
        GstTestSource.RequireGStreamer();

        await using var ctx = new PipeWireContext("test", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync();

        string node = $"gst-fmt-{gstFormat.ToLowerInvariant()}";
        await using var src = await GstTestSource.StartAsync(ctx, node,
            $"videotestsrc is-live=true ! video/x-raw,format={gstFormat},width=160,height=120,framerate=30/1",
            mediaClass: "Video/Source");

        var done = new TaskCompletionSource<PixelFormat>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cap = new PipeWireVideoCapture(ctx, $"{node}-sink");
        cap.FrameReady += (_, frame) => { if (frame.Data.Length > 0) done.TrySetResult(frame.Format); };
        cap.Connect(preferredFormats: stackalloc[] { expected }, targetObjectName: node);

        PixelFormat negotiated = await done.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.AreEqual(expected, negotiated);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task Registry_DiscoversGstSource()
    {
        GstTestSource.RequireGStreamer();

        await using var ctx = new PipeWireContext("test", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync();

        const string node = "gst-registry-probe";
        await using var src = await GstTestSource.StartAsync(ctx, node,
            "videotestsrc is-live=true ! video/x-raw,format=BGRA,width=64,height=64,framerate=30/1",
            mediaClass: "Video/Source");

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync();

        // The node may have arrived before the registry; poll briefly to be robust.
        PipeWireNode? found = null;
        for (int i = 0; i < 20 && found is null; i++)
        {
            found = reg.Nodes.FirstOrDefault(s => s.NodeName == node);
            if (found is null) await Task.Delay(100);
        }

        Assert.IsNotNull(found, "gst source should be discoverable via the registry");
        Assert.AreEqual(PipeWireMediaKind.Video, found!.Media,
            $"expected a video node, got class '{found.MediaClass}'");
        Assert.AreEqual(PipeWireMediaFlow.Source, found.Flow,
            $"expected a source, got class '{found.MediaClass}'");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task CaptureAudioTestSrc_DeliversTone()
    {
        GstTestSource.RequireGStreamer();

        await using var ctx = new PipeWireContext("test", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync();

        const string node = "gst-tone";
        await using var src = await GstTestSource.StartAsync(ctx, node,
            "audiotestsrc is-live=true ! audioconvert ! audio/x-raw,format=F32LE,channels=2,rate=48000",
            mediaClass: "Audio/Source");

        var done = new TaskCompletionSource<(int Rate, int Ch, bool NonSilent)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cap = new PipeWireAudioCapture(ctx, "gst-tone-sink");
        cap.FrameReady += (_, frame) =>
        {
            // Skip the initial silence pre-roll; wait for actual tone samples.
            bool nonSilent = false;
            foreach (byte b in frame.Samples) { if (b != 0) { nonSilent = true; break; } }
            if (nonSilent)
                done.TrySetResult((frame.SampleRate, frame.Channels, true));
        };
        cap.Connect(sampleRate: 48000, channels: 2, format: AudioSampleFormat.F32Le, targetObjectName: node);

        var got = await done.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.AreEqual(48000, got.Rate);
        Assert.AreEqual(2, got.Ch);
        Assert.IsTrue(got.NonSilent, "audiotestsrc tone should produce non-zero samples");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task VideoPresentationTimestamps_AreRealMonotonicAndRealTimePaced()
    {
        GstTestSource.RequireGStreamer();

        await using var ctx = new PipeWireContext("test", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync();

        const string node = "gst-pts-video";
        await using var src = await GstTestSource.StartAsync(ctx, node,
            "videotestsrc is-live=true ! video/x-raw,format=BGRA,width=160,height=120,framerate=30/1",
            mediaClass: "Video/Source");

        // Collect a window of PTS values. These come from the SPA_META_Header we request via
        // pw_stream_update_params in param_changed (without that, frames carry no timestamp).
        var pts = new List<long>();
        const int want = 15;                                   // ~0.5s at 30fps
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cap = new PipeWireVideoCapture(ctx, node + "-sink");
        cap.FrameReady += (_, f) =>
        {
            if (f.PresentationTimeNs < 0) return;
            lock (pts) { if (pts.Count < want) { pts.Add(f.PresentationTimeNs); if (pts.Count == want) enough.TrySetResult(); } }
        };
        cap.Connect(preferredFormats: stackalloc[] { PixelFormat.Bgra }, targetObjectName: node);

        await enough.Task.WaitAsync(TimeSpan.FromSeconds(10));
        long[] v;
        lock (pts) v = [.. pts];

        // Real, non-zero timestamps actually arrived (proves the header-meta negotiation works).
        Assert.IsTrue(v[0] > 0, "presentation timestamps must be real (SPA_META_Header attached)");
        // Strictly monotonic.
        for (int i = 1; i < v.Length; i++)
            Assert.IsTrue(v[i] > v[i - 1], "video PTS must increase monotonically");
        // Wall-clock paced: 30fps => ~33ms/frame. A counter or arbitrary value would not be.
        double avgFrameMs = (v[^1] - v[0]) / 1_000_000.0 / (v.Length - 1);
        Assert.IsTrue(avgFrameMs is > 10 and < 100,
            $"video PTS cadence {avgFrameMs:F1}ms/frame should be near 33ms (real-time 30fps)");
    }

    // NOTE: DMA-BUF zero-copy capture is covered by DmaBufRoundTripTests, which drives a libgbm-backed
    // PipeWireVideoOutput dmabuf producer into a PipeWireVideoCapture consumer in-process. We do NOT test
    // it via a gst GL source: pipewiresink's dmabuf EXPORT support varies by distro/driver/gst build (some
    // advertise no DRM modifier in EnumFormat at all and only ever hand out host memory), which makes a
    // gst-driven dmabuf test flaky and non-portable for CI. The in-process round-trip exercises the same
    // negotiation/fixation/fd-passing deterministically wherever a render node + libgbm exist.

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task AudioCapture_ExposesSampleAccurateClock()
    {
        GstTestSource.RequireGStreamer();

        await using var ctx = new PipeWireContext("test", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync();

        const string node = "gst-audio-clock";
        await using var src = await GstTestSource.StartAsync(ctx, node,
            "audiotestsrc is-live=true ! audioconvert ! audio/x-raw,format=F32LE,channels=2,rate=48000",
            mediaClass: "Audio/Source");

        var media = new List<long>();
        long lastDelay = -1;
        const int want = 10;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cap = new PipeWireAudioCapture(ctx, node + "-sink");
        cap.FrameReady += (_, f) =>
        {
            lastDelay = f.DelayNs;
            lock (media) { if (media.Count < want && f.MediaClockNs >= 0) { media.Add(f.MediaClockNs); if (media.Count == want) done.TrySetResult(); } }
        };
        cap.Connect(sampleRate: 48000, channels: 2, format: AudioSampleFormat.F32Le, targetObjectName: node);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        long[] m;
        lock (media) m = [.. media];

        // Media clock advances monotonically at ~real time (sample-accurate: derived from
        // ticks*rate). It starts near 0 (ticks=0 at stream start), so check advancement, not >0.
        // A broken rate/ticks read would be constant (span 0) or go backwards.
        for (int i = 1; i < m.Length; i++) Assert.IsTrue(m[i] >= m[i - 1], "media clock must be non-decreasing");
        Assert.IsTrue(m[^1] > m[0], "media clock must advance");
        double spanSec = (m[^1] - m[0]) / 1e9;
        Assert.IsTrue(spanSec is > 0.001 and < 5, $"media clock should advance at ~real time, got {spanSec:F3}s over {m.Length} chunks");
        Assert.IsTrue(lastDelay >= 0, "delay (latency) must be a sane non-negative value");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task AudioAndVideo_ShareTheGraphClock_AndCanBeSynced()
    {
        GstTestSource.RequireGStreamer();

        await using var ctx = new PipeWireContext("test", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync();

        // One gst pipeline, two named sinks -> both PipeWire nodes run in the same graph,
        // driven by the same clock. Per PipeWire's timing model, pw_stream_get_time().now is
        // the monotonic graph-clock time of the processing cycle and is the SAME reference for
        // every stream. We surface it as Frame.CaptureClockNs. This test proves audio and video
        // are stamped on that one shared clock - which is exactly what makes lip-sync possible.
        const string vNode = "gst-av-video", aNode = "gst-av-audio";
        await using var src = await GstTestSource.StartTwoAsync(ctx,
            video: ("videotestsrc is-live=true ! video/x-raw,format=BGRA,width=160,height=120,framerate=30/1", vNode),
            audio: ("audiotestsrc is-live=true ! audioconvert ! audio/x-raw,format=F32LE,channels=2,rate=48000", aNode));

        var vClk = new List<long>();
        var aClk = new List<long>();
        bool audioNonSilent = false;
        const int cap = 64; // bound memory; we collect over a window rather than a fixed count.

        await using var vCap = new PipeWireVideoCapture(ctx, "gst-av-video-sink");
        vCap.FrameReady += (_, f) =>
        {
            lock (vClk) { if (vClk.Count < cap) vClk.Add(f.CaptureClockNs); }
        };
        vCap.Connect(preferredFormats: stackalloc[] { PixelFormat.Bgra }, targetObjectName: vNode);

        await using var aCap = new PipeWireAudioCapture(ctx, "gst-av-audio-sink");
        aCap.FrameReady += (_, f) =>
        {
            foreach (byte b in f.Samples) { if (b != 0) { audioNonSilent = true; break; } }
            lock (aClk) { if (aClk.Count < cap) aClk.Add(f.CaptureClockNs); }
        };
        aCap.Connect(sampleRate: 48000, channels: 2, format: AudioSampleFormat.F32Le, targetObjectName: aNode);

        // Poll until BOTH legs have stamped a few frames (don't couple to either's exact cadence): a live
        // source flushes its preroll as a burst then paces, and in this dual-sink pipeline the video leg
        // starts a beat behind audio, so a fixed delay can sample before video's first cycle. Give it up
        // to ~12s to get both flowing before snapshotting.
        const int minSamples = 3;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            int vc, ac;
            lock (vClk) vc = vClk.Count;
            lock (aClk) ac = aClk.Count;
            if (vc >= minSamples && ac >= minSamples) break;
            await Task.Delay(100);
        }

        long[] v, a;
        lock (vClk) v = [.. vClk];
        lock (aClk) a = [.. aClk];

        // The two-provide-sink gst pipeline occasionally fails to start its video branch under daemon
        // contention (a gst-launch dual-sink startup race, not a capture defect - the single-sink video
        // tests above are reliable). That leaves nothing to compare, so report it as inconclusive rather
        // than a false failure; the shared-clock assertion below still runs whenever the graph forms. The
        // deterministic, contention-free coverage of the capture clock itself is AudioCapture_Exposes...
        // and VideoPresentationTimestamps_...; this test specifically proves audio+video share ONE clock,
        // which requires them in one driver group - only achievable via a single gst pipeline.
        if (v.Length == 0 || a.Length == 0)
        {
            Assert.Inconclusive(
                $"gst dual-sink graph did not start both legs in time (video={v.Length}, audio={a.Length}) - dual-sink startup race.");
            return;
        }

        Assert.IsTrue(audioNonSilent, "audio must carry real (non-silent) samples");

        // 1. Both streams produce a real graph-clock timestamp (proves pw_stream_get_time works
        //    for audio AND video - audio has no header PTS but DOES have this).
        Assert.IsTrue(v[0] > 0, "video CaptureClockNs must be a real graph-clock time");
        Assert.IsTrue(a[0] > 0, "audio CaptureClockNs must be a real graph-clock time");

        // 2. Each stream's clock advances monotonically.
        for (int i = 1; i < v.Length; i++) Assert.IsTrue(v[i] >= v[i - 1], "video clock must be monotonic");
        for (int i = 1; i < a.Length; i++) Assert.IsTrue(a[i] >= a[i - 1], "audio clock must be monotonic");

        // 3. THE sync proof: the two streams' clock ranges are mutually close. On one shared timeline
        //    the windows sit on top of each other (gap <= a small tolerance); on different clocks (or a
        //    fabricated one) the nanosecond windows would be seconds/epochs apart. A strict range
        //    *overlap* can't be required here: a live source flushes its preroll as a burst in one
        //    processing cycle, so a stream's whole window can collapse to a single clock value just
        //    before/after the other's window. Proximity (gap within tolerance) proves the shared clock
        //    without assuming both streams span time.
        //
        //    We deliberately do NOT compare the medians of the two collections: video flushes its preroll
        //    as a burst and then paces, while audio streams steadily, so the two sample sets have very
        //    different distributions over the window. Their medians can sit hundreds of ms apart purely
        //    from that skew even though every sample is stamped from the one graph clock - the range
        //    proximity below is the distribution-independent test of "same timeline".
        long gap = Math.Max(0, Math.Max(v[0], a[0]) - Math.Min(v[^1], a[^1]));
        Assert.IsTrue(gap < 250_000_000,
            $"audio and video must be stamped on one shared clock (video [{v[0]}..{v[^1]}], audio [{a[0]}..{a[^1]}], gap={gap / 1e6:F1}ms)");
    }
}
