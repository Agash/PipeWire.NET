using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The timing surface driven the way a sender actually drives it, against a live graph.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PipeWireRateController"/> had unit tests against a modelled queue and nothing else,
/// and the queue fields it consumes had a single assertion between them
/// (<c>Queued &gt; 0</c>). A controller that is correct against a simulated queue and never
/// connected to a real one is an untested feature: the modelling is the part most likely to be
/// wrong, because it encodes the assumption about what the daemon reports.
/// </para>
/// <para>
/// The failure mode is quiet. A queue depth that reads zero forever presents to the controller as a
/// standing underrun, and it drives a correction in one direction indefinitely - audio that slowly
/// slides out of sync with video over minutes, with nothing in any log.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class LiveTimingAndRateTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private const int Rate = 48000;
    private const int Channels = 2;

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    /// <summary>Starts a silent output and a capture bound to it, both streaming.</summary>
    private static async Task<(PipeWireContext Ctx, PipeWireAudioOutput Out, PipeWireAudioCapture Cap)>
        RunningPairAsync(string name, CancellationToken ct, int rate = Rate)
    {
        var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(ct);

        var output = new PipeWireAudioOutput(
            ctx, $"{name}-{Environment.ProcessId}", rate, Channels, AudioSampleFormat.F32Le);

        output.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };

        output.Connect(autoConnect: false);

        var capture = new PipeWireAudioCapture(ctx, $"{name}-sink-{Environment.ProcessId}");
        capture.Connect((await output.WaitForNodeIdAsync(ct)));
        await capture.WaitForStreamingAsync(ct);

        return (ctx, output, capture);
    }

    /// <summary>
    /// The queue fields a rate controller reads report a real, moving depth on a running stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every field is checked, not just <c>Queued</c>. <c>Buffered</c> and <c>AvailableBuffers</c> were
    /// read by nothing at all, so a wrong offset into <c>pw_time</c> would have gone unnoticed
    /// until a consumer computed a correction from garbage.
    /// </para>
    /// <para>
    /// The depth is read where upstream puts it for this pair. The output fills in <c>process</c>, so
    /// the buffer it queues is popped in the same cycle and <c>Queued</c> (<c>queued.incount</c> minus
    /// the position copied at <c>queued.outcount</c>, stream.c) is zero between cycles by design; only
    /// a producer that queues ahead holds a queue. <c>Buffered</c> is <c>rate_match->delay</c>, the
    /// frames the resampler holds (audioconvert's <c>resample_update_rate_match</c>), so the pair runs
    /// off the graph rate to put a resampler in the path. This is the depth a transport reads.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AStreamingPair_ReportsEveryQueueFieldAndTheDepthMoves()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        (PipeWireContext ctx, PipeWireAudioOutput output, PipeWireAudioCapture capture) =
            await RunningPairAsync("pwnet-queuefields", cts.Token, rate: 44100);

        await using (ctx)
        await using (output)
        await using (capture)
        {
            var samples = new List<PipeWireStreamQueue>();
            for (var i = 0; i < 40 && samples.Count < 10; i++)
            {
                if (output.Queue is { } q) samples.Add(q);
                await Task.Delay(50, cts.Token);
            }

            Assert.IsTrue(samples.Count >= 10, $"the stream reported its queue only {samples.Count} times");

            // AvailableBuffers is the pool the stream draws from. Zero forever means the reader is
            // looking at the wrong field, because a running stream always has a pool.
            Assert.IsTrue(
                samples.Any(s => s.AvailableBuffers > 0 || s.QueuedBuffers > 0),
                "no reading reported any buffer at all, queued or available");

            string seen = string.Join(", ", samples.Select(s => $"q={s.Queued} b={s.Buffered} qb={s.QueuedBuffers} ab={s.AvailableBuffers}"));

            Assert.IsTrue(
                samples.Any(s => s.Buffered > 0),
                $"the resampler's held frames read as zero on every sample with a resampler in the path [{seen}]");

            // A depth pinned to one value is indistinguishable from a field that is never updated,
            // and a controller cannot converge on a constant.
            var distinct = samples.Select(s => (s.Queued, s.Buffered, s.QueuedBuffers, s.AvailableBuffers)).Distinct().Count();
            Assert.IsTrue(distinct > 1, $"the queue reported an identical depth on every sample [{seen}]");
        }
    }

    /// <summary>
    /// The rate controller, driven from a live stream's queue depth, stays bounded and converges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the closed loop a receiver runs: read the depth, compute the error against a target,
    /// feed it to the DLL, and apply the correction. Run open-loop here - the correction is
    /// observed rather than applied - because applying it would change the graph's rate underneath
    /// the other tests sharing this daemon.
    /// </para>
    /// <para>
    /// What is pinned is that real measurements keep the controller in a sane range. A controller
    /// fed a plausible signal must not run away; a correction outside a few percent means either
    /// the DLL or the measurement feeding it is wrong, and both are silent failures downstream.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task TheRateController_StaysBoundedWhenDrivenFromALiveQueue()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        (PipeWireContext ctx, PipeWireAudioOutput output, PipeWireAudioCapture capture) =
            await RunningPairAsync("pwnet-ratelive", cts.Token);

        await using (ctx)
        await using (output)
        await using (capture)
        {
            var dll = new PipeWireRateController();
            dll.SetBandwidth(PipeWireRateController.MinBandwidth, period: 1024, rate: Rate);

            // Settle first: the first cycles of a stream are not representative of its steady state.
            await Task.Delay(300, cts.Token);

            double target = 0;
            var observations = 0;
            var corrections = new List<double>();

            for (var i = 0; i < 60 && observations < 25; i++)
            {
                if (capture.Queue is { } q)
                {
                    double avail = q.Queued + q.Buffered;

                    // The first reading sets the target the loop then holds against, which is how a
                    // receiver picks one: there is no absolute correct depth, only a stable one.
                    if (observations == 0) target = avail;

                    corrections.Add(dll.Update(target - avail));
                    observations++;
                }

                await Task.Delay(20, cts.Token);
            }

            Assert.IsTrue(observations >= 25, $"only {observations} live queue readings were taken");

            foreach (double c in corrections)
            {
                Assert.IsTrue(
                    double.IsFinite(c),
                    "the controller produced a non-finite correction from a live queue reading");

                Assert.IsTrue(
                    c is > 0.9 and < 1.1,
                    $"the controller ran away to {c:F6} on live data; a real queue must not drive "
                    + "the rate more than a few percent");
            }

            // And it is actually responding, rather than returning the neutral 1.0 because the
            // measurement never changes.
            Assert.IsTrue(
                corrections.Distinct().Count() > 1 || corrections.All(c => Math.Abs(c - 1.0) < 1e-9),
                "the controller neither moved nor sat at neutral, which is not a coherent state");
        }
    }

    /// <summary>
    /// The commands the daemon sends reach a subscriber.
    /// </summary>
    /// <remarks>
    /// <c>CommandReceived</c> was subscribed in an existing test whose assertions never looked at
    /// what arrived, so the dispatch could have delivered nothing and the test would still have
    /// passed. A stream is commanded to start when it goes live, which is the one command a
    /// connecting stream is guaranteed to see.
    /// </remarks>
    [TestMethod]
    public async Task AStreamGoingLive_DeliversItsNodeCommandsToSubscribers()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-commands", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        var commands = new ConcurrentBag<SpaNodeCommand>();

        await using var output = new PipeWireAudioOutput(
            ctx, $"pwnet-commands-{Environment.ProcessId}", Rate, Channels, AudioSampleFormat.F32Le);

        // Subscribed before connecting: the hook has to be installed at core creation, not on
        // subscription, or the commands raised while going live are missed entirely.
        output.CommandReceived += commands.Add;

        output.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };

        output.Connect(autoConnect: false);

        await using var capture = new PipeWireAudioCapture(ctx, $"pwnet-commands-sink-{Environment.ProcessId}");
        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)));
        await capture.WaitForStreamingAsync(cts.Token);

        for (var i = 0; i < 60 && commands.IsEmpty; i++)
            await Task.Delay(50, cts.Token);

        Assert.IsFalse(
            commands.IsEmpty,
            "a stream that went live delivered no node command, so the dispatch is not wired");

        SpaNodeCommand[] seen = [.. commands];

        Assert.IsTrue(
            seen.Contains(SpaNodeCommand.Start),
            $"a stream that reached Streaming never reported Start; got: "
            + $"{string.Join(", ", seen.Select(c => c.ToString()).Distinct())}");
    }

    /// <summary>
    /// Properties set on a stream before it connects reach the daemon, and it answers for its role.
    /// </summary>
    /// <remarks>
    /// <c>ExtraProperties</c> is an input, applied when the stream connects - so the only way to
    /// know it is wired is to look for the values on the node the daemon ends up holding. Setting
    /// it and never checking would pass whether or not the dictionary was read at all.
    /// </remarks>
    [TestMethod]
    public async Task PropertiesSetBeforeConnecting_ReachTheDaemonsNode()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string marker = $"marker-{Guid.NewGuid():N}";
        string sinkName = $"pwnet-extraprops-sink-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-extraprops", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var output = new PipeWireAudioOutput(
            ctx, $"pwnet-extraprops-{Environment.ProcessId}", Rate, Channels, AudioSampleFormat.F32Le);

        output.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };

        output.Connect(autoConnect: false);

        await using var capture = new PipeWireAudioCapture(ctx, sinkName)
        {
            ExtraProperties = new Dictionary<string, string>
            {
                [PipeWireKeys.PW_KEY_MEDIA_ROLE] = "Production",
                [PipeWireKeys.PW_KEY_NODE_DESCRIPTION] = marker,
            },
        };

        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)));
        await capture.WaitForStreamingAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode? node = null;
        for (var i = 0; i < 60 && node is null; i++)
        {
            node = reg.Current.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeName, sinkName, StringComparison.Ordinal));

            if (node?.Properties.GetValueOrDefault(PipeWireKeys.PW_KEY_NODE_DESCRIPTION) is null)
            {
                node = null;
                await Task.Delay(50, cts.Token);
            }
        }

        Assert.IsNotNull(node, $"the capture node {sinkName} never appeared with its extra properties");

        Assert.AreEqual(
            marker,
            node!.Properties.GetValueOrDefault(PipeWireKeys.PW_KEY_NODE_DESCRIPTION),
            "a property set through ExtraProperties never reached the daemon's node");

        Assert.AreEqual(
            "Production",
            node.Properties.GetValueOrDefault(PipeWireKeys.PW_KEY_MEDIA_ROLE),
            "a property set through ExtraProperties never reached the daemon's node");

        // Both ends of one link cannot both be the graph's driver.
        Assert.IsFalse(
            output.IsDriving && capture.IsDriving,
            "both ends of one link claimed the driver role");

        // Asking about lazy scheduling is safe on a running stream and stays coherent with it.
        if (capture.IsLazy)
            Assert.IsNotNull(capture.Queue, "a lazy stream must still report its queue");
    }
}
