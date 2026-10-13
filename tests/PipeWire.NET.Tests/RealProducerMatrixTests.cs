using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// Captures from real GStreamer producers across the shapes a consumer actually meets: several
/// pixel formats and resolutions, several sample rates and channel counts, odd dimensions, tiny and
/// large frames, and producers that stop mid-stream.
/// </summary>
/// <remarks>
/// Vary the negotiated format, because that is where the stride and buffer arithmetic lives, and
/// a wrong answer there is silent - a torn or short frame rather than an exception.
/// </remarks>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[TestCategory("RequiresGStreamer")]
[SupportedOSPlatform("linux")]
public sealed class RealProducerMatrixTests : PipeWireTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    private static void Require()
    {
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
        string gstFormat,
        PixelFormat expected,
        int width,
        int height
    )
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            $"pwnet-fmt-{gstFormat}",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        string node = $"pwnet_fmt_{gstFormat.ToLowerInvariant()}";
        await using GstTestSource src = await GstTestSource.StartAsync(
            ctx,
            node,
            $"videotestsrc is-live=true ! video/x-raw,format={gstFormat},width={width},height={height},framerate=30/1",
            "Video/Source"
        );

        (int frames, int shortFrames, PixelFormat? negotiated, int w, int h) =
            await CaptureVideoAsync(ctx, src.NodeId, expected, cts.Token);

        Assert.IsTrue(frames > 0, $"{gstFormat}: no frame arrived");
        Assert.AreEqual(expected, negotiated, $"{gstFormat} negotiated as {negotiated}");
        Assert.AreEqual(width, w);
        Assert.AreEqual(height, h);
        Assert.AreEqual(
            0,
            shortFrames,
            $"{gstFormat}: {shortFrames} of {frames} frames were smaller than the format requires"
        );
    }

    [TestMethod]
    [DataRow(2, 2)] // smallest sane frame
    [DataRow(17, 13)] // odd on both axes
    [DataRow(641, 481)] // odd, and past a page boundary
    [DataRow(1920, 1080)] // the common case
    public async Task OddAndExtremeDimensions_StillDeliverWholeFrames(int width, int height)
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            $"pwnet-dim-{width}x{height}",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        // I420 is the format whose chroma planes round up, so odd dimensions are its hard case.
        string node = $"pwnet_dim_{width}x{height}";
        await using GstTestSource src = await GstTestSource.StartAsync(
            ctx,
            node,
            $"videotestsrc is-live=true ! video/x-raw,format=I420,width={width},height={height},framerate=30/1",
            "Video/Source"
        );

        (int frames, int shortFrames, PixelFormat? _, int w, int h) = await CaptureVideoAsync(
            ctx,
            src.NodeId,
            PixelFormat.Yuv420,
            cts.Token
        );

        Assert.IsTrue(frames > 0, $"{width}x{height}: no frame arrived");
        Assert.AreEqual(width, w);
        Assert.AreEqual(height, h);
        Assert.AreEqual(
            0,
            shortFrames,
            $"{width}x{height}: {shortFrames} short frames - the planar size calculation under-allocates"
        );
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
        string gstFormat,
        AudioSampleFormat expected,
        int rate,
        int channels
    )
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            $"pwnet-aud-{gstFormat}-{rate}-{channels}",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        string node = $"pwnet_aud_{gstFormat.ToLowerInvariant()}_{rate}_{channels}";
        await using GstTestSource src = await GstTestSource.StartAsync(
            ctx,
            node,
            $"audiotestsrc is-live=true ! audio/x-raw,format={gstFormat},rate={rate},channels={channels}",
            "Audio/Source"
        );

        var buffers = new Tally();
        var ragged = 0;
        AudioSampleFormat? negotiated = null;
        int seenRate = 0,
            seenChannels = 0;

        await using var capture = new PipeWireAudioCapture(ctx, $"pwnet-aud-consumer-{node}");
        capture.FrameReady += (_, frame) =>
        {
            buffers.Increment();
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
        capture.Connect(
            PipeWireTarget.Id(src.NodeId),
            new()
            {
                SampleRate = rate,
                Channels = channels,
                Format = expected,
            }
        );
        await buffers.AtLeast(3).WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

        Assert.IsTrue(buffers > 0, $"{gstFormat} {rate}Hz {channels}ch: no audio arrived");
        Assert.AreEqual(expected, negotiated);
        Assert.AreEqual(rate, seenRate);
        Assert.AreEqual(channels, seenChannels);
        Assert.AreEqual(0, ragged, $"{ragged} buffers were not a whole number of frames");
    }

    // ------------------------------------------------------------------ producers behaving badly

    [TestMethod]
    // With no fallback allowed, WirePlumber answers the lost target by erroring the stream: the
    // consumer being told, which is what "rather than hanging" below asks for.
    [ExpectsLibraryError("defined target not found")]
    public async Task AProducerThatDisappearsMidStream_LeavesTheConsumerUsable()
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            "pwnet-vanish",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        var frames = new Tally();
        var states = new List<PipeWireStreamState>();

        // node.dont-fallback: once its producer is gone WirePlumber would otherwise move the
        // consumer to the default video source, which is some other device (on the lab box an
        // unfed loopback camera that fails to start). Surviving the loss is the subject here, and
        // with the fallback refused the loss is reported to the stream as an error.
        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-vanish-consumer")
        {
            ExtraProperties = new Dictionary<string, string> { ["node.dont-fallback"] = "true" },
        };
        capture.FrameReady += (_, _) => frames.Increment();
        var errored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.StateChanged += (_, _, s) =>
        {
            lock (states)
                states.Add(s);
            if (s == PipeWireStreamState.Error)
                errored.TrySetResult();
        };

        uint nodeId;
        {
            await using GstTestSource src = await GstTestSource.StartAsync(
                ctx,
                "pwnet_vanish",
                "videotestsrc is-live=true ! video/x-raw,format=BGRA,width=160,height=120,framerate=30/1",
                "Video/Source"
            );
            nodeId = src.NodeId;

            capture.Connect(
                PipeWireTarget.Id(nodeId),
                new() { PreferredFormats = [PixelFormat.Bgra] }
            );
            await frames.AtLeast(3).WaitAsync(TimeSpan.FromSeconds(20), cts.Token);
        } // the producer is killed here

        int atDeath = frames.Value;
        Assert.IsTrue(atDeath > 0, "no frames arrived before the producer was killed");
        await registry
            .WaitUntilAsync(g => g.GetNode(nodeId) is null, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

        // The consumer must survive its producer, and must be told rather than left hanging: with
        // no fallback allowed, the session manager's answer to the lost target is a stream error,
        // and that has to reach this stream's state.
        await errored.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);
        lock (states)
        {
            int streaming = states.IndexOf(PipeWireStreamState.Streaming);
            int error = states.LastIndexOf(PipeWireStreamState.Error);
            Assert.IsTrue(
                streaming >= 0 && error > streaming,
                $"the consumer did not go from streaming to an error when its producer went: {string.Join(" -> ", states)}"
            );
        }

        // And it is still an object that can be torn down, which the await using below does.
    }

    [TestMethod]
    public async Task TwoConsumersOnOneProducer_BothReceive()
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            "pwnet-fanout",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        await using GstTestSource src = await GstTestSource.StartAsync(
            ctx,
            "pwnet_fanout",
            "videotestsrc is-live=true ! video/x-raw,format=BGRA,width=160,height=120,framerate=30/1",
            "Video/Source"
        );

        var a = new Tally();
        var b = new Tally();
        await using var first = new PipeWireVideoCapture(ctx, "pwnet-fanout-a");
        await using var second = new PipeWireVideoCapture(ctx, "pwnet-fanout-b");
        first.FrameReady += (_, _) => a.Increment();
        second.FrameReady += (_, _) => b.Increment();

        first.Connect(
            PipeWireTarget.Id(src.NodeId),
            new() { PreferredFormats = [PixelFormat.Bgra] }
        );
        second.Connect(
            PipeWireTarget.Id(src.NodeId),
            new() { PreferredFormats = [PixelFormat.Bgra] }
        );

        await Task.WhenAll(a.AtLeast(3), b.AtLeast(3))
            .WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

        Assert.IsTrue(a >= 3, $"first consumer only saw {a} frames");
        Assert.IsTrue(b >= 3, $"second consumer only saw {b} frames");
    }

    [TestMethod]
    public async Task AConsumerThatThrowsOnEveryFrame_DoesNotStopTheStream()
    {
        Require();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext(
            "pwnet-badconsumer",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);

        await using GstTestSource src = await GstTestSource.StartAsync(
            ctx,
            "pwnet_badconsumer",
            "videotestsrc is-live=true ! video/x-raw,format=BGRA,width=160,height=120,framerate=30/1",
            "Video/Source"
        );

        // FrameReady is raised from the data thread; an escaping exception there aborts the process.
        var seen = new Tally();
        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-badconsumer-c");
        capture.FrameReady += (_, _) =>
        {
            seen.Increment();
            throw new InvalidOperationException("hostile frame handler");
        };

        capture.Connect(
            PipeWireTarget.Id(src.NodeId),
            new() { PreferredFormats = [PixelFormat.Bgra] }
        );
        await seen.AtLeast(5).WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

        Assert.IsTrue(
            seen >= 5,
            $"the stream stopped after {seen} frames; a throwing handler must not end delivery"
        );
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<(
        int Frames,
        int Short,
        PixelFormat? Format,
        int Width,
        int Height
    )> CaptureVideoAsync(PipeWireContext ctx, uint nodeId, PixelFormat want, CancellationToken ct)
    {
        var frames = new Tally();
        var shortFrames = 0;
        PixelFormat? negotiated = null;
        int width = 0,
            height = 0;

        await using var capture = new PipeWireVideoCapture(ctx, $"pwnet-consumer-{nodeId}");
        capture.FrameReady += (_, frame) =>
        {
            frames.Increment();
            negotiated ??= frame.Format;
            width = frame.Width;
            height = frame.Height;

            // Data spans the first block only, which for a planar format is the luma plane. The
            // floor is therefore one full block, not the whole image - see the planar-host note on
            // PipeWireVideoCapture.
            int required = SpaFormatProbe.BlockSize(frame.Format, frame.Width, frame.Height);
            if (frame.Pixels.Length > 0 && frame.Pixels.Length < required)
                Interlocked.Increment(ref shortFrames);
        };

        capture.Connect(PipeWireTarget.Id(nodeId), new() { PreferredFormats = [want] });
        await frames.AtLeast(3).WaitAsync(TimeSpan.FromSeconds(20), ct);
        return (frames, shortFrames, negotiated, width, height);
    }
}

/// <summary>Exposes the size calculation so a test can check a real frame against it.</summary>
[SupportedOSPlatform("linux")]
internal static class SpaFormatProbe
{
    internal static int ImageSize(PixelFormat fmt, int width, int height) =>
        SpaFormatPod.VideoImageSize(fmt, width, height);

    internal static int BlockSize(PixelFormat fmt, int width, int height) =>
        SpaFormatPod.VideoBlockSize(fmt, width, height);
}
