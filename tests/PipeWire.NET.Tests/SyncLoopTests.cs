using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The sync loop end to end: announce a latency, read it back, apply a rate correction, and check
/// the graph did not starve to keep up.
/// </summary>
/// <remarks>
/// <para>
/// The pieces compose as measure -> compute -> apply -> announce. Until now each end was checked in
/// isolation and the joins were not checked at all: <c>AnnounceLatency</c>, <c>SetRate</c> and
/// <c>RateMatch</c> were all reachable from tests without a single assertion on what they did.
/// </para>
/// <para>
/// Every failure in this loop is silent. An unannounced latency is drift by construction - the rest
/// of the graph compensates for a delay it cannot see, which is to say it does not. A rate
/// correction that never reaches the daemon leaves the caller believing it is correcting. Both
/// present as audio sliding away from video over minutes, with nothing in any log.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class SyncLoopTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    private const int Rate = 48000;
    private const int Channels = 2;

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static PipeWireAudioOutput SilentOutput(PipeWireContext ctx, string nodeName)
    {
        var output = new PipeWireAudioOutput(ctx, nodeName, Rate, Channels, AudioSampleFormat.F32Le);
        output.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };

        return output;
    }

    private static async Task<PipeWireNode> WaitForNodeAsync(
        PipeWireRegistry reg, string name, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            PipeWireNode? n = reg.Current.Nodes.FirstOrDefault(
                x => string.Equals(x.NodeName, name, StringComparison.Ordinal));

            if (n is not null) return n;
            await Task.Delay(50, ct);
        }

        throw new InvalidOperationException($"node {name} never appeared");
    }

    /// <summary>
    /// A latency announced by a stream is the latency the daemon then reports for its node.
    /// </summary>
    /// <remarks>
    /// The call this closes the loop on is the one a queued transport must make. Anything holding a
    /// buffer - an encoder, a network send queue - adds delay nothing else in the graph can observe,
    /// and <c>SPA_PARAM_ProcessLatency</c> is how it is published. Reading it back off the bound
    /// node is the only way to know the announcement left the process.
    /// </remarks>
    [TestMethod]
    public async Task AnAnnouncedProcessLatency_IsWhatTheDaemonReportsForTheNode()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // Deliberately odd, so a match cannot be a default the daemon already held.
        const long announcedNs = 17_000_000;

        string nodeName = $"pwnet-latency-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-latency", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using PipeWireAudioOutput output = SilentOutput(ctx, nodeName);
        output.Connect(autoConnect: false);

        await using var capture = new PipeWireAudioCapture(ctx, $"{nodeName}-sink");
        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)));
        await capture.WaitForStreamingAsync(cts.Token);

        output.AnnounceLatency(
            new PipeWireLatency(SpaDirection.Output, 0, 0, 0, 0, announcedNs, announcedNs),
            new PipeWireProcessLatency(0, 0, announcedNs));

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);
        PipeWireNode node = await WaitForNodeAsync(reg, nodeName, cts.Token);

        await using PipeWireNodeProxy control = reg.BindNode(node.NodeId);

        PipeWireProcessLatency? reported = null;
        for (var i = 0; i < 40 && reported?.Ns != announcedNs; i++)
        {
            reported = await control.GetProcessLatencyAsync(cts.Token);
            if (reported?.Ns != announcedNs) await Task.Delay(100, cts.Token);
        }

        Assert.IsNotNull(reported, "the daemon reported no process latency for a node that announced one");

        Assert.AreEqual(
            announcedNs,
            reported!.Ns,
            "the announced latency never reached the daemon, so the graph is compensating for a "
            + "delay it cannot see");
    }

    /// <summary>
    /// A resampling stream is told its rate match, and the values are usable.
    /// </summary>
    /// <remarks>
    /// <c>SPA_IO_RateMatch</c> is the area the daemon writes a resampler's delay and rate into, and
    /// the whole <see cref="PipeWireRateMatch"/> type had no test mentioning it. A stream whose
    /// sample rate differs from the graph's gets one; asking for a rate the graph is unlikely to be
    /// running at is what makes the resampler appear.
    /// </remarks>
    [TestMethod]
    public async Task AResamplingStream_IsGivenARateMatchArea()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // 44100 against a graph that almost always runs at 48000, so a resampler is inserted.
        const int offRate = 44100;

        string nodeName = $"pwnet-ratematch-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-ratematch", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var output = new PipeWireAudioOutput(
            ctx, nodeName, offRate, Channels, AudioSampleFormat.F32Le);

        // Real samples, so the resampler has something to resample and the flow can be asserted.
        uint n = 0;
        output.FillSamples += (_, samples, _, _, _) =>
        {
            Span<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(samples);
            for (var i = 0; i < floats.Length; i++) floats[i] = ++n;
            return samples.Length;
        };

        output.Connect(autoConnect: false);

        long receivedSamples = 0;
        await using var capture = new PipeWireAudioCapture(ctx, $"{nodeName}-sink");
        capture.FrameReady += (_, f) => Interlocked.Add(ref receivedSamples, f.Samples.Length / 4);
        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)));
        await capture.WaitForStreamingAsync(cts.Token);

        PipeWireRateMatch? match = null;
        for (var i = 0; i < 60 && match is null; i++)
        {
            match = output.RateMatch ?? capture.RateMatch;
            if (match is null) await Task.Delay(50, cts.Token);
        }

        // Not a skip: the sessions this runs in fix the graph at 48000 (clock.allowed-rates), so a
        // 44100 stream is always resampled and a missing area is the reader failing, not the graph.
        Assert.IsNotNull(match, "a 44100 stream in a 48000 graph was offered no rate-match area");

        // The area is set up with the link, a moment before the first cycle delivers anything.
        for (var i = 0; i < 60 && Interlocked.Read(ref receivedSamples) == 0; i++)
            await Task.Delay(50, cts.Token);

        Assert.IsTrue(
            match!.Value.Rate is > 0.5 and < 2.0,
            $"the rate-match ratio reads {match.Value.Rate}, which is not a resampling ratio - the "
            + "io area is being read at the wrong offset");

        Assert.IsTrue(
            match.Value.Size < 1_000_000,
            $"the rate-match size reads {match.Value.Size}, which is not a quantum");

        // And the resampler was actually carrying audio while it reported that.
        Assert.IsTrue(
            Interlocked.Read(ref receivedSamples) > 0,
            "the rate-match area was reported on a stream through which nothing flowed");
    }

    /// <summary>The frames one stream handled over a window, at each of three corrections.</summary>
    private sealed record RateSweep(long Neutral, long Below, long Above)
    {
        public override string ToString() => $"0.85: {Below}, 1.0: {Neutral}, 1.15: {Above}";
    }

    /// <summary>
    /// Runs an off-rate pair, applies 1.0, 0.85 and 1.15 to one of its streams, and counts what that
    /// stream's application side handled in a fixed window at each.
    /// </summary>
    /// <remarks>
    /// Off the graph rate so a resampler is in the path: <c>pw_stream_set_rate</c> only sets
    /// <c>rate_match->rate</c> (stream.c), and without a resampler there is nothing to read it.
    /// </remarks>
    private static async Task<RateSweep> SweepAsync(string name, bool correctTheOutput, CancellationToken ct)
    {
        const int offRate = 44100;
        string nodeName = $"{name}-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(ct);

        long framesAsked = 0, framesHanded = 0;

        await using var output = new PipeWireAudioOutput(
            ctx, nodeName, offRate, Channels, AudioSampleFormat.F32Le);
        output.FillSamples += (_, samples, _, _, _) =>
        {
            Interlocked.Add(ref framesAsked, samples.Length / (4 * Channels));
            samples.Clear();
            return samples.Length;
        };
        output.Connect(autoConnect: false);

        await using var capture = new PipeWireAudioCapture(ctx, $"{nodeName}-sink");
        capture.FrameReady += (_, f) => Interlocked.Add(ref framesHanded, f.FrameCount);
        capture.Connect((await output.WaitForNodeIdAsync(ct)), sampleRate: offRate, channels: Channels,
            format: AudioSampleFormat.F32Le);

        await capture.WaitForStreamingAsync(ct);
        await Task.Delay(400, ct);

        if ((correctTheOutput ? output.RateMatch : capture.RateMatch) is null)
            Assert.Fail("no resampler on the corrected stream, although it runs off the graph rate");

        long Read() => Interlocked.Read(ref correctTheOutput ? ref framesAsked : ref framesHanded);

        async Task<long> MeasureAt(double rate)
        {
            if (correctTheOutput) output.SetRate(rate); else capture.SetRate(rate);
            await Task.Delay(250, ct);
            long before = Read();
            await Task.Delay(TimeSpan.FromMilliseconds(900), ct);
            return Read() - before;
        }

        long neutral = await MeasureAt(1.0);
        long below = await MeasureAt(0.85);
        long above = await MeasureAt(1.15);
        if (correctTheOutput) output.SetRate(1.0); else capture.SetRate(1.0);

        Assert.IsTrue(neutral > 0, "nothing flowed at the neutral rate");
        return new RateSweep(neutral, below, above);
    }

    /// <summary>
    /// A correction on a playback stream changes how many frames its producer is asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The apply step, shown by its effect rather than by the stream surviving, on the stream kind
    /// upstream's tunnels correct (module-pipe-tunnel, module-rtp: <c>pw_stream_set_rate</c> on their
    /// playback stream). audioconvert asks the application for <c>resample_in_len</c> of the quantum,
    /// and resample-native divides the input rate by the correction, so below 1.0 the producer is
    /// asked for more frames per cycle and above it for fewer.
    /// </para>
    /// <para>
    /// This test used to set the rate on the capture and count what the producer was asked for. A
    /// capture's correction scales only what that capture is handed; the producer's side of the graph
    /// never sees it, so the count could not move.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task ARateCorrectionOnAPlaybackStream_ScalesWhatItsProducerIsAskedFor()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        RateSweep r = await SweepAsync("pwnet-setrate-out", correctTheOutput: true, cts.Token);

        // 15% either way, so a real effect is far outside cycle-to-cycle jitter.
        Assert.IsTrue(r.Below > r.Neutral * 1.05, $"0.85 did not ask the producer for more [{r}]");
        Assert.IsTrue(r.Above < r.Neutral * 0.95, $"1.15 did not ask the producer for less [{r}]");
    }

    /// <summary>
    /// A correction on a capture stream changes how many frames it is handed.
    /// </summary>
    /// <remarks>
    /// The other direction of the same resampler: a capture's audioconvert produces
    /// <c>resample_out_len</c> of what the graph gives it, so below 1.0 it is handed fewer frames and
    /// above it more. What a receiver draining a jitter buffer through a capture relies on.
    /// </remarks>
    [TestMethod]
    public async Task ARateCorrectionOnACaptureStream_ScalesWhatItIsHanded()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        RateSweep r = await SweepAsync("pwnet-setrate-in", correctTheOutput: false, cts.Token);

        Assert.IsTrue(r.Below < r.Neutral * 0.95, $"0.85 did not hand the capture fewer frames [{r}]");
        Assert.IsTrue(r.Above > r.Neutral * 1.05, $"1.15 did not hand the capture more frames [{r}]");
    }

    /// <summary>
    /// A streaming pair runs without the graph accumulating xruns, per <c>pw-top</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The independent check on everything else here. Every other assertion is made from inside this
    /// process, about data this library handed itself. <c>pw-top</c> is the daemon's own account,
    /// and its <c>ERR</c> column counts the cycles a node missed its deadline.
    /// </para>
    /// <para>
    /// That matters because starvation does not look like failure from the consumer side: frames
    /// still arrive, still carry ordered timestamps, and still line up. A test that only reads
    /// frames cannot tell a healthy graph from one that is silently dropping cycles to stay
    /// apparently on time.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AStreamingPair_RunsWithoutAccumulatingXruns()
    {
        RequireLinux();
        PwTop.Require();

        using var cts = new CancellationTokenSource(Budget);

        string nodeName = $"pwnet-xrun-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-xrun", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using PipeWireAudioOutput output = SilentOutput(ctx, nodeName);
        output.Connect(autoConnect: false);

        await using var capture = new PipeWireAudioCapture(ctx, $"{nodeName}-sink");
        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)));
        await capture.WaitForStreamingAsync(cts.Token);

        uint nodeId = (await output.WaitForNodeIdAsync(cts.Token));

        // Settle, then take a baseline: a node's counter is cumulative and the first cycles after a
        // connect legitimately miss while the graph re-negotiates its quantum.
        await Task.Delay(700, cts.Token);
        long? baseline = await PwTop.ErrorsForAsync(nodeId, cts.Token);

        Assert.IsNotNull(baseline, $"pw-top did not report node {nodeId}, which is streaming");

        await Task.Delay(2000, cts.Token);
        long? after = await PwTop.ErrorsForAsync(nodeId, cts.Token);

        Assert.IsNotNull(after, "pw-top stopped reporting the node while it was still streaming");

        Assert.AreEqual(
            baseline!.Value,
            after!.Value,
            $"the node accumulated {after.Value - baseline.Value} xruns over two seconds of steady "
            + "streaming; frames may line up, but the graph is missing its deadline to make that happen");
    }

    /// <summary>
    /// Draining plays out what was queued instead of dropping it.
    /// </summary>
    /// <remarks>
    /// The difference between stopping and ending. The producer writes a finite ramp and then stops
    /// producing; draining must deliver the tail of that ramp to the consumer. Asserting only that
    /// <c>DrainAsync</c> returns would pass if it dropped everything still queued, which is exactly
    /// the truncation it exists to prevent.
    /// </remarks>
    [TestMethod]
    public async Task DrainingAnOutput_DeliversTheTailRatherThanDroppingIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string nodeName = $"pwnet-drain-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-drain", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        // A finite ramp: the producer emits values 1..N and then silence.
        const long total = 40000;
        long produced = 0;

        await using var output = new PipeWireAudioOutput(
            ctx, nodeName, Rate, 1, AudioSampleFormat.F32Le);

        output.FillSamples += (_, samples, _, _, _) =>
        {
            Span<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(samples);
            for (var i = 0; i < floats.Length; i++)
            {
                long n = Interlocked.Increment(ref produced);
                floats[i] = n <= total ? n : 0f;
            }

            return samples.Length;
        };

        output.Connect(autoConnect: false);

        float highestSeen = 0;
        await using var capture = new PipeWireAudioCapture(ctx, $"{nodeName}-sink");
        capture.FrameReady += (_, f) =>
        {
            ReadOnlySpan<float> floats =
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(f.Samples);

            foreach (float v in floats)
            {
                if (v > Volatile.Read(ref highestSeen)) Volatile.Write(ref highestSeen, v);
            }
        };

        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)), sampleRate: Rate, channels: 1,
            format: AudioSampleFormat.F32Le);

        await capture.WaitForStreamingAsync(cts.Token);

        // Wait until the producer has emitted the whole ramp, then drain.
        for (var i = 0; i < 200 && Interlocked.Read(ref produced) < total; i++)
            await Task.Delay(20, cts.Token);

        Assert.IsTrue(
            Interlocked.Read(ref produced) >= total,
            "the producer never got through the ramp, so there was no tail to drain");

        await output.DrainAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(15), cts.Token);

        // Give the consumer a moment to be handed what the drain flushed.
        await Task.Delay(200, cts.Token);

        float seen = Volatile.Read(ref highestSeen);

        Assert.IsTrue(
            seen > 0,
            "the consumer received none of the ramp at all");

        // The tail: within one quantum of the end of the ramp. A drain that dropped the queue
        // would leave the consumer short by however much was still buffered.
        Assert.IsTrue(
            seen >= total - 4096,
            $"the consumer's last sample was {seen} of {total}; the drain dropped the tail");

        Assert.IsNotNull(output.Queue, "the stream stopped answering after being drained");
    }
}
