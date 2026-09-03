using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Publishes audio, wires it up through the graph, and reads it back.
/// </summary>
/// <remarks>
/// This is the shape a transport agent runs in: publish a virtual node, let something else route it,
/// and have a consumer pull from it. Testing publish and capture separately proves each talks to the
/// daemon; only the round trip proves they agree on the samples in between, which is where a wrong
/// stride or channel count hides.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class AudioRoundTripTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<(PipeWireContext Context, PipeWireRegistry Registry)> ConnectAsync(
        string name, CancellationToken ct)
    {
        var context = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(ct);
        var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(ct);
        return (context, registry);
    }

    private static async Task<PipeWireGraphSnapshot> WaitForAsync(
        PipeWireRegistry registry, Func<PipeWireGraphSnapshot, bool> until, CancellationToken ct)
    {
        await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(ct))
            if (until(graph))
                return graph;

        throw new InvalidOperationException("the snapshot stream ended before the condition held");
    }

    [TestMethod]
    public async Task AProducerReturningAPartialFrame_PublishesOnlyWholeFrames()
    {
        // The producer deliberately returns a byte count that ends mid-frame. chunk.size is read in
        // units of chunk.stride, so a remainder is taken as the start of the next frame and every
        // channel after it is offset by the shortfall for the rest of the buffer: the audio keeps
        // playing and the channels swap, which is much harder to notice than silence. The library
        // truncates to whole frames, and the consumer is where that is visible.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-ragged", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        const int Rate = 48000;
        const int Channels = 2;
        const AudioSampleFormat Format = AudioSampleFormat.F32Le;
        int frameBytes = Format.BytesPerSample() * Channels;

        string nodeName = $"pwnet-ragged-{Environment.ProcessId}";
        int askedRagged = 0;

        await using var output = new PipeWireAudioOutput(ctx, nodeName, Rate, Channels, Format);
        output.FillSamples += (_, samples, _, _, _) =>
        {
            for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i % 3 == 0 ? 0x01 : 0x00);

            int ragged = Math.Min(samples.Length, (frameBytes * 8) + (frameBytes / 2));
            if (ragged % frameBytes != 0) Interlocked.Increment(ref askedRagged);
            return ragged;
        };

        output.Connect(autoConnect: false);

        await WaitForAsync(reg, g => g.Nodes.Any(n => n.NodeName == nodeName), cts.Token);
        Assert.IsNotNull(output.NodeId, "a connected output must expose its node id");

        var received = 0;
        var ragged = 0;

        await using var capture = new PipeWireAudioCapture(ctx, $"pwnet-ragged-sink-{Environment.ProcessId}");
        capture.FrameReady += (_, frame) =>
        {
            Interlocked.Increment(ref received);

            int bytesPerFrame = frame.Channels * frame.Format.BytesPerSample();
            if (bytesPerFrame > 0 && frame.Samples.Length % bytesPerFrame != 0)
                Interlocked.Increment(ref ragged);
        };

        capture.Connect(output.NodeId!.Value, Rate, Channels, Format);

        await WaitForCountAsync(() => Volatile.Read(ref received), 5, cts.Token);

        Assert.IsTrue(Volatile.Read(ref askedRagged) > 0,
            "the producer never actually returned a partial frame, so nothing was exercised");
        Assert.AreEqual(0, Volatile.Read(ref ragged),
            $"{ragged} buffers reached the consumer without being a whole number of frames");
    }

    [TestMethod]
    [DataRow(48000, 2, AudioSampleFormat.F32Le)]
    [DataRow(44100, 2, AudioSampleFormat.F32Le)]
    [DataRow(48000, 1, AudioSampleFormat.F32Le)]
    [DataRow(48000, 2, AudioSampleFormat.S16Le)]
    public async Task PublishedAudio_ArrivesAtAConsumerWithTheSameShape(
        int rate, int channels, AudioSampleFormat format)
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync($"pwnet-art-{rate}-{channels}", cts.Token);

        await using (ctx)
        await using (reg)
        {
            string nodeName = $"pwnet_art_src_{rate}_{channels}_{format}";

            var filled = 0;
            await using var output = new PipeWireAudioOutput(ctx, nodeName, rate, channels, format);
            output.FillSamples += (_, samples, r, c, f) =>
            {
                // A recognisable pattern rather than silence, so a consumer reading zeroes because
                // nothing was routed is distinguishable from real audio. Deliberately tiny: if this
                // ever reaches a real device it should be inaudible, not a burst of noise.
                for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i % 3 == 0 ? 0x01 : 0x00);
                Interlocked.Increment(ref filled);
                return samples.Length;
            };

            // autoConnect: false keeps the session manager from routing this to the default sink,
            // which on a developer's machine is their speakers. The consumer targets it by id.
            output.Connect(autoConnect: false);

            await WaitForAsync(reg, g => g.Nodes.Any(n => n.NodeName == nodeName), cts.Token);
            Assert.IsNotNull(output.NodeId, "a connected output must expose its node id");

            var received = 0;
            var ragged = 0;
            var allZero = 0;
            int seenRate = 0, seenChannels = 0;
            AudioSampleFormat? seenFormat = null;

            await using var capture = new PipeWireAudioCapture(ctx, $"pwnet-art-sink-{rate}-{channels}");
            capture.FrameReady += (_, frame) =>
            {
                Interlocked.Increment(ref received);
                seenRate = frame.SampleRate;
                seenChannels = frame.Channels;
                seenFormat ??= frame.Format;

                int bytesPerFrame = frame.Channels * frame.Format.BytesPerSample();
                if (bytesPerFrame > 0 && frame.Samples.Length % bytesPerFrame != 0)
                    Interlocked.Increment(ref ragged);

                bool nonZero = false;
                foreach (byte b in frame.Samples) { if (b != 0) { nonZero = true; break; } }
                if (!nonZero && frame.Samples.Length > 0) Interlocked.Increment(ref allZero);
            };

            capture.Connect(output.NodeId!.Value, rate, channels, format);

            await WaitForCountAsync(() => Volatile.Read(ref received), 5, cts.Token);

            Assert.IsTrue(filled > 0, "the producer was never asked for samples");
            Assert.AreEqual(rate, seenRate, "the consumer negotiated a different rate");
            Assert.AreEqual(channels, seenChannels, "the consumer negotiated a different channel count");
            Assert.AreEqual(format, seenFormat);
            Assert.AreEqual(0, ragged, $"{ragged} buffers were not a whole number of frames");
            Assert.IsTrue(allZero < received,
                "every buffer was silence, so nothing was actually routed from the producer");
        }
    }

    [TestMethod]
    public async Task APublishedNode_IsARoutableGraphNodeLikeAnyOther()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-art-routable", cts.Token);

        await using (ctx)
        await using (reg)
        {
            await using var output = new PipeWireAudioOutput(ctx, "pwnet_art_routable");
            output.FillSamples += (_, s, _, _, _) => { s.Clear(); return s.Length; };
            output.Connect(autoConnect: false);

            PipeWireGraphSnapshot graph = await WaitForAsync(
                reg,
                g => g.Nodes.Any(n => n.NodeName == "pwnet_art_routable")
                     && g.GetPortsForNode(output.NodeId ?? 0).Length > 0,
                cts.Token);

            uint id = output.NodeId!.Value;
            PipeWireNode node = graph.GetNode(id)!;

            // A stream is a node: it must answer the same questions as any other, or a patchbay
            // cannot treat published streams and hardware alike.
            Assert.AreEqual(PipeWireMediaKind.Audio, node.Media);
            Assert.IsTrue(graph.CanCaptureFrom(node), "a published audio source must be readable");

            foreach (PipeWirePort port in graph.GetPortsForNode(id))
                Assert.AreEqual(id, port.NodeId);

            // And it must be linkable to a node we create separately.
            PipeWireNode sink = await reg.CreateVirtualNode("Sink")
                                         .WithName("pwnet_art_sink").ExecuteAsync(cts.Token);
            PipeWireGraphSnapshot ready = await WaitForAsync(
                reg, g => g.GetPortsForNode(sink.NodeId).Length == 4, cts.Token);

            PipeWirePort from = ready.GetPortsForNode(id, PipeWirePortDirection.Out)
                                     .OrderBy(p => p.PortId).First();
            PipeWirePort to = ready.GetPortsForNode(sink.NodeId, PipeWirePortDirection.In)
                                   .OrderBy(p => p.PortId).First();

            PipeWireLink link = await reg.CreateLink(from, to).ExecuteAsync(cts.Token);
            Assert.IsNotNull(reg.Current.GetLink(link.LinkId));
        }
    }

    [TestMethod]
    public async Task AProducerThatWritesNothing_IsTreatedAsSilenceNotAsAFailure()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-art-silent", cts.Token);

        await using (ctx)
        await using (reg)
        {
            var asked = 0;
            await using var output = new PipeWireAudioOutput(ctx, "pwnet_art_silent");
            output.FillSamples += (_, _, _, _, _) => { Interlocked.Increment(ref asked); return 0; };
            output.Connect(autoConnect: false);

            await WaitForAsync(reg, g => g.Nodes.Any(n => n.NodeName == "pwnet_art_silent"), cts.Token);

            var received = 0;
            await using var capture = new PipeWireAudioCapture(ctx, "pwnet-art-silent-sink");
            capture.FrameReady += (_, _) => Interlocked.Increment(ref received);
            capture.Connect(output.NodeId!.Value);

            await WaitForCountAsync(() => Volatile.Read(ref asked), 5, cts.Token);

            // Returning 0 means "no samples this round"; the stream must keep running rather than
            // erroring or stalling.
            Assert.IsTrue(asked >= 5, $"the producer stopped being asked after {asked} rounds");
        }
    }

    [TestMethod]
    public async Task AProducerWhoseCallbackThrows_DoesNotKillTheStream()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-art-throw", cts.Token);

        await using (ctx)
        await using (reg)
        {
            // FillSamples runs on the data thread inside a reverse P/Invoke.
            var asked = 0;
            await using var output = new PipeWireAudioOutput(ctx, "pwnet_art_throw");
            output.FillSamples += (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref asked);
                throw new InvalidOperationException("hostile fill handler");
            };
            output.Connect(autoConnect: false);

            await WaitForAsync(reg, g => g.Nodes.Any(n => n.NodeName == "pwnet_art_throw"), cts.Token);

            // An unrouted output is never driven, so nothing would ask it for samples and the test
            // would pass without exercising anything. A consumer pulling from it is what makes the
            // throwing callback actually run.
            await using var capture = new PipeWireAudioCapture(ctx, "pwnet-art-throw-sink");
            capture.Connect(output.NodeId!.Value);

            await WaitForCountAsync(() => Volatile.Read(ref asked), 5, cts.Token);

            Assert.IsTrue(asked >= 5,
                $"the stream stopped after {asked} throwing fills; it must survive a bad producer");
        }
    }

    [TestMethod]
    public async Task ConnectingTwice_IsRefused()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-art-twice", cts.Token);

        await using (ctx)
        await using (reg)
        {
            await using var output = new PipeWireAudioOutput(ctx, "pwnet_art_twice");
            output.FillSamples += (_, s, _, _, _) => { s.Clear(); return s.Length; };
            output.Connect(autoConnect: false);

            Assert.ThrowsExactly<InvalidOperationException>(() => output.Connect(),
                "a second Connect would leak the first stream");

            await using var capture = new PipeWireAudioCapture(ctx, "pwnet-art-twice-sink");
            capture.Connect();
            Assert.ThrowsExactly<InvalidOperationException>(() => capture.Connect());
        }
    }

    [TestMethod]
    [DataRow(0, 2)]
    [DataRow(-1, 2)]
    [DataRow(48000, 0)]
    [DataRow(48000, -1)]
    public async Task NonsenseStreamGeometry_IsRefusedAtConstruction(int rate, int channels)
    {
        RequireLinux();
        await using var ctx = new PipeWireContext("pwnet-art-bad", ConsoleTestLoggerFactory.Instance);

        // Caught before any native call, so a bad value never reaches the daemon.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new PipeWireAudioOutput(ctx, "pwnet_art_bad", rate, channels));
    }

    [TestMethod]
    public async Task AStreamRequiresAContextAndAName()
    {
        RequireLinux();
        await using var ctx = new PipeWireContext("pwnet-art-null", ConsoleTestLoggerFactory.Instance);

        Assert.ThrowsExactly<ArgumentNullException>(() => new PipeWireAudioOutput(null!, "n"));
        Assert.ThrowsExactly<ArgumentException>(() => new PipeWireAudioOutput(ctx, ""));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PipeWireAudioCapture(null!, "n"));
        Assert.ThrowsExactly<ArgumentException>(() => new PipeWireAudioCapture(ctx, ""));
    }

    private static async Task WaitForCountAsync(Func<int> counter, int target, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(25);
        while (counter() < target)
        {
            ct.ThrowIfCancellationRequested();
            if (waited > TimeSpan.FromSeconds(20))
                throw new TimeoutException($"only reached {counter()} of {target}");
            await Task.Delay(step, ct);
            waited += step;
        }
    }
}
