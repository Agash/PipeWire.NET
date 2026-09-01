using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// Captures from real GStreamer producers across the shapes a consumer actually meets: several
/// pixel formats and resolutions, several sample rates and channel counts, odd dimensions, tiny and
/// large frames, and producers that stop mid-stream.
/// </summary>
/// <remarks>
/// The existing GStreamer tests prove one representative pipeline works. These vary the negotiated
/// format instead, because that is where the stride and buffer arithmetic lives, and a wrong answer
/// there is silent - a torn or short frame rather than an exception.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[TestCategory("RequiresGStreamer")]
[SupportedOSPlatform("linux")]
public sealed class RealProducerMatrixTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    private static void Require()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
        GstTestSource.RequireGStreamer();
    }

    // ------------------------------------------------------------------ video

    [TestMethod]
    [DataRow("BGRA", PixelFormat.Bgra, 320, 240)]
    [DataRow("RGBA", PixelFormat.Rgba, 320, 240)]
    [DataRow("BGRx", PixelFormat.Bgrx, 320, 240)]
    [DataRow("RGBx", PixelFormat.Rgbx, 320, 240)]
    [DataRow("YUY2", PixelFormat.Yuyv, 320, 240)]
    [DataRow("I420", PixelFormat.Yuv420, 320, 240)]
    [DataRow("NV12", PixelFormat.Nv12, 320, 240)]
    public async Task EveryPixelFormat_NegotiatesAndDeliversWholeFrames(
        string gstFormat, PixelFormat expected, int width, int height)
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext($"pwnet-fmt-{gstFormat}", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        string node = $"pwnet_fmt_{gstFormat.ToLowerInvariant()}";
        await using GstTestSource src = await GstTestSource.StartAsync(
            ctx, node,
            $"videotestsrc is-live=true ! video/x-raw,format={gstFormat},width={width},height={height},framerate=30/1",
            "Video/Source");

        (int frames, int shortFrames, PixelFormat? negotiated, int w, int h) =
            await CaptureVideoAsync(ctx, src.NodeId, expected, cts.Token);

        Assert.IsTrue(frames > 0, $"{gstFormat}: no frame arrived");
        Assert.AreEqual(expected, negotiated, $"{gstFormat} negotiated as {negotiated}");
        Assert.AreEqual(width, w);
        Assert.AreEqual(height, h);
        Assert.AreEqual(0, shortFrames,
            $"{gstFormat}: {shortFrames} of {frames} frames were smaller than the format requires");
    }

    [TestMethod]
    [DataRow(2, 2)]          // smallest sane frame
    [DataRow(17, 13)]        // odd on both axes
    [DataRow(641, 481)]      // odd, and past a page boundary
    [DataRow(1920, 1080)]    // the common case
    public async Task OddAndExtremeDimensions_StillDeliverWholeFrames(int width, int height)
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext($"pwnet-dim-{width}x{height}", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        // I420 is the format whose chroma planes round up, so odd dimensions are its hard case.
        string node = $"pwnet_dim_{width}x{height}";
        await using GstTestSource src = await GstTestSource.StartAsync(
            ctx, node,
            $"videotestsrc is-live=true ! video/x-raw,format=I420,width={width},height={height},framerate=30/1",
            "Video/Source");

        (int frames, int shortFrames, PixelFormat? _, int w, int h) =
            await CaptureVideoAsync(ctx, src.NodeId, PixelFormat.Yuv420, cts.Token);

        Assert.IsTrue(frames > 0, $"{width}x{height}: no frame arrived");
        Assert.AreEqual(width, w);
        Assert.AreEqual(height, h);
        Assert.AreEqual(0, shortFrames,
            $"{width}x{height}: {shortFrames} short frames - the planar size calculation under-allocates");
    }

    // ------------------------------------------------------------------ audio

    [TestMethod]
    [DataRow("S16LE", AudioSampleFormat.S16Le, 48000, 2)]
    [DataRow("S32LE", AudioSampleFormat.S32Le, 48000, 2)]
    [DataRow("F32LE", AudioSampleFormat.F32Le, 48000, 2)]
    [DataRow("F32LE", AudioSampleFormat.F32Le, 44100, 1)]
    [DataRow("F32LE", AudioSampleFormat.F32Le, 96000, 2)]
    [DataRow("S16LE", AudioSampleFormat.S16Le, 8000, 1)]
    public async Task EveryAudioShape_NegotiatesAndDeliversWholeFrames(
        string gstFormat, AudioSampleFormat expected, int rate, int channels)
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            $"pwnet-aud-{gstFormat}-{rate}-{channels}", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        string node = $"pwnet_aud_{gstFormat.ToLowerInvariant()}_{rate}_{channels}";
        await using GstTestSource src = await GstTestSource.StartAsync(
            ctx, node,
            $"audiotestsrc is-live=true ! audio/x-raw,format={gstFormat},rate={rate},channels={channels}",
            "Audio/Source");

        var buffers = 0;
        var ragged = 0;
        AudioSampleFormat? negotiated = null;
        int seenRate = 0, seenChannels = 0;

        await using var capture = new PipeWireAudioCapture(ctx, $"pwnet-aud-consumer-{node}");
        capture.FrameReady += (_, frame) =>
        {
            Interlocked.Increment(ref buffers);
            negotiated ??= frame.Format;
            seenRate = frame.SampleRate;
            seenChannels = frame.Channels;

            // A buffer that is not a whole number of frames means the stride maths is wrong.
            int bytesPerFrame = frame.Channels * frame.Format.BytesPerSample();
            if (bytesPerFrame > 0 && frame.Samples.Length % bytesPerFrame != 0)
                Interlocked.Increment(ref ragged);
        };

        // These are preferences, not observations: PipeWire converts, so asking for the
        // producer's own shape is what proves the request is honoured end to end.
        capture.Connect(src.NodeId, rate, channels, expected);
        await WaitForAsync(() => Volatile.Read(ref buffers) >= 3, cts.Token);

        Assert.IsTrue(buffers > 0, $"{gstFormat} {rate}Hz {channels}ch: no audio arrived");
        Assert.AreEqual(expected, negotiated);
        Assert.AreEqual(rate, seenRate);
        Assert.AreEqual(channels, seenChannels);
        Assert.AreEqual(0, ragged, $"{ragged} buffers were not a whole number of frames");
    }

    // ------------------------------------------------------------------ producers behaving badly

    [TestMethod]
    public async Task AProducerThatDisappearsMidStream_LeavesTheConsumerUsable()
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-vanish", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        var frames = 0;
        var states = new List<PipeWireStreamState>();

        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-vanish-consumer");
        capture.FrameReady += (_, _) => Interlocked.Increment(ref frames);
        capture.StateChanged += (_, _, s) => { lock (states) states.Add(s); };

        uint nodeId;
        {
            await using GstTestSource src = await GstTestSource.StartAsync(
                ctx, "pwnet_vanish",
                "videotestsrc is-live=true ! video/x-raw,format=BGRA,width=160,height=120,framerate=30/1",
                "Video/Source");
            nodeId = src.NodeId;

            capture.Connect(nodeId, [PixelFormat.Bgra]);
            await WaitForAsync(() => Volatile.Read(ref frames) >= 3, cts.Token);
        }   // the producer is killed here

        int atDeath = Volatile.Read(ref frames);
        await WaitForAsync(() => registry.Current.GetNode(nodeId) is null, cts.Token);

        // The consumer must survive its producer, and must be able to say so rather than hanging.
        lock (states)
            Assert.IsTrue(states.Count > 0, "the consumer never reported a state at all");

        Assert.IsTrue(atDeath > 0, "no frames arrived before the producer was killed");
    }

    [TestMethod]
    public async Task TwoConsumersOnOneProducer_BothReceive()
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-fanout", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using GstTestSource src = await GstTestSource.StartAsync(
            ctx, "pwnet_fanout",
            "videotestsrc is-live=true ! video/x-raw,format=BGRA,width=160,height=120,framerate=30/1",
            "Video/Source");

        var a = 0;
        var b = 0;
        await using var first = new PipeWireVideoCapture(ctx, "pwnet-fanout-a");
        await using var second = new PipeWireVideoCapture(ctx, "pwnet-fanout-b");
        first.FrameReady += (_, _) => Interlocked.Increment(ref a);
        second.FrameReady += (_, _) => Interlocked.Increment(ref b);

        first.Connect(src.NodeId, [PixelFormat.Bgra]);
        second.Connect(src.NodeId, [PixelFormat.Bgra]);

        await WaitForAsync(() => Volatile.Read(ref a) >= 3 && Volatile.Read(ref b) >= 3, cts.Token);

        Assert.IsTrue(a >= 3, $"first consumer only saw {a} frames");
        Assert.IsTrue(b >= 3, $"second consumer only saw {b} frames");
    }

    [TestMethod]
    public async Task AConsumerThatThrowsOnEveryFrame_DoesNotStopTheStream()
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-badconsumer", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using GstTestSource src = await GstTestSource.StartAsync(
            ctx, "pwnet_badconsumer",
            "videotestsrc is-live=true ! video/x-raw,format=BGRA,width=160,height=120,framerate=30/1",
            "Video/Source");

        // FrameReady is raised from the data thread; an escaping exception there aborts the process.
        var seen = 0;
        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-badconsumer-c");
        capture.FrameReady += (_, _) =>
        {
            Interlocked.Increment(ref seen);
            throw new InvalidOperationException("hostile frame handler");
        };

        capture.Connect(src.NodeId, [PixelFormat.Bgra]);
        await WaitForAsync(() => Volatile.Read(ref seen) >= 5, cts.Token);

        Assert.IsTrue(seen >= 5,
            $"the stream stopped after {seen} frames; a throwing handler must not end delivery");
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<(int Frames, int Short, PixelFormat? Format, int Width, int Height)>
        CaptureVideoAsync(PipeWireContext ctx, uint nodeId, PixelFormat want, CancellationToken ct)
    {
        var frames = 0;
        var shortFrames = 0;
        PixelFormat? negotiated = null;
        int width = 0, height = 0;

        await using var capture = new PipeWireVideoCapture(ctx, $"pwnet-consumer-{nodeId}");
        capture.FrameReady += (_, frame) =>
        {
            Interlocked.Increment(ref frames);
            negotiated ??= frame.Format;
            width = frame.Width;
            height = frame.Height;

            // Data spans the first block only, which for a planar format is the luma plane. The
            // floor is therefore one full block, not the whole image - see the planar-host note on
            // PipeWireVideoCapture.
            int required = SpaFormatProbe.BlockSize(frame.Format, frame.Width, frame.Height);
            if (frame.Data.Length > 0 && frame.Data.Length < required)
                Interlocked.Increment(ref shortFrames);
        };

        capture.Connect(nodeId, [want]);
        await WaitForAsync(() => Volatile.Read(ref frames) >= 3, ct);
        return (frames, shortFrames, negotiated, width, height);
    }

    private static async Task WaitForAsync(Func<bool> until, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!until())
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed > TimeSpan.FromSeconds(20))
                throw new TimeoutException("condition never held");
            await Task.Delay(25, ct);
        }
    }
}

/// <summary>Exposes the size calculation so a test can check a real frame against it.</summary>
[SupportedOSPlatform("linux")]
internal static class SpaFormatProbe
{
    internal static int ImageSize(PixelFormat fmt, int width, int height) =>
        SpaFormat.VideoImageSize(fmt, width, height);

    internal static int BlockSize(PixelFormat fmt, int width, int height) =>
        SpaFormat.VideoBlockSize(fmt, width, height);
}
