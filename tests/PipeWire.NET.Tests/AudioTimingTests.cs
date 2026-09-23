using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// The timing an audio stream reports, which is what anything aligning audio with video depends on.
/// </summary>
/// <remarks>
/// PipeWire audio carries no per-buffer header PTS, so a consumer reading only the header sees no
/// timestamp on any audio frame ever. Everything here exists because that failure is invisible: the
/// audio plays, the frames arrive, and only the attempt to line them up against video shows that
/// there was never anything to line up with.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class AudioTimingTests : PipeWireTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private const int Rate = 48000;
    private const int Channels = 2;
    private const AudioSampleFormat Format = AudioSampleFormat.F32Le;

    private static async Task<PipeWireGraphSnapshot> WaitForAsync(
        PipeWireRegistry registry,
        Func<PipeWireGraphSnapshot, bool> until,
        CancellationToken ct
    )
    {
        await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(ct))
            if (until(graph))
                return graph;

        throw new InvalidOperationException("the snapshot stream ended before the condition held");
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    /// <summary>
    /// Every audio frame carries the cycle time it was queued in, it advances, and the header
    /// timestamp the output wrote does not survive the converters in between.
    /// </summary>
    /// <remarks>
    /// The queued time is what an audio consumer aligns on, so a regression here does not throw - it
    /// reports null on every frame, and A/V sync silently becomes impossible. The header half pins why
    /// the two are separate properties: the output stamps every buffer, and still no audio consumer
    /// sees it, because audioconvert does not copy <c>spa_meta_header</c>.
    /// </remarks>
    [TestMethod]
    public async Task AudioFrames_CarryAdvancingQueuedTimesAndNoHeaderTimestamp()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext(
            "pwnet-audiots",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        string nodeName = $"pwnet-audiots-{Environment.ProcessId}";
        await using var output = new PipeWireAudioOutput(ctx, nodeName, Rate, Channels, Format);
        output.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();
            return samples.Length;
        };
        output.Connect(autoConnect: false);

        await WaitForAsync(reg, g => g.Nodes.Any(n => n.NodeName == nodeName), cts.Token);

        var stamps = new List<long>();
        var missing = 0;
        var headers = 0;

        await using var capture = new PipeWireAudioCapture(
            ctx,
            $"pwnet-audiots-sink-{Environment.ProcessId}"
        );
        capture.FrameReady += (_, frame) =>
        {
            if (frame.PresentationTimestampNs is not null)
                Interlocked.Increment(ref headers);
            if (frame.QueuedTimeNs is { } ts && ts > 0)
            {
                lock (stamps)
                {
                    if (stamps.Count < 32)
                        stamps.Add(ts);
                }
            }
            else
            {
                Interlocked.Increment(ref missing);
            }
        };

        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)));
        await capture.WaitForStreamingAsync(cts.Token);

        for (int i = 0; i < 60; i++)
        {
            lock (stamps)
            {
                if (stamps.Count >= 8)
                    break;
            }
            await Task.Delay(50, cts.Token);
        }

        long[] seen;
        lock (stamps)
            seen = [.. stamps];

        Assert.IsTrue(
            seen.Length >= 8,
            $"only {seen.Length} audio frames carried a timestamp ({missing} carried none)"
        );

        // Monotonic and actually moving: a constant value would satisfy "has a timestamp" while
        // being just as useless for alignment.
        for (int i = 1; i < seen.Length; i++)
            Assert.IsTrue(
                seen[i] > seen[i - 1],
                $"timestamp went backwards or stalled at index {i}"
            );

        Assert.IsTrue(
            seen[^1] - seen[0] > 1_000_000,
            "timestamps advanced by less than a millisecond across eight frames"
        );

        Assert.AreEqual(
            0,
            Volatile.Read(ref headers),
            "an audio frame arrived with a header timestamp; audioconvert copies spa_meta_header now, "
                + "and the PresentationTimestampNs docs that say audio consumers see null are wrong"
        );
    }

    /// <summary>
    /// An audio stream reports its queue depth, and an output that queues data reports a non-zero
    /// one.
    /// </summary>
    /// <remarks>
    /// The depth is only non-zero because the size is set on each buffer as it is queued. Leaving
    /// that unset makes a busy stream look permanently empty, which reads to a rate controller as a
    /// standing underrun and drives a correction in the wrong direction forever.
    /// </remarks>
    [TestMethod]
    public async Task AnAudioOutput_ReportsANonZeroQueueDepth()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext(
            "pwnet-audioq",
            ConsoleTestLoggerFactory.Instance
        );
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        string nodeName = $"pwnet-audioq-{Environment.ProcessId}";
        int shortestAsk = int.MaxValue;

        await using var output = new PipeWireAudioOutput(ctx, nodeName, Rate, Channels, Format);
        output.FillSamples += (_, samples, _, _, _) =>
        {
            // The span is already clamped to what the graph asked for, so its length is the
            // observable side of honouring pw_buffer.requested.
            shortestAsk = Math.Min(shortestAsk, samples.Length);
            samples.Clear();
            return samples.Length;
        };
        output.Connect(autoConnect: false);

        await WaitForAsync(reg, g => g.Nodes.Any(n => n.NodeName == nodeName), cts.Token);

        await using var capture = new PipeWireAudioCapture(
            ctx,
            $"pwnet-audioq-sink-{Environment.ProcessId}"
        );
        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)));
        await capture.WaitForStreamingAsync(cts.Token);
        await Task.Delay(500, cts.Token);

        PipeWireStreamQueue? queue = output.Queue;
        Assert.IsNotNull(queue, "a running output must report its queue");

        Assert.IsTrue(
            queue.Value.Queued > 0 || queue.Value.QueuedBuffers > 0,
            "the output queued audio but reported an empty queue, so the size was never set"
        );

        Assert.IsTrue(shortestAsk > 0 && shortestAsk < int.MaxValue, "the fill callback never ran");
        Assert.IsNotNull(output.GraphClock, "a running output should see the graph clock");
    }
}
