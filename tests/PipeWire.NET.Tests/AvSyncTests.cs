using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// Whether audio and video published together arrive on one timeline, whether a desync between
/// them survives the trip through the daemon and can be measured, and whether one can be driven
/// back out.
/// </summary>
/// <remarks>
/// <para>
/// Aligned on per-buffer time, which for the two legs means two different things. Video carries the
/// producer's <c>SPA_META_Header</c> <c>pts</c> through the graph intact, which is what upstream's
/// video-play-sync aligns on. Audio does not: no audio converter or mixer copies that header, so an
/// audio consumer gets the graph cycle time its buffer was queued in (<c>pw_buffer.time</c>), the same
/// time pipewiresrc falls back to. The two meet on one timeline when the video producer stamps in
/// the graph's clock - <c>pw_stream_get_nsec</c>, CLOCK_MONOTONIC, this library's default - and not
/// otherwise.
/// </para>
/// <para>
/// The producer is this library's own audio and video outputs, in one process, which is the shape
/// of a transport republishing a remote peer's media: one publisher, one timeline, two streams. It
/// is this library on both ends so the offset and correction tests can inject exactly what they
/// measure. The interop half is
/// <see cref="OneGStreamerPipeline_VideoCarriesItsRunningTimeAndAudioTheGraphs"/>: against an
/// independent producer the two legs arrive in different domains, by upstream's design, and that test
/// pins what each one is.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class AvSyncTests
{
    private const int Width = 160,
        Height = 120,
        FrameRate = 30;
    private const int Rate = 48000;
    private const int Cap = 128;

    /// <summary>
    /// What each leg delivered: its per-buffer time (the header timestamp for video, the queued time for
    /// audio), and when this process received it.
    /// </summary>
    private sealed record Leg(long[] Time, long[] ReceivedNs, long[] Delay, long[] Clock);

    private sealed record Capture(Leg Video, Leg Audio, bool VideoDrove);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    /// <summary>CLOCK_MONOTONIC in nanoseconds - the clock pw_stream_get_nsec reads.</summary>
    private static long NowNs() =>
        (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000_000 / Stopwatch.Frequency);

    /// <summary>
    /// Publishes audio and video from this process and captures both, over a real streaming window.
    /// </summary>
    /// <param name="name">Client name, also the prefix of both published streams.</param>
    /// <param name="videoOffsetNs">
    /// Null to let both outputs stamp the current stream time themselves. Otherwise both stamp from
    /// one publisher clock - the way a transport republishing media supplies times it already has -
    /// with the video shifted by this much. The video, because it is the leg whose header timestamp
    /// crosses the daemon: no audio converter or mixer copies <c>spa_meta_header.pts</c>, so an audio
    /// consumer is handed the graph's cycle time whatever the producer stamped.
    /// </param>
    /// <param name="ct">Cancels the capture.</param>
    private static async Task<Capture> CaptureBothLegsAsync(
        string name,
        long? videoOffsetNs,
        CancellationToken ct
    )
    {
        await using var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(ct);

        await using var video = new PipeWireVideoOutput(
            ctx,
            $"{name}-v",
            Width,
            Height,
            PixelFormat.Bgra,
            FrameRate
        );
        video.FillFrame += (sender, pixels, _, _, _, _) =>
        {
            pixels.Fill(0x40);
            if (videoOffsetNs is { } offset)
                sender.NextPresentationTimestampNs = NowNs() + offset;
            return true;
        };

        await using var audio = new PipeWireAudioOutput(
            ctx,
            $"{name}-a",
            Rate,
            2,
            AudioSampleFormat.F32Le
        );
        audio.FillSamples += (sender, samples, _, _, _) =>
        {
            MemoryMarshal.Cast<byte, float>(samples).Fill(0.25f);
            if (videoOffsetNs is not null)
                sender.NextPresentationTimestampNs = NowNs();
            return samples.Length;
        };

        video.Connect(autoConnect: false);
        audio.Connect(autoConnect: false);

        uint videoNode = await video.WaitForNodeIdAsync(ct);
        uint audioNode = await audio.WaitForNodeIdAsync(ct);

        var v = new List<(long Time, long Rx, long Delay, long Clock)>();
        var a = new List<(long Time, long Rx, long Delay, long Clock)>();

        await using var vCap = new PipeWireVideoCapture(ctx, $"{name}-v-sink");
        vCap.FrameReady += (_, f) =>
        {
            long rx = NowNs();
            lock (v)
            {
                if (v.Count < Cap && f.PresentationTimestampNs is { } pts)
                    v.Add((pts, rx, f.DelayNs, f.GraphTimeNs ?? -1));
            }
        };
        vCap.Connect(videoNode, stackalloc[] { PixelFormat.Bgra });

        await using var aCap = new PipeWireAudioCapture(ctx, $"{name}-a-sink");
        aCap.FrameReady += (_, f) =>
        {
            long rx = NowNs();
            lock (a)
            {
                if (a.Count < Cap && f.QueuedTimeNs is { } pts)
                    a.Add((pts, rx, f.DelayNs, f.GraphTimeNs ?? -1));
            }
        };
        aCap.Connect(audioNode, sampleRate: Rate, channels: 2, format: AudioSampleFormat.F32Le);

        await video.WaitForStreamingAsync(ct);
        video.DriveAt(TimeSpan.FromSeconds(1.0 / FrameRate));

        // Until both legs have delivered enough buffers whose timestamps actually moved. A count
        // alone is not enough: a burst delivered in one cycle satisfies it while spanning no time.
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            bool vReady,
                aReady;
            lock (v)
                vReady = v.Count >= 24 && v[^1].Time > v[0].Time;
            lock (a)
                aReady = a.Count >= 24 && a[^1].Time > a[0].Time;
            if (vReady && aReady)
                break;
            await Task.Delay(100, ct);
        }

        bool drove = video.IsDriving;

        Leg Snapshot(List<(long Time, long Rx, long Delay, long Clock)> xs)
        {
            lock (xs)
            {
                return new Leg(
                    [.. xs.Select(x => x.Time)],
                    [.. xs.Select(x => x.Rx)],
                    [.. xs.Select(x => x.Delay)],
                    [.. xs.Select(x => x.Clock)]
                );
            }
        }

        Leg videoLeg = Snapshot(v),
            audioLeg = Snapshot(a);

        // Buffers without their leg's time are not collected at all, so a producer that stopped
        // stamping shows up here as a leg that never filled rather than as a wrong number later.
        Assert.IsTrue(
            videoLeg.Time.Length >= 24,
            $"only {videoLeg.Time.Length} video frames carried a presentation timestamp"
        );
        Assert.IsTrue(
            audioLeg.Time.Length >= 24,
            $"only {audioLeg.Time.Length} audio buffers carried a queued time"
        );

        return new Capture(videoLeg, audioLeg, drove);
    }

    private static long Median(IEnumerable<long> xs)
    {
        long[] sorted = [.. xs.Order()];
        return sorted[sorted.Length / 2];
    }

    /// <summary>
    /// How far a leg's timestamps sit from the moment its buffers arrived, as a median.
    /// </summary>
    /// <remarks>
    /// The difference of this between the legs is the offset a consumer sees: the injected offset,
    /// plus whatever latency the two legs do not share. Taking it per buffer against arrival rather
    /// than comparing raw timestamp medians is what keeps two windows captured at slightly different
    /// moments from reading as an offset.
    /// </remarks>
    private static long LeadNs(Leg leg) => Median(leg.Time.Zip(leg.ReceivedNs, (p, r) => p - r));

    /// <summary>What a leg delivered, for a failure message that says which leg went wrong and how.</summary>
    private static string Describe(string name, Leg leg) =>
        leg.Time.Length == 0
            ? $"{name}: nothing"
            : $"{name}: n={leg.Time.Length} pts {leg.Time[0]}..{leg.Time[^1]} "
                + $"(span {(leg.Time[^1] - leg.Time[0]) / 1e6:F1}ms) lead {LeadNs(leg) / 1e6:F1}ms";

    /// <summary>
    /// Audio and video published together, stamped by the outputs themselves, arrive on one timeline.
    /// </summary>
    /// <remarks>
    /// Nothing is supplied here: both outputs stamp <c>pw_stream_get_nsec()</c>, which is what
    /// upstream's video-src stamps. That is the property a publisher that does not manage time
    /// itself relies on - that the library's defaults already line the two streams up.
    /// </remarks>
    [TestMethod]
    public async Task AudioAndVideoPublishedTogether_ArriveOnOneTimeline()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Capture c = await CaptureBothLegsAsync("pwnet-avsync", videoOffsetNs: null, cts.Token);

        long videoFrom = c.Video.Time.Min(),
            videoTo = c.Video.Time.Max();
        long audioFrom = c.Audio.Time.Min(),
            audioTo = c.Audio.Time.Max();

        Assert.IsTrue(videoTo > videoFrom, "the video timestamps never advanced");
        Assert.IsTrue(audioTo > audioFrom, "the audio timestamps never advanced");

        long overlap = Math.Min(videoTo, audioTo) - Math.Max(videoFrom, audioFrom);
        Assert.IsTrue(
            overlap > 0,
            $"the legs' timestamp windows do not meet (video {videoFrom}..{videoTo}, audio {audioFrom}..{audioTo})"
        );

        // Both were stamped from one clock at production, so what separates them is latency the
        // legs do not share - a quantum or a frame, not hundreds of milliseconds.
        long skew = LeadNs(c.Audio) - LeadNs(c.Video);
        Assert.IsTrue(
            Math.Abs(skew) < 100_000_000,
            $"audio and video stamped by the same process sit {skew / 1e6:F1}ms apart"
        );

        // The graph clock the video capture reports must advance as well when this library drove
        // the graph: a driving stream has to publish the clock itself, and before that was done
        // every consumer of a graph driven from here saw it frozen.
        if (c.VideoDrove)
        {
            long[] clock = [.. c.Video.Clock.Where(x => x >= 0)];
            Assert.IsTrue(
                clock.Length > 1 && clock.Max() > clock.Min(),
                "this library drove the video graph, but the graph clock its consumer saw never advanced"
            );
        }
    }

    /// <summary>
    /// A desync published into the video's timestamps is recovered, to the millisecond, by the consumer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The offset is applied where a real one would be: at publish time, in the header timestamps a
    /// transport supplies. It then crosses the daemon in the buffers' metadata and is recovered from
    /// what the consumer received, so this measures the mechanism rather than arithmetic on a
    /// captured array.
    /// </para>
    /// <para>
    /// In the video, because that is the leg whose header crosses the graph. An audio buffer passes
    /// through converters and mixers, none of which copy <c>spa_meta_header.pts</c> (the only writers
    /// upstream are producers: alsa-pcm, bluez5, v4l2, pipewiresink), so an audio consumer is handed
    /// the cycle time the buffer was queued in, <c>pw_buffer.time</c>, whatever the producer stamped.
    /// Measured on the lab box: a 250ms offset stamped into the audio arrived as 0.0ms.
    /// </para>
    /// <para>
    /// A baseline run with no offset is subtracted. What remains is exactly the injected offset,
    /// because everything else - the legs' differing latency - is common to both runs.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AnOffsetPublishedIntoTheVideo_IsRecoveredByTheConsumer()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        Capture baseline = await CaptureBothLegsAsync("pwnet-avbase", videoOffsetNs: 0, cts.Token);
        long baseSkew = LeadNs(baseline.Video) - LeadNs(baseline.Audio);

        foreach (long injected in new[] { 250_000_000L, -100_000_000L })
        {
            Capture c = await CaptureBothLegsAsync(
                $"pwnet-avoff{(injected > 0 ? "p" : "n")}",
                injected,
                cts.Token
            );
            long recovered = LeadNs(c.Video) - LeadNs(c.Audio) - baseSkew;

            Assert.AreEqual(
                injected / 1e6,
                recovered / 1e6,
                15.0,
                $"a {injected / 1e6:F0}ms offset published into the video came back as {recovered / 1e6:F1}ms; "
                    + $"baseline [{Describe("audio", baseline.Audio)}; {Describe("video", baseline.Video)}], "
                    + $"offset run [{Describe("audio", c.Audio)}; {Describe("video", c.Video)}]"
            );
        }
    }

    /// <summary>
    /// A measured desync is driven back to zero when the correction is applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loop a receiver runs to hold lip-sync: measure the offset, feed it to the controller,
    /// consume at the corrected rate, measure again. Run over the real inter-arrival cadence of the
    /// audio leg, so the controller settles against the timing a live graph actually produces.
    /// </para>
    /// <para>
    /// The correction is not applied to the daemon. Changing the graph's rate would reach every
    /// other test sharing this session, and what is being tested is the control law against real
    /// timing, not <c>pw_stream_set_rate</c>.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task ADesyncedStream_IsDrivenBackIntoSync()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Capture c = await CaptureBothLegsAsync("pwnet-avresync", videoOffsetNs: null, cts.Token);

        // The real cadence: how far apart consecutive audio buffers were stamped, in samples.
        var periods = new List<double>();
        for (var i = 1; i < c.Audio.Time.Length; i++)
        {
            double d = (c.Audio.Time[i] - c.Audio.Time[i - 1]) / 1e9 * Rate;
            if (d is > 0 and < 1e6)
                periods.Add(d);
        }

        Assert.IsTrue(
            periods.Count > 5,
            "the audio leg produced no usable cadence to drive against"
        );
        double period = periods.Sum() / periods.Count;

        var dll = new PipeWireRateController();
        dll.SetBandwidth(PipeWireRateController.MinBandwidth, period: (int)period, rate: Rate);

        // A quarter second of audio late, expressed in samples - a desync nobody would miss.
        double offset = 0.25 * Rate;
        double startingOffset = offset;

        for (var i = 0; i < 20000; i++)
        {
            double correction = dll.Update(offset);

            Assert.IsTrue(
                double.IsFinite(correction),
                $"the controller produced a non-finite correction at cycle {i}"
            );

            // Consumed at the corrected rate against a nominal arrival, which is what applying the
            // correction to the stream would do.
            offset -= period - (period * correction);
        }

        Assert.IsTrue(
            Math.Abs(offset) < Math.Abs(startingOffset),
            $"the correction made the desync worse: {startingOffset / Rate * 1000:F1}ms became "
                + $"{offset / Rate * 1000:F1}ms - the sign is inverted"
        );

        Assert.AreEqual(
            0.0,
            offset / Rate,
            0.005,
            $"a 250ms desync did not close; {offset / Rate * 1000:F1}ms remains"
        );
    }

    /// <summary>
    /// The reported delay is usable as a presentation offset rather than merely non-negative.
    /// </summary>
    /// <remarks>
    /// <c>DelayNs</c> is how far ahead of presentation a buffer was handed over. A consumer adds it to
    /// the buffer's timestamp to get the time it should actually be presented; if it were garbage,
    /// that conversion would reorder frames, which is worse than ignoring it. Based on the header
    /// timestamp, which advances per buffer, rather than on the graph clock, which a frozen driver
    /// would leave constant and turn every ordering check into a tautology.
    /// </remarks>
    [TestMethod]
    public async Task ApplyingTheReportedDelay_KeepsPresentationOrder()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Capture c = await CaptureBothLegsAsync("pwnet-avdelay", videoOffsetNs: null, cts.Token);

        foreach ((string name, Leg leg) in new[] { ("video", c.Video), ("audio", c.Audio) })
        {
            var presentation = new long[leg.Time.Length];
            for (var i = 0; i < leg.Time.Length; i++)
            {
                Assert.IsTrue(leg.Delay[i] >= 0, $"{name}: a negative delay at buffer {i}");

                // A delay larger than a second is not a latency, it is a misread field.
                Assert.IsTrue(
                    leg.Delay[i] < 1_000_000_000,
                    $"{name}: a delay of {leg.Delay[i] / 1e6:F1}ms is not a plausible latency"
                );

                presentation[i] = leg.Time[i] + leg.Delay[i];
            }

            for (var i = 1; i < presentation.Length; i++)
            {
                Assert.IsTrue(
                    presentation[i] >= presentation[i - 1],
                    $"{name}: applying the reported delay reordered buffers {i - 1} and {i}"
                );
            }
        }
    }

    /// <summary>
    /// From one GStreamer pipeline, the video arrives in the producer's own time and the audio in the
    /// graph's - the two domains upstream actually delivers, which a consumer has to bridge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interop proof the other tests here cannot give, since both their ends are this library, and
    /// what it proves is what an A/V consumer of an independent producer gets. pipewiresink stamps
    /// every buffer's header with <c>GST_BUFFER_PTS</c> - the pipeline's running time, which starts near
    /// zero - and the video header reaches the consumer intact. The audio header does not survive the
    /// graph: no converter or mixer copies it, so the audio consumer gets the cycle time its buffers
    /// were queued in (<c>pw_buffer.time</c>, CLOCK_MONOTONIC), the same fallback pipewiresrc uses.
    /// </para>
    /// <para>
    /// So the two legs of one pipeline sit in different domains by upstream's design, and a consumer
    /// that wants them on one timeline needs the producer's base time, which PipeWire does not carry.
    /// Asserted as facts, because each is something such a consumer depends on: the video times are
    /// the running time (small, advancing at wall rate), the audio times are the graph's (close to
    /// when this process received them).
    /// </para>
    /// <para>
    /// A leg that delivers nothing is a failure, not a skip: the producer is started and targeted, so
    /// silence is either the one-pipeline preroll problem <c>StartOnePipelineAsync</c> describes or a
    /// defect in the captures.
    /// </para>
    /// </remarks>
    [TestMethod]
    [TestCategory("RequiresGStreamer")]
    public async Task OneGStreamerPipeline_VideoCarriesItsRunningTimeAndAudioTheGraphs()
    {
        RequireLinux();
        GstTestSource.RequireGStreamer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        string name = $"pwnet-gstav-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        (GstTestSource source, uint videoNode, uint audioNode) =
            await GstTestSource.StartOnePipelineAsync(
                ctx,
                (
                    $"videotestsrc is-live=true ! video/x-raw,format=BGRx,width={Width},height={Height},framerate={FrameRate}/1",
                    $"{name}-v"
                ),
                (
                    $"audiotestsrc is-live=true ! audio/x-raw,format=F32LE,rate={Rate},channels=2",
                    $"{name}-a"
                )
            );
        await using (source)
        {
            var v = new List<(long Time, long Rx)>();
            var a = new List<(long Time, long Rx)>();

            await using var vCap = new PipeWireVideoCapture(ctx, $"{name}-v-sink");
            vCap.FrameReady += (_, f) =>
            {
                long rx = NowNs();
                lock (v)
                {
                    if (v.Count < Cap && f.PresentationTimestampNs is { } pts)
                        v.Add((pts, rx));
                }
            };
            vCap.Connect(videoNode, stackalloc[] { PixelFormat.Bgrx });

            await using var aCap = new PipeWireAudioCapture(ctx, $"{name}-a-sink");
            aCap.FrameReady += (_, f) =>
            {
                long rx = NowNs();
                lock (a)
                {
                    if (a.Count < Cap && f.QueuedTimeNs is { } pts)
                        a.Add((pts, rx));
                }
            };
            aCap.Connect(audioNode, sampleRate: Rate, channels: 2, format: AudioSampleFormat.F32Le);

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                bool vReady,
                    aReady;
                lock (v)
                    vReady = v.Count >= 24 && v[^1].Time > v[0].Time;
                lock (a)
                    aReady = a.Count >= 24 && a[^1].Time > a[0].Time;
                if (vReady && aReady)
                    break;
                await Task.Delay(100, cts.Token);
            }

            Leg Snapshot(List<(long Time, long Rx)> xs)
            {
                lock (xs)
                    return new Leg([.. xs.Select(x => x.Time)], [.. xs.Select(x => x.Rx)], [], []);
            }

            Leg video = Snapshot(v),
                audio = Snapshot(a);
            string seen = $"[{Describe("audio", audio)}; {Describe("video", video)}]";

            Assert.IsTrue(
                video.Time.Length >= 24,
                $"the pipeline's video leg delivered only {video.Time.Length} stamped frames {seen}"
            );
            Assert.IsTrue(
                audio.Time.Length >= 24,
                $"the pipeline's audio leg delivered only {audio.Time.Length} stamped buffers {seen}"
            );

            // Video: GStreamer's running time, carried through intact. It starts when the pipeline
            // started, so it is small, and it advances with the wall clock.
            long videoSpan = video.Time[^1] - video.Time[0];
            long videoWall = video.ReceivedNs[^1] - video.ReceivedNs[0];
            Assert.IsTrue(
                video.Time[0] < 60_000_000_000L,
                $"the video times are not GStreamer's running time - a pipeline seconds old reports {video.Time[0] / 1e9:F1}s {seen}"
            );
            Assert.IsTrue(
                Math.Abs(videoSpan - videoWall) < 100_000_000,
                $"the video times advanced {videoSpan / 1e6:F0}ms over {videoWall / 1e6:F0}ms of arrivals {seen}"
            );

            // Audio: the graph's time, so within a cycle or two of when this process got the buffer.
            Assert.IsTrue(
                Math.Abs(LeadNs(audio)) < 100_000_000,
                $"the audio times are not the graph's cycle time: they sit {LeadNs(audio) / 1e6:F1}ms from arrival {seen}"
            );
        }
    }
}
