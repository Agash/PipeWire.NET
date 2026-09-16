using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// <c>export-source</c> and <c>export-sink</c>: being a node the graph talks to, rather than
/// using one libpipewire implements on our behalf.
/// </summary>
/// <remarks>
/// <para>
/// A <c>pw_stream</c> is a node with libpipewire's policy baked in. Exporting means implementing
/// <c>spa_node</c> and answering the graph directly - what formats the port carries, what buffers to
/// allocate, and what to do each cycle. It is the layer <c>pw_stream</c> itself is built on
/// (upstream <c>stream.c</c> implements this same interface).
/// </para>
/// <para>
/// The failure mode is silence, in both senses. A node that answers <c>port_enum_params</c> wrongly
/// is exported, appears in the graph, links up - and never negotiates, so nothing ever flows and
/// nothing reports an error. Only checking that data arrives distinguishes it from a working one.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class ExportedNodeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    private const int Rate = 48000;
    private const int Channels = 1;

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    /// <summary>
    /// An exported node reaches the graph under the name and media class it was given.
    /// </summary>
    /// <remarks>
    /// The first half of the contract, and the half that fails loudly. If <c>pw_core_export</c> had
    /// been handed a malformed <c>spa_node</c> the proxy would come back null or the node would
    /// never appear; either way it is visible here rather than three layers later.
    /// </remarks>
    [TestMethod]
    public async Task AnExportedNode_AppearsInTheGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = $"pwnet-export-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-export", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireNodeProvider node = PipeWireNodeProvider.Create(
            ctx,
            name,
            PipeWireExportedFormat.AudioF32(Rate, Channels),
            SpaDirection.Output,
            new Dictionary<string, string>
            {
                [PipeWireKeys.PW_KEY_MEDIA_CLASS] = "Audio/Source",
                [PipeWireKeys.PW_KEY_NODE_DESCRIPTION] = "exported by PipeWire.NET",
            });

        PipeWireNode? seen = null;
        for (var i = 0; i < 100 && seen is null; i++)
        {
            seen = reg.Current.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeName, name, StringComparison.Ordinal));

            if (seen is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(seen, "the exported node never appeared in the graph");

        Assert.AreEqual("Audio/Source", seen!.MediaClass, "the exported node lost its media class");

        Assert.AreEqual(
            "exported by PipeWire.NET",
            seen.Properties.GetValueOrDefault(PipeWireKeys.PW_KEY_NODE_DESCRIPTION),
            "the exported node lost the properties it was published with");

        // A node with no ports is in the graph but cannot be linked to, which presents downstream
        // as "no target node available" when something tries to connect - a message that says
        // nothing about ports. Asserting it here names the cause instead.
        List<PipeWirePort> ports = [];
        for (var i = 0; i < 60 && ports.Count == 0; i++)
        {
            ports = [.. reg.Current.Ports.Where(p => p.NodeId == seen.NodeId)];
            if (ports.Count == 0) await Task.Delay(50, cts.Token);
        }

        Assert.IsTrue(
            ports.Count > 0,
            "the exported node has no ports in the graph, so nothing can link to it - the node's "
            + "port_info emission did not register a port");

        Assert.IsTrue(
            ports.Any(p => p.PortDirection == PipeWirePortDirection.Out),
            "the exported source has no output port for a consumer to read from");
    }

    /// <summary>
    /// An exported source negotiates and its samples reach an ordinary consumer.
    /// </summary>
    /// <remarks>
    /// The whole path: the graph enumerates the port's parameters, picks the offered format, tells
    /// the node which buffers to use, and drives it. The consumer is a plain
    /// <see cref="PipeWireAudioCapture"/> that knows nothing about how the source is implemented,
    /// which is the point of exporting - other clients cannot tell the difference.
    /// </remarks>
    [TestMethod]
    public async Task AnExportedSource_NegotiatesAndItsSamplesReachAConsumer()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = $"pwnet-exportsrc-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-exportsrc", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // A ramp, so the consumer can prove it received what this node produced rather than silence
        // the graph inserted in its place.
        uint next = 0;

        await using PipeWireNodeProvider node = PipeWireNodeProvider.Create(
            ctx,
            name,
            PipeWireExportedFormat.AudioF32(Rate, Channels),
            SpaDirection.Output,
            new Dictionary<string, string>
            {
                [PipeWireKeys.PW_KEY_MEDIA_CLASS] = "Audio/Source",
            });

        node.ProcessCallback = (_, data) =>
        {
            Span<float> floats = MemoryMarshal.Cast<byte, float>(data);
            for (var i = 0; i < floats.Length; i++) floats[i] = ++next;
            return floats.Length * 4;
        };

        PipeWireNode? exported = null;
        for (var i = 0; i < 100 && exported is null; i++)
        {
            exported = reg.Current.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeName, name, StringComparison.Ordinal));

            if (exported is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(exported, "the exported node never appeared in the graph");

        var received = new List<float>();
        await using var capture = new PipeWireAudioCapture(ctx, $"{name}-sink");
        capture.FrameReady += (_, f) =>
        {
            ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(f.Samples);
            lock (received) { if (received.Count < 8000) foreach (float v in floats) received.Add(v); }
        };

        // Routed by the session manager, as upstream's export-source is: it exports with
        // node.autoconnect and lets the session manager link it, and never hand-links its raw port.
        // That matters here. A stream's port faces the graph in DSP form (audio/dsp, F32P), and a
        // raw port hand-linked to it shares no format with it - the link fails with "no more output
        // formats" whatever this node offers. Routing lets the session manager put the converter
        // the two sides need between them, which is how every exported node is normally reached.
        //
        // The one race to avoid is connecting before the node's port is registered. A node reaches
        // the registry before its ports do, and a target with nothing to link to yet is refused as
        // "no target node available", so the port is awaited first.
        PipeWirePort? source = null;
        for (var i = 0; i < 100 && source is null; i++)
        {
            source = reg.Current.Ports.FirstOrDefault(
                p => p.NodeId == exported!.NodeId && p.PortDirection == PipeWirePortDirection.Out);

            if (source is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(source, "the exported node never registered an output port to route to");

        capture.Connect(exported!.NodeId, sampleRate: Rate, channels: Channels, format: AudioSampleFormat.F32Le);
        await capture.WaitForStreamingAsync(cts.Token);

        for (var i = 0; i < 100; i++)
        {
            lock (received) { if (received.Count > 2000) break; }
            await Task.Delay(50, cts.Token);
        }

        float[] got;
        lock (received) got = [.. received];

        // The graph negotiated: the node was told a format and given buffers.
        Assert.IsNotNull(node.NegotiatedFormat, "the graph never settled a format on the exported node");
        Assert.IsTrue(node.BufferCount > 0, "the graph never gave the exported node any buffers");
        Assert.IsTrue(node.HasProcessed, "the exported node was never driven");

        Assert.IsTrue(got.Length > 1000, $"only {got.Length} samples reached the consumer");

        // And what arrived is the ramp this node wrote, in order - not silence, and not garbage.
        int start = 0;
        while (start < got.Length && got[start] == 0f) start++;

        Assert.IsTrue(
            got.Length - start > 500,
            "the consumer received only silence, so the exported node's data never reached it");

        var breaks = 0;
        for (int i = start + 1; i < got.Length; i++)
            if (got[i] != got[i - 1] + 1f) breaks++;

        Assert.IsTrue(
            breaks <= 2,
            $"the ramp broke {breaks} times, so the exported node's buffers were reordered or lost");
    }

    /// <summary>
    /// The same source, linked by the session manager instead of by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both routes are kept deliberately. The explicit-link test above pins the node's own
    /// negotiation and data path; this one pins that an exported node is a normal citizen of the
    /// graph - something WirePlumber will route a consumer to, exactly as it routes to the exported
    /// nodes behind every ALSA card.
    /// </para>
    /// <para>
    /// The wait for the port is the point. Connecting the instant the node appears fails with
    /// "no target node available", because a node reaches the registry before its ports do and the
    /// session manager finds nothing to link to. That is a race in the caller, not a refusal.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AnExportedSource_IsRoutedToByTheSessionManager()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = $"pwnet-exportauto-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-exportauto", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        uint next = 0;

        await using PipeWireNodeProvider node = PipeWireNodeProvider.Create(
            ctx,
            name,
            PipeWireExportedFormat.AudioF32(Rate, Channels),
            SpaDirection.Output,
            new Dictionary<string, string>
            {
                [PipeWireKeys.PW_KEY_MEDIA_CLASS] = "Audio/Source",
            });

        node.ProcessCallback = (_, data) =>
        {
            Span<float> floats = MemoryMarshal.Cast<byte, float>(data);
            for (var i = 0; i < floats.Length; i++) floats[i] = ++next;
            return floats.Length * 4;
        };

        PipeWireNode? exported = null;
        for (var i = 0; i < 100 && exported is null; i++)
        {
            exported = reg.Current.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeName, name, StringComparison.Ordinal));

            if (exported is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(exported, "the exported node never appeared in the graph");

        // Wait for the port before asking to be routed to it.
        var hasPort = false;
        for (var i = 0; i < 100 && !hasPort; i++)
        {
            hasPort = reg.Current.Ports.Any(
                p => p.NodeId == exported!.NodeId && p.PortDirection == PipeWirePortDirection.Out);

            if (!hasPort) await Task.Delay(50, cts.Token);
        }

        Assert.IsTrue(hasPort, "the exported node never exposed an output port to route to");

        var received = new List<float>();
        await using var capture = new PipeWireAudioCapture(ctx, $"{name}-sink");
        capture.FrameReady += (_, f) =>
        {
            ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(f.Samples);
            lock (received) { if (received.Count < 8000) foreach (float v in floats) received.Add(v); }
        };

        capture.Connect(exported!.NodeId, sampleRate: Rate, channels: Channels,
            format: AudioSampleFormat.F32Le);

        await capture.WaitForStreamingAsync(cts.Token);
        uint captureNode = await capture.WaitForNodeIdAsync(cts.Token);

        // The link itself, not just samples arriving: a consumer the session manager could not route
        // to this node falls back to the default source and streams that source's silence, which a
        // sample count alone takes for success. That is how this test once passed while nothing was
        // routed to the exported node at all.
        bool linked = false;
        for (var i = 0; i < 100 && !linked; i++)
        {
            linked = reg.Current.Links.Any(l => l.OutputNodeId == exported.NodeId && l.InputNodeId == captureNode);
            if (!linked) await Task.Delay(50, cts.Token);
        }

        Assert.IsTrue(linked, "the session manager never linked the consumer to the exported node");

        for (var i = 0; i < 100; i++)
        {
            lock (received) { if (received.Count > 2000) break; }
            await Task.Delay(50, cts.Token);
        }

        AssertRamp(received, "routed by the session manager");
    }

    /// <summary>
    /// <c>export-spa</c>: a node that lives inside a SPA plugin, published by us.
    /// </summary>
    /// <remarks>
    /// The other way to export. Nothing here implements a node - the factory already has one, and
    /// this process only hands its interface to the graph. That is also the mechanism behind
    /// <c>export-spa-device</c> and <c>bluez-session</c>; the difference there is which SPA
    /// interface is fetched from the handle, not how it gets exported.
    /// </remarks>
    [TestMethod]
    public async Task ASpaFactorysNode_CanBeExported()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = $"pwnet-exportspa-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-exportspa", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNodeProvider node;
        try
        {
            node = PipeWireNodeProvider.FromSpaFactory(
                ctx,
                "audiotestsrc",
                new Dictionary<string, string>
                {
                    [PipeWireKeys.PW_KEY_NODE_NAME] = name,
                    [PipeWireKeys.PW_KEY_MEDIA_CLASS] = "Audio/Source",
                },
                // Named outright, as upstream's export-spa does. A client's context.spa-libs map
                // does not cover audiotestsrc, so resolving it by name alone fails on a stock
                // install and the test skips itself claiming the plugin is not installed.
                libraryName: "audiotestsrc/libspa-audiotestsrc");
        }
        catch (InvalidOperationException ex)
        {
            // The support plugins are a separate package on some distributions.
            Assert.Inconclusive($"the audiotestsrc SPA factory is unavailable: {ex.Message}");
            return;
        }

        await using (node)
        {
            PipeWireNode? seen = null;
            for (var i = 0; i < 100 && seen is null; i++)
            {
                seen = reg.Current.Nodes.FirstOrDefault(
                    n => string.Equals(n.NodeName, name, StringComparison.Ordinal));

                if (seen is null) await Task.Delay(50, cts.Token);
            }

            Assert.IsNotNull(seen, "the exported SPA factory node never appeared in the graph");
            Assert.AreEqual("Audio/Source", seen!.MediaClass);
        }
    }

    /// <summary>
    /// <c>export-sink</c>: an exported node on the receiving end, handed what the graph plays.
    /// </summary>
    /// <remarks>
    /// The same interface with the data flowing the other way: the graph fills the buffer and this
    /// node reads it. What is checked is that the bytes handed over are the producer's, because a
    /// sink that read the whole allocation rather than the chunk the producer wrote would see
    /// stale tail bytes and never notice.
    /// </remarks>
    [TestMethod]
    public async Task AnExportedSink_IsHandedWhatAProducerSends()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string sinkName = $"pwnet-exportsink-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-exportsink", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        var consumed = new List<float>();

        await using PipeWireNodeProvider sink = PipeWireNodeProvider.Create(
            ctx,
            sinkName,
            PipeWireExportedFormat.AudioF32(Rate, Channels),
            SpaDirection.Input,
            new Dictionary<string, string>
            {
                [PipeWireKeys.PW_KEY_MEDIA_CLASS] = "Audio/Sink",
            });

        sink.ProcessCallback = (_, data) =>
        {
            ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(data);
            lock (consumed) { if (consumed.Count < 8000) foreach (float v in floats) consumed.Add(v); }
            return 0;
        };

        PipeWireNode? exported = null;
        for (var i = 0; i < 100 && exported is null; i++)
        {
            exported = reg.Current.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeName, sinkName, StringComparison.Ordinal));

            if (exported is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(exported, "the exported sink never appeared in the graph");

        // The port first, for the reason the source test gives: a node reaches the registry before
        // its ports, and a producer targeting a node with no port yet is refused as "no target node
        // available" rather than linked later.
        PipeWirePort? input = null;
        for (var i = 0; i < 100 && input is null; i++)
        {
            input = reg.Current.Ports.FirstOrDefault(
                p => p.NodeId == exported!.NodeId && p.PortDirection == PipeWirePortDirection.In);

            if (input is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(input, "the exported sink never registered an input port to route to");

        // An ordinary producer, which knows nothing about how the sink is implemented.
        uint next = 0;
        await using var output = new PipeWireAudioOutput(
            ctx, $"{sinkName}-src", Rate, Channels, AudioSampleFormat.F32Le);

        output.FillSamples += (_, samples, _, _, _) =>
        {
            Span<float> floats = MemoryMarshal.Cast<byte, float>(samples);
            for (var i = 0; i < floats.Length; i++) floats[i] = ++next;
            return samples.Length;
        };

        output.Connect(exported!.NodeId);

        for (var i = 0; i < 120; i++)
        {
            lock (consumed) { if (consumed.Count > 1000) break; }
            await Task.Delay(50, cts.Token);
        }

        float[] got;
        lock (consumed) got = [.. consumed];

        // A failure, not a skip: the port is registered and the producer was routed to it, so a
        // sink that is never driven is this node failing to negotiate or to be scheduled.
        Assert.IsNotNull(sink.NegotiatedFormat, "the graph never settled a format on the exported sink");
        Assert.IsTrue(sink.BufferCount > 0, "the graph never gave the exported sink any buffers");
        Assert.IsTrue(sink.HasProcessed, "the exported sink was linked but never driven");
        Assert.IsNull(sink.LastCallbackError, $"a sink callback faulted: {sink.LastCallbackError}");

        Assert.IsTrue(got.Length > 500, $"the exported sink was handed only {got.Length} samples");

        int start = 0;
        while (start < got.Length && got[start] == 0f) start++;

        Assert.IsTrue(
            got.Length - start > 200,
            "the exported sink was handed only silence, so the producer's data never reached it");
    }

    /// <summary>
    /// Disposing an exported node takes it out of the graph and frees what it published.
    /// </summary>
    /// <remarks>
    /// An exported node hands the graph raw pointers to memory this process owns, plus a handle
    /// pinning the managed instance. Getting teardown wrong here is not a leak of a managed object -
    /// it is the graph calling into freed memory on the realtime thread.
    /// </remarks>
    [TestMethod]
    public async Task DisposingAnExportedNode_RemovesItFromTheGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = $"pwnet-exportgone-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-exportgone", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNodeProvider node = PipeWireNodeProvider.Create(
            ctx, name, PipeWireExportedFormat.AudioF32(Rate, Channels));

        var appeared = false;
        for (var i = 0; i < 100 && !appeared; i++)
        {
            appeared = reg.Current.Nodes.Any(n => string.Equals(n.NodeName, name, StringComparison.Ordinal));
            if (!appeared) await Task.Delay(50, cts.Token);
        }

        Assert.IsTrue(appeared, "the exported node never appeared, so its removal proves nothing");

        await node.DisposeAsync();

        var gone = false;
        for (var i = 0; i < 100 && !gone; i++)
        {
            gone = !reg.Current.Nodes.Any(n => string.Equals(n.NodeName, name, StringComparison.Ordinal));
            if (!gone) await Task.Delay(50, cts.Token);
        }

        Assert.IsTrue(gone, "the exported node outlived its disposal, so the graph still holds it");

        // Disposing twice is what a using block layered over an explicit dispose does routinely.
        await node.DisposeAsync();
    }

    /// <summary>
    /// A process handler that throws is contained, not fatal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The process callback is invoked from C on the realtime data-loop thread. A managed exception
    /// cannot unwind through the native frames above it, so letting one escape terminates the whole
    /// process: the run ends as SIGABRT on <c>data-loop.0</c>, every remaining test is lost, and the
    /// only evidence is a core file that names no test.
    /// </para>
    /// <para>
    /// That is worth a test of its own because the handler is caller-supplied. Any consumer of this
    /// library can throw here by accident - a bad cast, an index slip - and the library's answer has
    /// to be to drop the cycle and record the fault, not to take the application down with it.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AProcessHandlerThatThrows_DropsTheCycleInsteadOfKillingTheProcess()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = $"pwnet-exportthrow-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-exportthrow", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        var calls = 0;

        await using PipeWireNodeProvider node = PipeWireNodeProvider.Create(
            ctx,
            name,
            PipeWireExportedFormat.AudioF32(Rate, Channels),
            SpaDirection.Output,
            new Dictionary<string, string>
            {
                [PipeWireKeys.PW_KEY_MEDIA_CLASS] = "Audio/Source",
            });

        node.ProcessCallback = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("deliberate fault from a process handler");
        };

        PipeWireNode? exported = null;
        for (var i = 0; i < 100 && exported is null; i++)
        {
            exported = reg.Current.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeName, name, StringComparison.Ordinal));

            if (exported is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(exported, "the exported node never appeared, so nothing would have driven it");

        var hasPort = false;
        for (var i = 0; i < 100 && !hasPort; i++)
        {
            hasPort = reg.Current.Ports.Any(
                p => p.NodeId == exported!.NodeId && p.PortDirection == PipeWirePortDirection.Out);

            if (!hasPort) await Task.Delay(50, cts.Token);
        }

        Assert.IsTrue(hasPort, "the exported node never exposed a port, so it would never be scheduled");

        await using var capture = new PipeWireAudioCapture(ctx, $"{name}-sink");
        capture.Connect(exported!.NodeId, sampleRate: Rate, channels: Channels,
            format: AudioSampleFormat.F32Le);

        await capture.WaitForStreamingAsync(cts.Token);
        await Task.Delay(700, cts.Token);

        // Reaching this line at all is most of the assertion: before the callback was guarded, the
        // throw above ended the test host here rather than failing this test.
        Assert.IsTrue(
            Volatile.Read(ref calls) > 0,
            "the handler was never invoked, so the throw was never actually exercised");

        Assert.IsInstanceOfType<InvalidOperationException>(
            node.LastProcessError,
            "the exception a cycle threw should be recorded where a caller can diagnose it");

        // And the graph keeps running: the node stays, and the context is still usable afterwards.
        Assert.IsTrue(
            reg.Current.Nodes.Any(n => string.Equals(n.NodeName, name, StringComparison.Ordinal)),
            "the node was torn out of the graph by a fault that should only have dropped one cycle");
    }

    /// <summary>
    /// An exported source is consumed by GStreamer's pipewiresrc, and what GStreamer writes out is
    /// the ramp this node produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The independent client. Every other test here reads the exported node with this library's
    /// own capture, so a mistake shared by both halves - a format both misread the same way, a
    /// buffer protocol both bend the same way - passes. pipewiresrc is a different implementation of
    /// the consumer side, written against upstream's contract rather than against this library.
    /// </para>
    /// <para>
    /// Routed by the session manager with <c>target-object</c>, as upstream's export-source is
    /// meant to be reached, and written to a raw file so the samples can be checked exactly.
    /// </para>
    /// <para>
    /// Stereo, upstream export-source's default, not mono. With a mono source WirePlumber configured
    /// pipewiresrc's DSP ports as stereo and linked the MONO port to FL only (pw-dump on the lab box,
    /// 2026-09-15), so the stream's converter averaged it with a silent FR and every sample arrived
    /// halved - correct mixing for that link, and a ramp that "breaks" on every sample. Stereo maps FL
    /// to FL and FR to FR, so nothing is remixed and the bytes GStreamer writes are the node's.
    /// </para>
    /// </remarks>
    [TestMethod]
    [TestCategory("RequiresGStreamer")]
    public async Task AnExportedSource_IsReadCorrectlyByGStreamer()
    {
        RequireLinux();
        GstTestSource.RequireGStreamer();
        using var cts = new CancellationTokenSource(Budget);

        string name = $"pwnet-exportgst-{Environment.ProcessId}";
        string output = Path.Combine(Path.GetTempPath(), $"{name}.f32");

        await using var ctx = new PipeWireContext("pwnet-exportgst", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        const int stereo = 2;
        uint next = 0;
        await using PipeWireNodeProvider node = PipeWireNodeProvider.Create(
            ctx,
            name,
            PipeWireExportedFormat.AudioF32(Rate, stereo),
            SpaDirection.Output,
            new Dictionary<string, string> { [PipeWireKeys.PW_KEY_MEDIA_CLASS] = "Audio/Source" });

        // Every cycle's io state, so a failure below says whether a quantum was lost at this end
        // (published over a buffer the consumer had not taken) or after it.
        node.ProduceTrace = new PipeWireNodeProvider.ProduceCycleRecord[4096];

        // The first sample of each published quantum, in cycle order, to line the ramp up with the
        // cycles that wrote it.
        var firstOfCycle = new float[4096];
        var cycles = 0;
        node.ProcessCallback = (_, data) =>
        {
            Span<float> floats = MemoryMarshal.Cast<byte, float>(data);
            if (cycles < firstOfCycle.Length) firstOfCycle[cycles] = next + 1;
            cycles++;
            for (var i = 0; i < floats.Length; i++) floats[i] = ++next;
            return floats.Length * 4;
        };

        PipeWirePort? port = null;
        for (var i = 0; i < 100 && port is null; i++)
        {
            port = reg.Current.Ports.FirstOrDefault(p =>
                reg.Current.Nodes.Any(n => n.NodeId == p.NodeId && string.Equals(n.NodeName, name, StringComparison.Ordinal))
                && p.PortDirection == PipeWirePortDirection.Out);
            if (port is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(port, "the exported node never registered an output port for GStreamer to reach");

        var psi = new ProcessStartInfo("/usr/bin/gst-launch-1.0")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        // pipewiresrc's own account of what it did with each buffer, and its stream's warnings: an
        // input stream with no free buffer drops the cycle and says so ("out of buffers on port",
        // audioconvert), which is what tells a consumer overrun from data lost at this end.
        psi.Environment["GST_DEBUG"] = "pipewiresrc:6";
        psi.Environment["GST_DEBUG_NO_COLOR"] = "1";
        psi.Environment["PIPEWIRE_DEBUG"] = "2";

        // min-buffers: pipewiresrc asks for one by default and negotiates two, so any moment
        // GStreamer holds both (filesink writing, the machine loaded) drops a whole cycle. Sixteen
        // is the headroom upstream's property exists to give a consumer that must not drop.
        foreach (string arg in new[]
                 {
                     "-q", "pipewiresrc", $"target-object={name}", "num-buffers=40", "min-buffers=16", "!",
                     $"audio/x-raw,format=F32LE,channels={stereo},rate={Rate}", "!",
                     "filesink", $"location={output}",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using Process gst = Process.Start(psi) ?? throw new InvalidOperationException("gst-launch-1.0 did not start");

        // Both streams drained: a child writing to a pipe nobody reads blocks once it fills, and a
        // blocked child looks exactly like a hung test.
        Task<string> stderr = gst.StandardError.ReadToEndAsync(cts.Token);
        Task<string> stdout = gst.StandardOutput.ReadToEndAsync(cts.Token);

        try
        {
            await gst.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            gst.Kill(entireProcessTree: true);
            throw;
        }

        string errors = await stderr;
        _ = await stdout;

        Assert.AreEqual(0, gst.ExitCode, $"gst-launch failed: {errors}");

        try
        {
            byte[] raw = await File.ReadAllBytesAsync(output, cts.Token);
            float[] got = MemoryMarshal.Cast<byte, float>(raw).ToArray();

            Assert.IsTrue(got.Length > 1000, $"GStreamer wrote only {got.Length} samples");

            int start = 0;
            while (start < got.Length && got[start] == 0f) start++;
            Assert.IsTrue(got.Length - start > 500, "GStreamer received only silence from the exported node");

            // Every break must be a whole number of cycles skipped, and reported by the consumer.
            // Our node records the value each published cycle started at; a lost cycle ends one run
            // on a cycle's last value and resumes on a later cycle's first, forward. Anything else
            // (a jump inside a cycle, backwards, a value never written) is data altered on the way.
            var cycleStarts = new HashSet<float>(firstOfCycle.Take(Math.Min(cycles, firstOfCycle.Length)));
            int dropsReported = errors.Split((char)10).Count(l => l.Contains("out of buffers", StringComparison.Ordinal));

            var breaks = 0;
            var altered = 0;
            int firstBreak = -1;
            var breakList = new List<string>();
            for (int i = start + 1; i < got.Length; i++)
            {
                if (got[i] == got[i - 1] + 1f) continue;
                breaks++;
                if (firstBreak < 0) firstBreak = i;
                bool wholeCycles = got[i] > got[i - 1] && cycleStarts.Contains(got[i]) && cycleStarts.Contains(got[i - 1] + 1f);
                if (!wholeCycles) altered++;
                if (breakList.Count < 16)
                    breakList.Add($"{i - start}:{got[i] - got[i - 1] - 1f:+0;-0}{(wholeCycles ? "" : "!")}");
            }

            // What the stream looked like where it first went wrong, so a failure says whether the
            // values were scaled (a volume), interpolated (a resampler), repeated or reordered.
            string around = firstBreak < 0
                ? ""
                : string.Join(", ", got[Math.Max(start, firstBreak - 4)..Math.Min(got.Length, firstBreak + 6)]);

            string gstLog = Path.Combine(Path.GetTempPath(), $"{name}.gst.log");
            if (altered > 0 || (breaks > 0 && dropsReported == 0))
            {
                // Status/buffer on entry -> buffer published : result, free buffers after / pool,
                // and the ramp value that cycle started at.
                var cycleLines = new System.Text.StringBuilder();
                PipeWireNodeProvider.ProduceCycleRecord[] trace = node.ProduceTrace!;
                int recorded = Math.Min(node.ProduceTraceCount, trace.Length);
                for (int c = 0, written = 0; c < recorded; c++)
                {
                    PipeWireNodeProvider.ProduceCycleRecord r = trace[c];
                    string at = r.Result == 2 && written < firstOfCycle.Length ? $" @{firstOfCycle[written++]}" : "";
                    cycleLines.Append(System.Globalization.CultureInfo.InvariantCulture,
                        $"{c}: s{r.EntryStatus}/b{(int)r.EntryBuffer} -> b{(int)r.Published} : {r.Result} free {r.FreeAfter}/{r.Pool}{at}")
                        .Append((char)10);
                }

                await File.WriteAllTextAsync(gstLog, errors + (char)10 + "--- exported node cycles ---" + (char)10 + cycleLines, cts.Token);
            }

            string detail = $"{breaks} break(s) in {got.Length - start} samples, first at {firstBreak - start}: [{around}]; "
                + $"breaks (index:jump, ! = not whole cycles): {string.Join(" ", breakList)}; "
                + $"the consumer reported {dropsReported} drop(s); logs: {gstLog}";

            Assert.AreEqual(0, altered, $"the exported node's data reached GStreamer altered: {detail}");
            Assert.IsTrue(breaks == 0 || dropsReported > 0,
                $"whole cycles went missing without the consumer reporting a drop, so they were lost between this node and it: {detail}");
        }
        finally
        {
            File.Delete(output);
        }
    }

    /// <summary>
    /// An exported source reconfigured over and over while it processes keeps delivering its ramp
    /// intact, and none of its callbacks faults.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hostile case for the node's buffer state. Every consumer that leaves takes the port's
    /// last link with it, and upstream then clears the port's format (<c>pw_impl_port_release_mix</c>),
    /// which clears its buffers and io; the next consumer sets a format, buffers and io again. All
    /// of that arrives on the main loop while the data loop may be inside <c>process</c>, which is
    /// the crossing the <c>SPA_IO_Buffers</c> swap is locked for. Overlapping a second consumer in
    /// alternate rounds adds a link to a port that is already running.
    /// </para>
    /// <para>
    /// A torn crossing shows as a fault in a callback (recorded, not thrown, since a throw there
    /// aborts the process), as a round that receives nothing, or as a ramp that breaks because a
    /// buffer was recycled while still in use.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AnExportedSourceReconfiguredWhileProcessing_KeepsItsDataIntact()
    {
        RequireLinux();
        const int Rounds = 6;
        using var cts = new CancellationTokenSource(Budget * 2);

        string name = $"pwnet-exportchurn-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-exportchurn", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        uint next = 0;
        await using PipeWireNodeProvider node = PipeWireNodeProvider.Create(
            ctx,
            name,
            PipeWireExportedFormat.AudioF32(Rate, Channels),
            SpaDirection.Output,
            new Dictionary<string, string> { [PipeWireKeys.PW_KEY_MEDIA_CLASS] = "Audio/Source" });

        node.ProcessCallback = (_, data) =>
        {
            Span<float> floats = MemoryMarshal.Cast<byte, float>(data);
            for (var i = 0; i < floats.Length; i++) floats[i] = ++next;
            return floats.Length * 4;
        };

        PipeWireNode? exported = null;
        PipeWirePort? source = null;
        for (var i = 0; i < 100 && source is null; i++)
        {
            exported ??= reg.Current.Nodes.FirstOrDefault(n => string.Equals(n.NodeName, name, StringComparison.Ordinal));
            if (exported is not null)
            {
                source = reg.Current.Ports.FirstOrDefault(
                    p => p.NodeId == exported.NodeId && p.PortDirection == PipeWirePortDirection.Out);
            }

            if (source is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(source, "the exported node never registered an output port to route to");

        for (int round = 0; round < Rounds; round++)
        {
            var first = new List<float>();
            var second = new List<float>();

            await using (PipeWireAudioCapture a = await ConnectRampConsumerAsync(ctx, $"{name}-a{round}", exported!.NodeId, first, cts.Token))
            {
                if (round % 2 == 1)
                {
                    await using PipeWireAudioCapture b = await ConnectRampConsumerAsync(ctx, $"{name}-b{round}", exported.NodeId, second, cts.Token);
                    await WaitForSamplesAsync(second, cts.Token);
                    AssertRamp(second, $"round {round}, joining consumer");
                }

                await WaitForSamplesAsync(first, cts.Token);
            }

            AssertRamp(first, $"round {round}");
        }

        Assert.IsNull(node.LastCallbackError, $"a node callback faulted while reconfigured: {node.LastCallbackError}");
        Assert.IsNull(node.LastProcessError, $"the process handler faulted while reconfigured: {node.LastProcessError}");
    }

    private static async Task<PipeWireAudioCapture> ConnectRampConsumerAsync(
        PipeWireContext ctx, string name, uint target, List<float> received, CancellationToken ct)
    {
        var capture = new PipeWireAudioCapture(ctx, name);
        capture.FrameReady += (_, f) =>
        {
            ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(f.Samples);
            lock (received) { if (received.Count < 8000) foreach (float v in floats) received.Add(v); }
        };

        capture.Connect(target, sampleRate: Rate, channels: Channels, format: AudioSampleFormat.F32Le);
        await capture.WaitForStreamingAsync(ct);
        return capture;
    }

    private static async Task WaitForSamplesAsync(List<float> received, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            lock (received) { if (received.Count > 2000) return; }
            await Task.Delay(50, ct);
        }
    }

    private static void AssertRamp(List<float> received, string what)
    {
        float[] got;
        lock (received) got = [.. received];

        int start = 0;
        while (start < got.Length && got[start] == 0f) start++;

        Assert.IsTrue(got.Length - start > 500, $"{what}: only {got.Length - start} samples of the ramp arrived");

        var breaks = 0;
        for (int i = start + 1; i < got.Length; i++)
            if (got[i] != got[i - 1] + 1f) breaks++;

        Assert.IsTrue(breaks <= 2, $"{what}: the ramp broke {breaks} times, so a buffer was reordered or recycled in use");
    }

    /// <summary>
    /// A node that misses a cycle can say so, and the graph takes the report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `xrun` is the only way a node reports an over or underrun; nothing can report one on its
    /// behalf, so a node without access to it leaves the daemon's accounting silently wrong. The
    /// graph installs the callback through `set_callbacks` once the node is scheduled, which is why
    /// this reports from inside a cycle rather than from the test thread.
    /// </para>
    /// <para>
    /// A `false` return is legal - it means the graph installed no xrun callback - so the assertion
    /// is that a report from inside a cycle is accepted, which is what proves the pointer the node
    /// stored is the graph's and is live.
    /// </para>
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task ANodeThatMissesACycle_CanReportTheXrunToTheGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = $"pwnet-xrun-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-xrun", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireNodeProvider node = PipeWireNodeProvider.Create(
            ctx,
            name,
            PipeWireExportedFormat.AudioF32(Rate, Channels),
            SpaDirection.Output,
            new Dictionary<string, string>
            {
                [PipeWireKeys.PW_KEY_MEDIA_CLASS] = "Audio/Source",
            });

        var reported = 0;
        var accepted = 0;

        node.ProcessCallback = (self, data) =>
        {
            // Reported from the realtime thread, which is where the graph expects its callbacks.
            if (Interlocked.Increment(ref reported) <= 3 && self.ReportXrun(1000, 250))
                Interlocked.Increment(ref accepted);

            return 0;
        };

        // Something has to pull, or the node is never scheduled and no callbacks are installed.
        await using var capture = new PipeWireAudioCapture(ctx, name + "-sink");
        capture.Connect(await WaitForNodeIdAsync(reg, name, cts.Token));

        for (var i = 0; i < 100 && Volatile.Read(ref reported) == 0; i++)
            await Task.Delay(50, cts.Token);

        Assert.IsTrue(Volatile.Read(ref reported) > 0, "the exported node was never driven");

        Assert.IsTrue(
            Volatile.Read(ref accepted) > 0,
            "the graph installed no xrun callback, so a node here cannot report a missed cycle at all");
    }

    private static async Task<uint> WaitForNodeIdAsync(
        PipeWireRegistry reg, string nodeName, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 100; i++)
        {
            PipeWireNode? found = reg.Current.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeName, nodeName, StringComparison.Ordinal));

            if (found is not null) return found.NodeId;
            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail($"the node '{nodeName}' never reached the graph");
        return 0;
    }

    /// <summary>A node with no process handler is driven anyway, and produces silence.</summary>
    /// <remarks>
    /// <para>
    /// Setting <c>ProcessCallback</c> is optional, and a node without one is a real shape: a node
    /// exported to hold a place in the graph, or one whose handler is attached later. The graph
    /// still schedules it, so every cycle reaches the produce path with nothing to call.
    /// </para>
    /// <para>
    /// The answer has to be an empty cycle rather than a refusal. Returning an error would make the
    /// graph treat the node as broken and tear the link down, where the honest outcome is a node
    /// that is simply not producing yet.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AnExportedNodeWithNoHandler_IsStillDrivenAndProducesSilence()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        string name = $"pwnet-nohandler-{Environment.ProcessId}";

        await using var ctx = new PipeWireContext("pwnet-nohandler", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireNodeProvider node = PipeWireNodeProvider.Create(
            ctx,
            name,
            PipeWireExportedFormat.AudioF32(Rate, Channels),
            SpaDirection.Output,
            new Dictionary<string, string>
            {
                [PipeWireKeys.PW_KEY_MEDIA_CLASS] = "Audio/Source",
            });

        // Deliberately no ProcessCallback.

        PipeWireNode? exported = null;
        for (var i = 0; i < 100 && exported is null; i++)
        {
            exported = reg.Current.Nodes.FirstOrDefault(
                n => string.Equals(n.NodeName, name, StringComparison.Ordinal));

            if (exported is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(exported, "the exported node never appeared in the graph");

        PipeWirePort? source = null;
        for (var i = 0; i < 100 && source is null; i++)
        {
            source = reg.Current.Ports.FirstOrDefault(
                p => p.NodeId == exported!.NodeId && p.PortDirection == PipeWirePortDirection.Out);

            if (source is null) await Task.Delay(50, cts.Token);
        }

        Assert.IsNotNull(source, "the exported node never registered an output port to route to");

        var frames = 0;
        var nonZero = 0;

        await using var capture = new PipeWireAudioCapture(ctx, $"{name}-sink");
        capture.FrameReady += (_, f) =>
        {
            Interlocked.Increment(ref frames);
            ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(f.Samples);
            foreach (float v in floats)
            {
                if (v != 0f) { Interlocked.Increment(ref nonZero); break; }
            }
        };

        capture.Connect(exported!.NodeId, sampleRate: Rate, channels: Channels, format: AudioSampleFormat.F32Le);
        await capture.WaitForStreamingAsync(cts.Token);

        for (var i = 0; i < 100 && Volatile.Read(ref frames) < 10; i++) await Task.Delay(50, cts.Token);

        Assert.IsTrue(
            node.HasBeenScheduled,
            "a node with no handler was never driven at all");

        Assert.IsFalse(
            node.HasProcessed,
            "a node with no handler reported that a handler had produced something");

        Assert.IsTrue(node.BufferCount > 0, "the graph never gave the node any buffers");
        Assert.IsNull(node.LastProcessError, "an absent handler was recorded as a process fault");

        Assert.AreEqual(
            0, Volatile.Read(ref nonZero),
            "a node with no handler produced something other than silence");

        // Still there: an empty cycle is not a reason for the graph to drop the node.
        Assert.IsTrue(
            reg.Current.Nodes.Any(n => n.NodeId == exported.NodeId),
            "the graph removed a node that was merely producing nothing");
    }
}
