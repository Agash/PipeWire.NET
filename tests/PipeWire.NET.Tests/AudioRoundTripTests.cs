using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
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
[OSCondition(OperatingSystems.Linux)]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class AudioRoundTripTests : PipeWireTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static async Task<(PipeWireContext Context, PipeWireRegistry Registry)> ConnectAsync(
        string name,
        CancellationToken ct
    )
    {
        var context = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(ct);
        var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(ct);
        return (context, registry);
    }

    [TestMethod]
    public async Task AProducerReturningAPartialFrame_PublishesOnlyWholeFrames()
    {
        // The producer deliberately returns a byte count that ends mid-frame. chunk.size is read in
        // units of chunk.stride, so a remainder is taken as the start of the next frame and every
        // channel after it is offset by the shortfall for the rest of the buffer: the audio keeps
        // playing and the channels swap, which is much harder to notice than silence. The library
        // truncates to whole frames, and the consumer is where that is visible.
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext(
            "pwnet-ragged",
            ConsoleTestLoggerFactory.Instance
        );
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
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (byte)(i % 3 == 0 ? 0x01 : 0x00);

            int ragged = Math.Min(samples.Length, (frameBytes * 8) + (frameBytes / 2));
            if (ragged % frameBytes != 0)
                Interlocked.Increment(ref askedRagged);
            return ragged;
        };

        output.Connect(options: new() { AutoConnect = false });

        await reg.WaitUntilAsync(g => g.Nodes.Any(n => n.NodeName == nodeName), cts.Token);
        Assert.IsNotNull(output.NodeId, "a connected output must expose its node id");

        var received = new Tally();
        var ragged = 0;

        await using var capture = new PipeWireAudioCapture(
            ctx,
            $"pwnet-ragged-sink-{Environment.ProcessId}"
        );
        capture.FrameReady += (_, frame) =>
        {
            received.Increment();

            int bytesPerFrame = frame.Channels * frame.Format.BytesPerSample();
            if (bytesPerFrame > 0 && frame.Samples.Length % bytesPerFrame != 0)
                Interlocked.Increment(ref ragged);
        };

        capture.Connect(
            PipeWireTarget.Id(await output.WaitForNodeIdAsync(cts.Token)),
            new()
            {
                SampleRate = Rate,
                Channels = Channels,
                Format = Format,
            }
        );

        await received
            .AtLeast(5)
            .WaitAsync(TimeSpan.FromSeconds(20), cts.Token)
            .OrFail($"only {received.Value} of 5 arrived");

        Assert.IsTrue(
            Volatile.Read(ref askedRagged) > 0,
            "the producer never actually returned a partial frame, so nothing was exercised"
        );
        Assert.AreEqual(
            0,
            Volatile.Read(ref ragged),
            $"{ragged} buffers reached the consumer without being a whole number of frames"
        );
    }

    [TestMethod]
    [DataRow(48000, 2, AudioSampleFormat.F32Le)]
    [DataRow(44100, 2, AudioSampleFormat.F32Le)]
    [DataRow(48000, 1, AudioSampleFormat.F32Le)]
    [DataRow(48000, 2, AudioSampleFormat.S16Le)]
    public async Task PublishedAudio_ArrivesAtAConsumerWithTheSameShape(
        int rate,
        int channels,
        AudioSampleFormat format
    )
    {
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync(
            $"pwnet-art-{rate}-{channels}",
            cts.Token
        );

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
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (byte)(i % 3 == 0 ? 0x01 : 0x00);
                Interlocked.Increment(ref filled);
                return samples.Length;
            };

            // autoConnect: false keeps the session manager from routing this to the default sink,
            // which on a developer's machine is their speakers. The consumer targets it by id.
            output.Connect(options: new() { AutoConnect = false });

            await reg.WaitUntilAsync(g => g.Nodes.Any(n => n.NodeName == nodeName), cts.Token);
            Assert.IsNotNull(output.NodeId, "a connected output must expose its node id");

            var received = new Tally();
            var ragged = 0;
            var allZero = 0;
            int seenRate = 0,
                seenChannels = 0;
            AudioSampleFormat? seenFormat = null;

            await using var capture = new PipeWireAudioCapture(
                ctx,
                $"pwnet-art-sink-{rate}-{channels}"
            );
            capture.FrameReady += (_, frame) =>
            {
                received.Increment();
                seenRate = frame.SampleRate;
                seenChannels = frame.Channels;
                seenFormat ??= frame.Format;

                int bytesPerFrame = frame.Channels * frame.Format.BytesPerSample();
                if (bytesPerFrame > 0 && frame.Samples.Length % bytesPerFrame != 0)
                    Interlocked.Increment(ref ragged);

                bool nonZero = false;
                foreach (byte b in frame.Samples)
                {
                    if (b != 0)
                    {
                        nonZero = true;
                        break;
                    }
                }
                if (!nonZero && frame.Samples.Length > 0)
                    Interlocked.Increment(ref allZero);
            };

            capture.Connect(
                PipeWireTarget.Id(await output.WaitForNodeIdAsync(cts.Token)),
                new()
                {
                    SampleRate = rate,
                    Channels = channels,
                    Format = format,
                }
            );

            await received
                .AtLeast(5)
                .WaitAsync(TimeSpan.FromSeconds(20), cts.Token)
                .OrFail($"only {received.Value} of 5 arrived");

            Assert.IsTrue(filled > 0, "the producer was never asked for samples");
            Assert.AreEqual(rate, seenRate, "the consumer negotiated a different rate");
            Assert.AreEqual(
                channels,
                seenChannels,
                "the consumer negotiated a different channel count"
            );
            Assert.AreEqual(format, seenFormat);
            Assert.AreEqual(0, ragged, $"{ragged} buffers were not a whole number of frames");
            Assert.IsTrue(
                allZero < received.Value,
                "every buffer was silence, so nothing was actually routed from the producer"
            );
        }
    }

    [TestMethod]
    public async Task APublishedNode_IsARoutableGraphNodeLikeAnyOther()
    {
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync(
            "pwnet-art-routable",
            cts.Token
        );

        await using (ctx)
        await using (reg)
        {
            await using var output = new PipeWireAudioOutput(ctx, "pwnet_art_routable");
            output.FillSamples += (_, s, _, _, _) =>
            {
                s.Clear();
                return s.Length;
            };
            output.Connect(options: new() { AutoConnect = false });

            PipeWireGraphSnapshot graph = await reg.WaitUntilAsync(
                g =>
                    g.Nodes.Any(n => n.NodeName == "pwnet_art_routable")
                    && g.GetPortsForNode(output.NodeId ?? 0).Length > 0,
                cts.Token
            );

            uint id = (await output.WaitForNodeIdAsync(cts.Token));
            PipeWireNode node = graph.GetNode(id)!;

            // A stream is a node: it must answer the same questions as any other, or a patchbay
            // cannot treat published streams and hardware alike.
            Assert.AreEqual(PipeWireMediaKind.Audio, node.Media);
            Assert.IsTrue(graph.CanCaptureFrom(node), "a published audio source must be readable");

            foreach (PipeWirePort port in graph.GetPortsForNode(id))
                Assert.AreEqual(id, port.NodeId);

            // And it must be linkable to a node we create separately.
            PipeWireNode sink = await reg.CreateVirtualSink("Sink")
                .WithName("pwnet_art_sink")
                .ExecuteAsync(cts.Token);
            PipeWireGraphSnapshot ready = await reg.WaitUntilAsync(
                g => g.GetPortsForNode(sink.NodeId).Length == 4,
                cts.Token
            );

            PipeWirePort from = ready
                .GetPortsForNode(id, PipeWirePortDirection.Out)
                .OrderBy(p => p.PortId)
                .First();
            PipeWirePort to = ready
                .GetPortsForNode(sink.NodeId, PipeWirePortDirection.In)
                .OrderBy(p => p.PortId)
                .First();

            PipeWireLink link = await reg.CreateLink(from, to).ExecuteAsync(cts.Token);
            Assert.IsNotNull(reg.Current.GetLink(link.LinkId));
        }
    }

    [TestMethod]
    public async Task AProducerThatWritesNothing_IsTreatedAsSilenceNotAsAFailure()
    {
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync(
            "pwnet-art-silent",
            cts.Token
        );

        await using (ctx)
        await using (reg)
        {
            var asked = new Tally();
            await using var output = new PipeWireAudioOutput(ctx, "pwnet_art_silent");
            output.FillSamples += (_, _, _, _, _) =>
            {
                asked.Increment();
                return 0;
            };
            output.Connect(options: new() { AutoConnect = false });

            await reg.WaitUntilAsync(
                g => g.Nodes.Any(n => n.NodeName == "pwnet_art_silent"),
                cts.Token
            );

            var received = 0;
            await using var capture = new PipeWireAudioCapture(ctx, "pwnet-art-silent-sink");
            capture.FrameReady += (_, _) => Interlocked.Increment(ref received);
            capture.Connect(PipeWireTarget.Id(await output.WaitForNodeIdAsync(cts.Token)));

            await asked
                .AtLeast(5)
                .WaitAsync(TimeSpan.FromSeconds(20), cts.Token)
                .OrFail($"only {asked.Value} of 5 arrived");

            // Returning 0 means "no samples this round"; the stream must keep running rather than
            // erroring or stalling.
            Assert.IsTrue(
                asked.Value >= 5,
                $"the producer stopped being asked after {asked.Value} rounds"
            );
        }
    }

    [TestMethod]
    public async Task AProducerWhoseCallbackThrows_DoesNotKillTheStream()
    {
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync(
            "pwnet-art-throw",
            cts.Token
        );

        await using (ctx)
        await using (reg)
        {
            // FillSamples runs on the data thread inside a reverse P/Invoke.
            var asked = new Tally();
            await using var output = new PipeWireAudioOutput(ctx, "pwnet_art_throw");
            output.FillSamples += (_, _, _, _, _) =>
            {
                asked.Increment();
                throw new InvalidOperationException("hostile fill handler");
            };
            output.Connect(options: new() { AutoConnect = false });

            await reg.WaitUntilAsync(
                g => g.Nodes.Any(n => n.NodeName == "pwnet_art_throw"),
                cts.Token
            );

            // An unrouted output is never driven, so nothing would ask it for samples and the test
            // would pass without exercising anything. A consumer pulling from it is what makes the
            // throwing callback actually run.
            await using var capture = new PipeWireAudioCapture(ctx, "pwnet-art-throw-sink");
            capture.Connect(PipeWireTarget.Id(await output.WaitForNodeIdAsync(cts.Token)));

            await asked
                .AtLeast(5)
                .WaitAsync(TimeSpan.FromSeconds(20), cts.Token)
                .OrFail($"only {asked.Value} of 5 arrived");

            Assert.IsTrue(
                asked.Value >= 5,
                $"the stream stopped after {asked.Value} throwing fills; it must survive a bad producer"
            );

            // Surviving is half of it. A swallowed exception nothing can read leaves a handler that
            // throws every cycle looking exactly like one that published silence, which is the
            // hardest kind of bug to find in somebody else's application.
            Assert.IsNotNull(
                output.LastProcessError,
                "the fill handler threw every cycle and the stream reported no error at all"
            );

            Assert.IsInstanceOfType<InvalidOperationException>(
                output.LastProcessError,
                "the recorded error is not the one the handler threw"
            );

            Assert.IsTrue(
                output.ProcessErrorCount >= 5,
                $"{asked.Value} fills threw but only {output.ProcessErrorCount} were counted"
            );
        }
    }

    [TestMethod]
    public async Task ConnectingTwice_IsRefused()
    {
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync(
            "pwnet-art-twice",
            cts.Token
        );

        await using (ctx)
        await using (reg)
        {
            await using var output = new PipeWireAudioOutput(ctx, "pwnet_art_twice");
            output.FillSamples += (_, s, _, _, _) =>
            {
                s.Clear();
                return s.Length;
            };
            output.Connect(options: new() { AutoConnect = false });

            Assert.ThrowsExactly<InvalidOperationException>(
                () => output.Connect(),
                "a second Connect would leak the first stream"
            );

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
        await using var ctx = new PipeWireContext(
            "pwnet-art-bad",
            ConsoleTestLoggerFactory.Instance
        );

        // Caught before any native call, so a bad value never reaches the daemon.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PipeWireAudioOutput(ctx, "pwnet_art_bad", rate, channels)
        );
    }

    [TestMethod]
    public async Task AStreamRequiresAContextAndAName()
    {
        await using var ctx = new PipeWireContext(
            "pwnet-art-null",
            ConsoleTestLoggerFactory.Instance
        );

        Assert.ThrowsExactly<ArgumentNullException>(() => new PipeWireAudioOutput(null!, "n"));
        Assert.ThrowsExactly<ArgumentException>(() => new PipeWireAudioOutput(ctx, ""));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PipeWireAudioCapture(null!, "n"));
        Assert.ThrowsExactly<ArgumentException>(() => new PipeWireAudioCapture(ctx, ""));
    }

    [TestMethod]
    public async Task TwoStreamsOnOneContext_SeeTheSameGraphClock()
    {
        // The premise A/V sync rests on: streams driven by one graph share its clock, so their
        // timestamps are on the same timeline. Before io_changed was wired there was no way to
        // ask, and a consumer had to assume it.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-clock", cts.Token);

        await using (ctx)
        await using (reg)
        {
            await using var output = new PipeWireAudioOutput(ctx, "pwnet_clock_out");
            output.FillSamples += (_, _, _, _, _) => 0;
            output.Connect(options: new() { AutoConnect = false });

            await reg.WaitUntilAsync(
                g => g.Nodes.Any(n => n.NodeName == "pwnet_clock_out"),
                cts.Token
            );

            await using var capture = new PipeWireAudioCapture(ctx, "pwnet-clock-in");
            capture.Connect(PipeWireTarget.Id(await output.WaitForNodeIdAsync(cts.Token)));

            // The area arrives with the first cycle, not with the connect: read once a frame has.
            _ = await capture.NextFrame().Within(TimeSpan.FromSeconds(5), cts.Token);
            PipeWireGraphClock? producer = output.GraphClock;
            PipeWireGraphClock? consumer = capture.GraphClock;

            Assert.IsNotNull(producer, "the producer never received SPA_IO_Position");
            Assert.IsNotNull(consumer, "the consumer never received SPA_IO_Position");

            Assert.IsTrue(producer!.Value.RateDen > 0, "a clock with no rate says nothing");
            Assert.AreEqual(
                producer!.Value.RateDen,
                consumer!.Value.RateDen,
                "two streams on one graph must be on one clock rate"
            );
            Assert.IsTrue(
                producer!.Value.Duration > 0,
                "the quantum must be a real number of frames"
            );
        }
    }

    /// <summary>A stream can tell the graph it cannot go on, and both idioms dispose it.</summary>
    /// <remarks>
    /// <c>SetError</c> is what a callback calls when it cannot do what the graph asked; a stream
    /// that fails silently leaves its peer waiting on a cycle that never comes. The disposal half
    /// is here because the two forms do the same work and a caller should be able to pick either.
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [ExpectsLibraryError("stream error: a test said so")]
    public async Task AStreamThatCannotGoOn_CanSaySoAndDisposeEitherWay()
    {
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync(
            "pwnet-art-seterror",
            cts.Token
        );

        await using (ctx)
        await using (reg)
        {
            var output = new PipeWireAudioOutput(ctx, "pwnet_art_seterror");
            output.FillSamples += (_, samples, _, _, _) =>
            {
                samples.Clear();
                return samples.Length;
            };
            output.Connect(options: new() { AutoConnect = false });

            await reg.WaitUntilAsync(
                g => g.Nodes.Any(n => n.NodeName == "pwnet_art_seterror"),
                cts.Token
            );

            // Accepted on a live stream, and it does not throw its way back out.
            output.SetError(-5, "a test said so");

            Assert.IsNull(output.LastProcessError, "SetError is not a callback fault");

            // The synchronous form, on a type that only offered the async one until this session.
            output.Dispose();
            output.Dispose();
        }
    }
}
