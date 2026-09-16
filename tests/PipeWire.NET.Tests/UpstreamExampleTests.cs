using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The upstream examples this library had no way to express, recreated.
/// </summary>
/// <remarks>
/// <para>
/// Upstream ships these as the canonical use-cases, and three of them were unreachable from here:
/// <c>video-src-reneg</c> and <c>video-src-fixate</c> because only the consumer could renegotiate,
/// and <c>video-dsp-play</c> / <c>video-dsp-src</c> because a filter port could only carry mono
/// audio, MIDI or control. Both were library gaps rather than missing tests, and both are closed by
/// <see cref="PipeWireVideoOutput.RequestFormat"/> and
/// <see cref="PipeWireDspFormat.Rgba32FloatVideo"/>.
/// </para>
/// <para>
/// They matter for real reasons. A capture source whose window is resized has to renegotiate or
/// tear the stream down and lose its consumer; and video through a filter is how a processing node
/// sits in the graph without being a stream at either end.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class UpstreamExampleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    private const int Width = 320;
    private const int Height = 240;

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<uint> WaitForNodeIdAsync(
        PipeWireVideoOutput output, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            if (output.NodeId is { } id) return id;
            await Task.Delay(50, ct);
        }

        throw new InvalidOperationException("the producer was never given a node id");
    }

    /// <summary>
    /// <c>video-src-reneg</c>: a producer offers a new size mid-stream and the stream survives it.
    /// </summary>
    /// <remarks>
    /// The offer going out is what is pinned, not the consumer accepting - whether it does is its
    /// business, and a producer must behave the same either way. What would be a bug is the call
    /// killing the stream, which is what tearing down and reconnecting to change size amounts to.
    /// </remarks>
    [TestMethod]
    public async Task AProducerOfferingANewFormat_KeepsItsConsumer()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-srcreneg", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        var filled = 0;

        await using var output = new PipeWireVideoOutput(
            ctx, "pwnet-srcreneg-src", Width, Height, PixelFormat.Bgra, 30);

        output.FillFrame += (_, pixels, _, _, _, _) =>
        {
            Interlocked.Increment(ref filled);
            pixels.Clear();
            return true;
        };

        output.Connect(autoConnect: false);
        uint nodeId = await WaitForNodeIdAsync(output, cts.Token);

        var received = 0;
        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-srcreneg-sink");
        capture.FrameReady += (_, _) => Interlocked.Increment(ref received);
        capture.Connect(nodeId, [PixelFormat.Bgra]);
        await capture.WaitForStreamingAsync(cts.Token);

        await Task.Delay(400, cts.Token);
        Assert.IsTrue(Volatile.Read(ref received) > 0, "nothing flowed before the renegotiation");

        Volatile.Write(ref received, 0);

        PixelFormat[] formats = [PixelFormat.Bgra];
        Assert.IsTrue(
            output.RequestFormat(formats, 160, 120),
            "the producer's renegotiation offer was refused outright");

        await Task.Delay(600, cts.Token);

        Assert.IsTrue(
            Volatile.Read(ref received) > 0,
            "the consumer stopped receiving after the producer offered a new format");

        Assert.IsNotNull(output.Queue, "the producer stopped answering after renegotiating");
    }

    /// <summary>
    /// <c>video-src-fixate</c>: a producer offers a range and lets the consumer fixate inside it.
    /// </summary>
    /// <remarks>
    /// The difference from the test above is <c>fixedSize: false</c>, which is the whole point of
    /// fixation: the producer says what it can do rather than what it will do, and the negotiated
    /// result may be neither end of the range. A consumer must therefore read the geometry off the
    /// frame rather than assume what it asked for, which is what the assertion here checks.
    /// </remarks>
    [TestMethod]
    public async Task AProducerOfferingARange_LetsTheConsumerFixateWithinIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-srcfixate", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var output = new PipeWireVideoOutput(
            ctx, "pwnet-srcfixate-src", Width, Height, PixelFormat.Bgra, 30);

        output.FillFrame += (_, pixels, _, _, _, _) =>
        {
            pixels.Clear();
            return true;
        };

        output.Connect(autoConnect: false);
        uint nodeId = await WaitForNodeIdAsync(output, cts.Token);

        var sizes = new List<(int W, int H)>();
        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-srcfixate-sink");
        capture.FrameReady += (_, f) =>
        {
            lock (sizes) { if (sizes.Count < 32) sizes.Add((f.Width, f.Height)); }
        };

        capture.Connect(nodeId, [PixelFormat.Bgra]);
        await capture.WaitForStreamingAsync(cts.Token);

        PixelFormat[] formats = [PixelFormat.Bgra];
        Assert.IsTrue(
            output.RequestFormat(formats, 256, 144, frameRate: 30, fixedSize: false),
            "the producer's range offer was refused outright");

        await Task.Delay(800, cts.Token);

        (int W, int H)[] seen;
        lock (sizes) seen = [.. sizes];

        Assert.IsTrue(seen.Length > 0, "no frame arrived while a range was on offer");

        // Whatever was settled on, every frame has to describe itself consistently - a frame whose
        // geometry does not match its own stride is what an importer crashes on.
        foreach ((int w, int h) in seen)
        {
            Assert.IsTrue(w > 0 && h > 0, $"a frame reported a degenerate size {w}x{h}");
        }

        Assert.AreEqual(1, seen.Distinct().Count(),
            "the negotiated size changed between frames without a renegotiation in between");
    }

    /// <summary>
    /// <c>audio-src-ring</c>: a producer serving from a ring delivers an unbroken sample stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of the ring examples is continuity: the producer is not generating samples in the
    /// callback, it is handing over whatever the ring holds, and the graph must receive exactly that
    /// sequence with nothing dropped or repeated. A counter written into the samples is what makes
    /// that checkable - a gap or a repeat in the received sequence is a buffer the stream lost or
    /// served twice.
    /// </para>
    /// <para>
    /// That is invisible to a test that only counts frames, and it is audible as a click.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AProducerServingFromARing_DeliversAnUnbrokenSequence()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        const int rate = 48000, channels = 1;

        await using var ctx = new PipeWireContext("pwnet-ring", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        string nodeName = $"pwnet-ring-{Environment.ProcessId}";

        // The ring: a counter the producer walks, one value per float written.
        uint next = 0;

        await using var output = new PipeWireAudioOutput(
            ctx, nodeName, rate, channels, AudioSampleFormat.F32Le);

        output.FillSamples += (_, samples, _, _, _) =>
        {
            Span<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(samples);
            for (var i = 0; i < floats.Length; i++) floats[i] = next++;
            return samples.Length;
        };

        output.Connect(autoConnect: false);

        var received = new List<float>();
        await using var capture = new PipeWireAudioCapture(ctx, $"{nodeName}-sink");
        capture.FrameReady += (_, f) =>
        {
            ReadOnlySpan<float> floats =
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(f.Samples);

            lock (received)
            {
                if (received.Count > 20000) return;
                foreach (float v in floats) received.Add(v);
            }
        };

        capture.Connect((await output.WaitForNodeIdAsync(cts.Token)), sampleRate: rate, channels: channels,
            format: AudioSampleFormat.F32Le);

        await capture.WaitForStreamingAsync(cts.Token);
        await Task.Delay(700, cts.Token);

        float[] got;
        lock (received) got = [.. received];

        Assert.IsTrue(got.Length > 2000, $"only {got.Length} samples arrived from the ring");

        // Skip the head: the first cycles can carry silence the graph inserted before the producer
        // was scheduled, which is not the producer losing anything.
        int start = 0;
        while (start < got.Length && got[start] == 0f) start++;

        Assert.IsTrue(got.Length - start > 1000, "the stream was silence all the way through");

        var breaks = 0;
        for (int i = start + 1; i < got.Length; i++)
        {
            if (got[i] != got[i - 1] + 1f) breaks++;
        }

        // Exactly zero: every sample the producer wrote arrived once, in order.
        Assert.AreEqual(
            0, breaks,
            $"the received sequence broke {breaks} times over {got.Length - start} samples, so "
            + "buffers were dropped or served twice");
    }

    /// <summary>
    /// <c>video-play-fixate</c>: a consumer asks for a size the producer does not have, and takes
    /// what it is actually given.
    /// </summary>
    /// <remarks>
    /// The consumer half of fixation. Connecting states a preference, not a demand, so the frames
    /// that arrive may be a different size - and the failure this guards against is a consumer that
    /// believes its own request. Reading width and height off the frame is the contract; assuming
    /// them is how a downstream importer gets handed a buffer laid out differently from how it was
    /// told to read it.
    /// </remarks>
    [TestMethod]
    public async Task AConsumerAskingForASizeTheProducerLacks_TakesWhatItIsGiven()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-playfixate", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var output = new PipeWireVideoOutput(
            ctx, "pwnet-playfixate-src", Width, Height, PixelFormat.Bgra, 30);

        output.FillFrame += (_, pixels, _, _, _, _) =>
        {
            pixels.Clear();
            return true;
        };

        output.Connect(autoConnect: false);
        uint nodeId = await WaitForNodeIdAsync(output, cts.Token);

        var sizes = new List<(int W, int H)>();
        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-playfixate-sink");
        capture.FrameReady += (_, f) =>
        {
            lock (sizes) { if (sizes.Count < 16) sizes.Add((f.Width, f.Height)); }
        };

        // A size the producer is not offering. The connect must still negotiate.
        capture.Connect(
            nodeId, [PixelFormat.Bgra], preferredWidth: 640, preferredHeight: 360);

        await capture.WaitForStreamingAsync(cts.Token);
        await Task.Delay(700, cts.Token);

        (int W, int H)[] seen;
        lock (sizes) seen = [.. sizes];

        Assert.IsTrue(
            seen.Length > 0,
            "asking for a size the producer does not have stopped the negotiation entirely");

        // What arrived is the producer's size, not the request - which is exactly why a consumer
        // must read the geometry rather than assume it.
        Assert.AreEqual(
            (Width, Height),
            seen[0],
            "the negotiated geometry is neither what was asked for nor what the producer offers");

        Assert.AreEqual(1, seen.Distinct().Count(), "the geometry changed between frames");
    }

    /// <summary>
    /// <c>video-dsp-src</c> into <c>video-dsp-play</c>: pixels written by one filter are read by
    /// another, through a real DSP video link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven, not merely constructed. One filter writes a per-cycle pattern into its output video
    /// port, the graph carries it over a link, and the second filter reads it back out of its input
    /// port and checks it. A port that reached the graph declaring the wrong format, or an accessor
    /// handing back the wrong number of floats, fails here - where the same test written as "the
    /// ports appeared" would pass.
    /// </para>
    /// <para>
    /// The geometry comes from the graph rather than from either port: the daemon publishes
    /// <c>position-&gt;video</c> from its own <c>default.video.*</c> settings on every cycle
    /// (<c>impl-node.c</c>), with a stride of sixteen bytes per pixel - four floats, RGBA.
    /// </para>
    /// <para>
    /// Upstream's source half is a <c>pw_stream</c> connecting with <c>SPA_MEDIA_SUBTYPE_dsp</c>,
    /// which this library's stream layer does not offer; a filter is used for both ends here. The
    /// link and the buffer format being exercised are the same either way.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task PixelsWrittenByOneFilter_AreReadByAnotherOverADspVideoLink()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-dspvideo", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // - The source half -
        await using PipeWireFilter source = PipeWireFilter.Create(ctx, "pwnet_dsp_src");
        PipeWireFilterPort srcOut = source.AddVideoPort(PipeWirePortDirection.Out, "output");

        var written = 0;
        float lastWritten = 0;

        source.ProcessCallback = (f, _, in _) =>
        {
            PipeWireVideoCycle v = f.VideoCycle;
            if (!v.IsValid || v.Width == 0 || v.Height == 0) return;

            Span<float> pixels = srcOut.GetPixels(v.Width, v.Height);
            if (pixels.IsEmpty) return;

            // A value that changes every cycle, so a stale or repeated buffer is visible.
            float tag = Interlocked.Increment(ref written);
            pixels.Fill(tag);
            Volatile.Write(ref lastWritten, tag);
        };

        // - The consuming half -
        await using PipeWireFilter play = PipeWireFilter.Create(ctx, "pwnet_dsp_play");
        PipeWireFilterPort playIn = play.AddVideoPort(PipeWirePortDirection.In, "input");

        var read = 0;
        var nonUniform = 0;
        float lastRead = 0;
        var pixelCounts = new List<int>();

        play.ProcessCallback = (f, _, in _) =>
        {
            PipeWireVideoCycle v = f.VideoCycle;
            if (!v.IsValid || v.Width == 0 || v.Height == 0) return;

            Span<float> pixels = playIn.GetPixels(v.Width, v.Height);
            if (pixels.IsEmpty) return;

            float first = pixels[0];
            if (first == 0f) return;   // a cycle before the source has written anything

            foreach (float px in pixels)
            {
                if (px != first) { Interlocked.Increment(ref nonUniform); break; }
            }

            // Recorded inside the cycle, where the geometry is actually available: four floats
            // per pixel is the RGBA F32 contract, and a short buffer would mean the accessor
            // handed back less than a frame.
            lock (pixelCounts)
            {
                if (pixelCounts.Count < 8)
                    pixelCounts.Add(pixels.Length - (int)(v.Width * v.Height * 4));
            }

            Volatile.Write(ref lastRead, first);
            Interlocked.Increment(ref read);
        };

        await source.ConnectAsync(PipeWireFilterFlags.RtProcess, cts.Token);
        await play.ConnectAsync(PipeWireFilterFlags.RtProcess, cts.Token);

        uint srcNode = await source.WaitForNodeIdAsync(cts.Token);
        uint playNode = await play.WaitForNodeIdAsync(cts.Token);

        // Both ports have to reach the registry before they can be linked.
        PipeWirePort? outPort = null, inPort = null;
        for (var i = 0; i < 100 && (outPort is null || inPort is null); i++)
        {
            PipeWireGraphSnapshot g = reg.Current;
            outPort ??= g.Ports.FirstOrDefault(
                p => p.NodeId == srcNode && p.PortDirection == PipeWirePortDirection.Out);
            inPort ??= g.Ports.FirstOrDefault(
                p => p.NodeId == playNode && p.PortDirection == PipeWirePortDirection.In);

            if (outPort is null || inPort is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(outPort, "the source filter's video port never reached the graph");
        Assert.IsNotNull(inPort, "the consuming filter's video port never reached the graph");

        Assert.AreEqual("32 bit float RGBA video", outPort!.DspFormat,
            "the source port reached the graph declaring the wrong DSP format");
        Assert.AreEqual("32 bit float RGBA video", inPort!.DspFormat,
            "the consuming port reached the graph declaring the wrong DSP format");

        PipeWireLink link = await reg.CreateLink(outPort, inPort).ExecuteAsync(cts.Token);
        Assert.IsNotNull(link, "the two video ports could not be linked");

        source.SetActive(true);
        play.SetActive(true);

        for (var i = 0; i < 80 && Volatile.Read(ref read) < 4; i++)
            await Task.Delay(50, cts.Token);

        Assert.IsTrue(
            Volatile.Read(ref written) > 0,
            "the source filter's process callback never got a video buffer to write into");

        Assert.IsTrue(
            Volatile.Read(ref read) >= 4,
            $"only {Volatile.Read(ref read)} cycles carried pixels across the link");

        Assert.AreEqual(
            0, Volatile.Read(ref nonUniform),
            "a frame arrived with mixed values, so the buffer was torn or only partly written");

        // The accessor handed back exactly one frame: width * height * 4 floats.
        int[] shortfalls;
        lock (pixelCounts) shortfalls = [.. pixelCounts];

        Assert.IsTrue(shortfalls.Length > 0, "no buffer size was recorded");

        foreach (int shortfall in shortfalls)
        {
            Assert.AreEqual(
                0, shortfall,
                $"a video buffer was {shortfall} floats away from one RGBA frame");
        }

        // What was read is something the source actually wrote, not an artefact.
        Assert.IsTrue(
            Volatile.Read(ref lastRead) > 0 && Volatile.Read(ref lastRead) <= Volatile.Read(ref written),
            $"the consumer read {Volatile.Read(ref lastRead)}, which the source never wrote "
            + $"(it had written up to {Volatile.Read(ref written)})");

        // Outside a cycle the geometry is deliberately not reported, so a filter cannot size its
        // next access from a frame the graph has moved on from. Every cycle above proves the other
        // half: the callback returns early unless the geometry is valid, so 4 reads is 4 cycles
        // that had it.
        //
        // The filter is taken out of the graph before the read. VideoCycle is written on the
        // realtime thread at the top of each cycle and cleared at the bottom, so a read taken while
        // cycles are still running races that write and sees a cycle in flight - which is the
        // contract working, not failing.
        play.SetActive(false);
        source.SetActive(false);

        int quiet = Volatile.Read(ref read);
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(50, cts.Token);
            int now = Volatile.Read(ref read);
            if (now == quiet) break;
            quiet = now;
        }

        Assert.AreEqual(
            quiet, Volatile.Read(ref read),
            "the consuming filter is still being scheduled after SetActive(false)");

        Assert.IsFalse(
            play.VideoCycle.IsValid,
            "the frame geometry is still being reported outside the process callback");
    }

    /// <summary>
    /// <c>midi-src</c>: timed MIDI events written by one filter arrive at another, in time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven, with the payload and the timing both checked. Upstream's <c>midi-src</c> builds a
    /// sequence of <c>SPA_CONTROL_UMP</c> controls at sample offsets within the cycle; the point of
    /// a sequence port is that events carry <em>when</em> inside the quantum they happen, so a
    /// consumer that received the right notes at the wrong offsets would still be out of time.
    /// </para>
    /// <para>
    /// This is what the sequence accessors exist for. Before them a MIDI port could be created and
    /// linked but its buffer could not be touched from managed code at all.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task TimedMidiEventsWrittenByOneFilter_ArriveAtAnotherInTime()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // A note-on and a note-off as UMP packets, at distinct offsets inside the cycle.
        byte[] noteOn = [0x20, 0x90, 0x3C, 0x64];
        byte[] noteOff = [0x20, 0x80, 0x3C, 0x00];
        const uint onOffset = 0, offOffset = 64;

        await using var ctx = new PipeWireContext("pwnet-midi", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireFilter source = PipeWireFilter.Create(ctx, "pwnet_midi_src");
        PipeWireFilterPort srcOut = source.AddUmpPort(PipeWirePortDirection.Out, "output");

        var sent = 0;
        source.ProcessCallback = (_, _, in _) =>
        {
            SpaControl[] events =
            [
                new SpaControl(onOffset, (uint)SpaControlType.Ump, new SpaBytes([.. noteOn])),
                new SpaControl(offOffset, (uint)SpaControlType.Ump, new SpaBytes([.. noteOff])),
            ];

            if (srcOut.WriteEvents(events)) Interlocked.Increment(ref sent);
        };

        await using PipeWireFilter sink = PipeWireFilter.Create(ctx, "pwnet_midi_sink");
        PipeWireFilterPort sinkIn = sink.AddUmpPort(PipeWirePortDirection.In, "input");

        var received = new List<(uint Offset, byte[] Data)>();
        sink.ProcessCallback = (_, _, in _) =>
        {
            SpaSequence? seq = sinkIn.ReadEvents();
            if (seq is null || seq.Controls.Length == 0) return;

            lock (received)
            {
                if (received.Count >= 16) return;
                foreach (SpaControl c in seq.Controls)
                {
                    if (c.Value is SpaBytes b) received.Add((c.Offset, [.. b.Value]));
                }
            }
        };

        await source.ConnectAsync(PipeWireFilterFlags.RtProcess, cts.Token);
        await sink.ConnectAsync(PipeWireFilterFlags.RtProcess, cts.Token);

        uint srcNode = await source.WaitForNodeIdAsync(cts.Token);
        uint sinkNode = await sink.WaitForNodeIdAsync(cts.Token);

        PipeWirePort? outPort = null, inPort = null;
        for (var i = 0; i < 100 && (outPort is null || inPort is null); i++)
        {
            PipeWireGraphSnapshot g = reg.Current;
            outPort ??= g.Ports.FirstOrDefault(
                p => p.NodeId == srcNode && p.PortDirection == PipeWirePortDirection.Out);
            inPort ??= g.Ports.FirstOrDefault(
                p => p.NodeId == sinkNode && p.PortDirection == PipeWirePortDirection.In);

            if (outPort is null || inPort is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(outPort, "the MIDI source port never reached the graph");
        Assert.IsNotNull(inPort, "the MIDI sink port never reached the graph");

        PipeWireLink link = await reg.CreateLink(outPort!, inPort!).ExecuteAsync(cts.Token);
        Assert.IsNotNull(link, "the two MIDI ports could not be linked");

        source.SetActive(true);
        sink.SetActive(true);

        for (var i = 0; i < 100; i++)
        {
            lock (received) { if (received.Count >= 4) break; }
            await Task.Delay(50, cts.Token);
        }

        (uint Offset, byte[] Data)[] got;
        lock (received) got = [.. received];

        Assert.IsTrue(
            Volatile.Read(ref sent) > 0,
            "the source filter never got a buffer to write MIDI into");

        Assert.IsTrue(got.Length >= 4, $"only {got.Length} MIDI events crossed the link");

        // The payloads survived intact.
        foreach ((uint offset, byte[] data) in got)
        {
            bool isOn = data.AsSpan().SequenceEqual(noteOn);
            bool isOff = data.AsSpan().SequenceEqual(noteOff);

            Assert.IsTrue(
                isOn || isOff,
                $"an event arrived with payload [{string.Join(" ", data.Select(b => b.ToString("X2")))}], "
                + "which is neither packet that was sent");

            // And at the offset it was sent at: a sequence that lost its timing would deliver the
            // right notes at the wrong moment inside the quantum.
            Assert.AreEqual(
                isOn ? onOffset : offOffset,
                offset,
                "a MIDI event arrived at the wrong offset within the cycle");
        }

        Assert.IsTrue(
            got.Any(e => e.Data.AsSpan().SequenceEqual(noteOn))
            && got.Any(e => e.Data.AsSpan().SequenceEqual(noteOff)),
            "only one of the two packets ever arrived");
    }

    /// <summary>
    /// Every DSP format a filter port can declare reaches the graph as the string PipeWire reads.
    /// </summary>
    /// <remarks>
    /// Checked from the registry rather than from the port object, because the port object is this
    /// library's own record of what it asked for. The daemon's copy is what other nodes link
    /// against, and a wrong string there is a port nothing will connect to - discovered much later
    /// and looking like "nothing is sending me anything".
    /// </remarks>
    [TestMethod]
    public async Task EveryDspFormat_ReachesTheGraphAsTheStringPipeWireReads()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-dspformats", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_dsp_formats");

        filter.AddAudioPort(PipeWirePortDirection.In, "audio_in");
        filter.AddMidiPort(PipeWirePortDirection.In, "midi_in");
        filter.AddControlPort(PipeWirePortDirection.In, "control_in");
        filter.AddVideoPort(PipeWirePortDirection.In, "video_in");
        filter.AddUmpPort(PipeWirePortDirection.In, "ump_in");

        Assert.AreEqual(5, filter.Ports.Count);

        await filter.ConnectAsync(cancellationToken: cts.Token);
        uint nodeId = await filter.WaitForNodeIdAsync(cts.Token);

        // All five have to reach the registry before anything can link to them.
        List<PipeWirePort> ports = [];
        for (var i = 0; i < 100 && ports.Count < 5; i++)
        {
            ports = [.. reg.Current.Ports.Where(p => p.NodeId == nodeId)];
            if (ports.Count < 5) await Task.Delay(100, cts.Token);
        }

        Assert.AreEqual(5, ports.Count, "not every DSP port reached the graph");

        Dictionary<string, string?> byName = ports.ToDictionary(
            p => p.Properties.GetValueOrDefault(PipeWireKeys.PW_KEY_PORT_NAME) ?? p.PortId.ToString(),
            p => p.DspFormat,
            StringComparer.Ordinal);

        Assert.AreEqual("32 bit float mono audio", byName["audio_in"]);
        Assert.AreEqual("8 bit raw midi", byName["midi_in"]);
        Assert.AreEqual("8 bit raw control", byName["control_in"]);
        Assert.AreEqual("32 bit float RGBA video", byName["video_in"]);

        // UMP is the one PipeWire renames: it rewrites format.dsp back to MIDI and records the
        // difference in control.ump instead, so asserting the name alone would miss a port that
        // silently became plain MIDI.
        Assert.AreEqual("8 bit raw midi", byName["ump_in"]);

        // control.ump is one of the port's own properties, outside the keys the daemon copies onto
        // the registry global (impl-port.c global_keys). It arrives with the port's info once the
        // port is bound, and binding files it into the registry's record of the port.
        PipeWirePort ump = ports.Single(
            p => p.Properties.GetValueOrDefault(PipeWireKeys.PW_KEY_PORT_NAME) == "ump_in");
        await using PipeWirePortProxy bound = reg.BindPort(ump.PortId);
        for (var i = 0; i < 50 && ump.Properties.GetValueOrDefault("control.ump") is null; i++)
        {
            await Task.Delay(100, cts.Token);
            ump = reg.Current.GetPort(ump.Id) ?? ump;
        }

        // The daemon only records the distinction from 1.6.8; before that a UMP port really is
        // announced as plain MIDI and there is nothing on the wire to assert against. The rest of
        // this test still runs, which is most of what it covers.
        if (SessionGates.DaemonAtLeast(1, 6, 8))
        {
            Assert.AreEqual(
                "true",
                ump.Properties.GetValueOrDefault("control.ump")?.ToLowerInvariant(),
                "the UMP port reached the graph as plain MIDI, so MIDI 2.0 packets would be misread");
        }
    }
}
