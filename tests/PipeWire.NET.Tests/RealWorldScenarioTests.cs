using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Whole workflows rather than single calls: the shapes an application built on this library
/// actually performs, end to end, against a live session.
/// </summary>
/// <remarks>
/// Each of these crosses several parts of the library at once - registry, parameters, links,
/// metadata, filters - because that is where the interesting failures live. A single call being
/// correct is what the other suites establish; these check that the pieces still fit when used
/// together and in the order a real program would use them.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class RealWorldScenarioTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<(PipeWireContext Context, PipeWireRegistry Registry)> ConnectAsync(
        string name, CancellationToken cancellationToken)
    {
        var context = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cancellationToken);
        var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cancellationToken);
        return (context, registry);
    }

    private static string Unique(string prefix) =>
        $"{prefix}_{Environment.ProcessId}_{Random.Shared.Next():x}";

    /// <summary>Waits until a node has the ports it is expected to have.</summary>
    private static async Task<ImmutableArray<PipeWirePort>> PortsOfAsync(
        PipeWireRegistry registry, uint nodeId, int expected, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await registry.WaitForInitialEnumerationAsync(cancellationToken);

            ImmutableArray<PipeWirePort> ports = registry.Current.GetPortsForNode(nodeId);
            if (ports.Length >= expected) return ports;

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    [TestMethod]
    public async Task AStreamingMixer_BuildsItsGraphSetsLevelsAndTearsItDownCleanly()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-scenario-mixer", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // The shape a streaming app builds: several sources feeding one submix, each at its own
            // level, the submix at a master level.
            string submixName = Unique("pwnet_submix");
            PipeWireNode submix = await registry.CreateVirtualNode("Submix")
                .WithName(submixName).ExecuteAsync(cts.Token);

            var sources = new List<PipeWireNode>();
            for (int i = 0; i < 3; i++)
            {
                sources.Add(await registry.CreateVirtualNode($"Source{i}")
                    .WithName(Unique($"pwnet_src{i}")).ExecuteAsync(cts.Token));
            }

            // Every source's monitor ports feed the submix's playback ports. A sink's monitor is how
            // its output is tapped, which is the part that is easy to get backwards.
            ImmutableArray<PipeWirePort> submixIn = await PortsOfAsync(registry, submix.NodeId, 4, cts.Token);
            PipeWirePort[] submixInputs = [.. submixIn.Where(p => p.IsDataInput).OrderBy(p => p.PortName)];
            Assert.AreEqual(2, submixInputs.Length, "a stereo sink has two playback ports");

            var links = new List<uint>();
            foreach (PipeWireNode source in sources)
            {
                ImmutableArray<PipeWirePort> ports = await PortsOfAsync(registry, source.NodeId, 4, cts.Token);
                PipeWirePort[] monitors = [.. ports.Where(p => p.IsDataOutput).OrderBy(p => p.PortName)];
                Assert.AreEqual(2, monitors.Length, "a stereo sink exposes two monitor ports");

                for (int channel = 0; channel < 2; channel++)
                {
                    PipeWireLink link = await registry.CreateLink(monitors[channel], submixInputs[channel])
                        .ExecuteAsync(cts.Token);
                    links.Add(link.LinkId);
                }
            }

            // The graph now says what was built, from both ends.
            PipeWireGraphSnapshot graph = registry.Current;
            Assert.AreEqual(6, graph.GetLinksForNode(submix.NodeId).Count(),
                "three stereo sources make six links into the submix");

            foreach (PipeWireNode source in sources)
            {
                Assert.AreEqual(2, graph.GetLinksForNode(source.NodeId).Count(),
                    $"source {source.NodeId} should have two links out");
            }

            // Levels: each source quieter than the last, the submix at master level.
            await using PipeWireNodeControl master = registry.BindNode(submix.NodeId);
            await master.SetVolumeAsync(0.8f, cts.Token);

            for (int i = 0; i < sources.Count; i++)
            {
                await using PipeWireNodeControl channel = registry.BindNode(sources[i].NodeId);
                await channel.SetChannelVolumesAsync([0.2f * (i + 1), 0.2f * (i + 1)], cts.Token);
            }

            // Read every level back through fresh bindings, the way a UI redrawing itself would.
            // Polled, not read once: a parameter write returns when the daemon has processed it,
            // which is not when the node has applied it, and the last source written is the one
            // with the least time to have got there.
            Assert.AreEqual(0.8f, await SettledVolumeAsync(master, 0.8f, cts.Token), 0.01f);
            for (int i = 0; i < sources.Count; i++)
            {
                await using PipeWireNodeControl channel = registry.BindNode(sources[i].NodeId);
                float want = 0.2f * (i + 1);
                float got = await SettledChannelVolumeAsync(channel, want, cts.Token);
                Assert.AreEqual(want, got, 0.01f, $"source {i} level did not stick");
            }

            // Tear down in the awkward order: links first, then the sources, then the submix they
            // pointed at - and the graph must agree at every step.
            foreach (uint link in links)
                await registry.DestroyGlobalAsync(link, cts.Token);

            Assert.AreEqual(0, registry.Current.GetLinksForNode(submix.NodeId).Count(),
                "every link must be gone");

            foreach (PipeWireNode source in sources)
                await registry.DestroyGlobalAsync(source.NodeId, cts.Token);
            await registry.DestroyGlobalAsync(submix.NodeId, cts.Token);

            graph = registry.Current;
            Assert.IsNull(graph.GetNode(submix.NodeId));
            foreach (PipeWireNode source in sources)
                Assert.IsNull(graph.GetNode(source.NodeId));
        }
    }

    [TestMethod]
    public async Task DestroyingANodeMidGraph_TakesItsLinksWithItAndLeavesNothingDangling()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-scenario-cascade", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode a = await registry.CreateVirtualNode("CascadeA")
                .WithName(Unique("pwnet_casc_a")).ExecuteAsync(cts.Token);
            PipeWireNode b = await registry.CreateVirtualNode("CascadeB")
                .WithName(Unique("pwnet_casc_b")).ExecuteAsync(cts.Token);

            ImmutableArray<PipeWirePort> aPorts = await PortsOfAsync(registry, a.NodeId, 4, cts.Token);
            ImmutableArray<PipeWirePort> bPorts = await PortsOfAsync(registry, b.NodeId, 4, cts.Token);

            await registry.CreateLink(aPorts.First(p => p.IsDataOutput), bPorts.First(p => p.IsDataInput))
                .ExecuteAsync(cts.Token);

            Assert.AreEqual(1, registry.Current.GetLinksForNode(b.NodeId).Count());

            // Destroy the node in the middle. Its ports and links must leave the graph with it, and
            // no snapshot may ever be published that still holds a link to a port that is gone.
            await registry.DestroyGlobalAsync(a.NodeId, cts.Token);
            await registry.WaitForInitialEnumerationAsync(cts.Token);

            PipeWireGraphSnapshot graph = registry.Current;
            Assert.IsNull(graph.GetNode(a.NodeId));
            Assert.AreEqual(0, graph.GetPortsForNode(a.NodeId).Length, "its ports must be gone too");
            Assert.AreEqual(0, graph.GetLinksForNode(b.NodeId).Count(), "the link must have gone with it");

            foreach (PipeWireLink link in graph.Links)
            {
                Assert.IsNotNull(graph.GetNode(link.LinkOutputNode),
                    $"link {link.LinkId} points at output node {link.LinkOutputNode}, which is not in the graph");
                Assert.IsNotNull(graph.GetNode(link.LinkInputNode),
                    $"link {link.LinkId} points at input node {link.LinkInputNode}, which is not in the graph");
            }

            await registry.DestroyGlobalAsync(b.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task AFilterInsertedBetweenTwoNodes_ProcessesWhatPassesThrough()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-scenario-insert", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // The classic insert: source -> filter -> sink, with the filter doing the work. This is
            // the arrangement an equaliser or a noise gate lives in.
            PipeWireNode source = await registry.CreateVirtualNode("InsertSrc")
                .WithName(Unique("pwnet_insert_src")).ExecuteAsync(cts.Token);
            PipeWireNode sink = await registry.CreateVirtualNode("InsertSink")
                .WithName(Unique("pwnet_insert_sink")).ExecuteAsync(cts.Token);

            await using PipeWireFilter filter = PipeWireFilter.Create(ctx, Unique("pwnet_insert_filter"));
            PipeWireFilterPort input = filter.AddAudioPort(PipeWirePortDirection.In, "input_FL");
            PipeWireFilterPort output = filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");

            long cycles = 0;
            long copied = 0;
            var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            filter.ProcessCallback = (_, samples) =>
            {
                Interlocked.Increment(ref cycles);

                Span<float> from = input.GetSamples(samples);
                Span<float> to = output.GetSamples(samples);

                // Pass-through, which is what an insert does when it is doing nothing. Both spans
                // may legitimately be empty on a cycle the graph did not give this filter a buffer.
                if (!to.IsEmpty)
                {
                    if (from.IsEmpty) to.Clear();
                    else { from.CopyTo(to); Interlocked.Increment(ref copied); }
                    ran.TrySetResult();
                }
            };

            await filter.ConnectAsync(cancellationToken: cts.Token);
            uint filterNode = await filter.WaitForNodeIdAsync(cts.Token);

            ImmutableArray<PipeWirePort> filterPorts = await PortsOfAsync(registry, filterNode, 2, cts.Token);
            ImmutableArray<PipeWirePort> sourcePorts = await PortsOfAsync(registry, source.NodeId, 4, cts.Token);
            ImmutableArray<PipeWirePort> sinkPorts = await PortsOfAsync(registry, sink.NodeId, 4, cts.Token);

            await registry.CreateLink(
                sourcePorts.First(p => p.IsDataOutput),
                filterPorts.First(p => p.IsDataInput)).ExecuteAsync(cts.Token);

            await registry.CreateLink(
                filterPorts.First(p => p.IsDataOutput),
                sinkPorts.First(p => p.IsDataInput)).ExecuteAsync(cts.Token);

            await ran.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);

            Assert.IsTrue(Interlocked.Read(ref cycles) > 0, "the filter never ran");

            // And the graph shows the filter sitting between the two, with a link on each side.
            PipeWireGraphSnapshot graph = registry.Current;
            Assert.AreEqual(2, graph.GetLinksForNode(filterNode).Count(),
                "the filter must be linked on both sides");

            await registry.DestroyGlobalAsync(source.NodeId, cts.Token);
            await registry.DestroyGlobalAsync(sink.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ARealGStreamerProducer_IsDiscoveredNamedAndControllable()
    {
        RequireLinux();
        GstTestSource.RequireGStreamer();

        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-scenario-gst", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // A producer this library did not create, from another process, in a format it did not
            // choose. Everything below is what a mixer would do on discovering one.
            string name = Unique("pwnet_gst_audio");
            await using GstTestSource gst = await GstTestSource.StartAsync(
                ctx, name,
                "audiotestsrc is-live=true ! audioconvert ! audio/x-raw,format=F32LE,channels=2,rate=48000",
                "Stream/Output/Audio");

            await registry.WaitForInitialEnumerationAsync(cts.Token);

            PipeWireNode? produced = null;
            while (produced is null)
            {
                cts.Token.ThrowIfCancellationRequested();
                produced = registry.Current.Nodes.FirstOrDefault(n => n.NodeName == name);
                if (produced is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);
                    await registry.WaitForInitialEnumerationAsync(cts.Token);
                }
            }

            // It is a node like any other: identifiable, with ports. The ports arrive a moment after
            // the node, and capability is answered from them - so waiting for them comes first.
            Assert.AreEqual(PipeWireMediaKind.Audio, produced!.Media);

            ImmutableArray<PipeWirePort> ports = await PortsOfAsync(registry, produced.NodeId, 1, cts.Token);
            Assert.IsTrue(ports.Any(p => p.IsDataOutput), "a producer must expose an output port");
            Assert.IsTrue(registry.Current.CanCaptureFrom(produced),
                "a producer with an output port must be something the graph can capture from");

            // And its parameters are readable, which is the part that needs the binding to work
            // against a node this library never created.
            await using PipeWireNodeControl control = registry.BindNode(produced.NodeId);
            await control.ReadyAsync(cts.Token);

            Assert.IsTrue(control.Parameters.Length > 0, "a real producer must describe its parameters");
            Assert.IsTrue(control.CanRead(SpaParamType.Format) || control.CanRead(SpaParamType.EnumFormat),
                "a producer must describe the format it is producing");

            ImmutableArray<SpaObject> formats =
                await control.EnumerateParametersAsync(SpaParamType.EnumFormat, cts.Token);

            Assert.IsTrue(formats.Length > 0, "the producer must offer at least one format");
            Assert.IsTrue(
                formats.Any(f => f[SpaFormat.MediaType] is SpaId id && id.Value == (uint)SpaMediaType.Audio),
                "the offered formats must be audio");
        }
    }

    [TestMethod]
    public async Task ADeviceItsNodesAndTheDefaultSink_AgreeWithEachOther()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-scenario-device", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireGraphSnapshot graph = registry.Current;

            PipeWireDevice? card = graph.Devices
                .FirstOrDefault(d => string.Equals(d.Api, "alsa", StringComparison.Ordinal));
            if (card is null)
                Assert.Inconclusive("this session has no ALSA card.");

            // What a settings panel shows: the card, the nodes it provides, and which of them is the
            // system default. Every hop has to resolve, or the panel shows an orphan.
            await using PipeWireDeviceControl device = registry.BindDevice(card!.Id);
            await device.ReadyAsync(cts.Token);

            Assert.IsTrue(device.CanRead(SpaParamType.EnumProfile));
            SpaObject? profile = await device.GetProfileAsync(cts.Token);
            Assert.IsNotNull(profile, "a card in use reports its profile");

            PipeWireMetadataStore? defaults = registry.BindMetadataStore("default");
            if (defaults is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (defaults)
            {
                await defaults.ReadyAsync(cts.Token);

                string? sinkName = defaults.DefaultAudioSink?.NameValue;
                if (sinkName is null)
                    Assert.Inconclusive("this session has no default sink set.");

                PipeWireNode? defaultSink = registry.Current.Nodes
                    .FirstOrDefault(n => n.NodeName == sinkName);

                Assert.IsNotNull(defaultSink, $"the default sink '{sinkName}' must be a node in the graph");
                Assert.AreEqual(PipeWireMediaKind.Audio, defaultSink!.Media);
                Assert.IsTrue(registry.Current.CanSendTo(defaultSink),
                    "the default sink must be something audio can be sent to");

                // And it is controllable, which is what a volume slider needs.
                await using PipeWireNodeControl control = registry.BindNode(defaultSink.NodeId);
                await control.ReadyAsync(cts.Token);
                Assert.IsTrue(control.CanWrite(SpaParamType.Props),
                    "the default sink must accept a volume change");
            }
        }
    }

    [TestMethod]
    public async Task AUiHoldingASnapshotWhileTheGraphChanges_KeepsAConsistentView()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-scenario-ui", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // What a UI does: take a snapshot, draw from it, and not care that the graph moved on.
            PipeWireGraphSnapshot held = registry.Current;
            int nodesWhenTaken = held.Nodes.Length;

            var created = new List<uint>();
            for (int i = 0; i < 4; i++)
            {
                PipeWireNode node = await registry.CreateVirtualNode($"Ui{i}")
                    .WithName(Unique($"pwnet_ui{i}")).ExecuteAsync(cts.Token);
                created.Add(node.NodeId);
            }

            // The held snapshot has not moved, and still answers every query the same way.
            Assert.AreEqual(nodesWhenTaken, held.Nodes.Length, "a held snapshot must not change");
            foreach (uint id in created)
                Assert.IsNull(held.GetNode(id), "a snapshot must not gain nodes created after it");

            Assert.IsTrue(registry.Current.Nodes.Length > nodesWhenTaken, "the live graph did move on");
            Assert.IsTrue(registry.Current.Version > held.Version, "and its version advanced");

            foreach (uint id in created)
                await registry.DestroyGlobalAsync(id, cts.Token);

            // Still readable after the objects it describes are gone: a snapshot is data, not a
            // handle on anything native.
            Assert.AreEqual(nodesWhenTaken, held.Nodes.Length);
            foreach (PipeWireNode node in held.Nodes)
                Assert.IsNotNull(node.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Reads a node's volume until it reports the value written, or the budget runs out.</summary>
    /// <remarks>
    /// <c>SetParameterAsync</c> returns once the daemon has processed the write, not once the object
    /// has applied it. Reading once therefore races the object, and the race is invisible on an idle
    /// session and reliable on a busy one.
    /// </remarks>
    private static async Task<float> SettledVolumeAsync(
        PipeWireNodeControl node, float want, CancellationToken cancellationToken)
    {
        float last = float.NaN;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            last = (await node.GetVolumeAsync(cancellationToken).ConfigureAwait(false)) ?? float.NaN;
            if (Math.Abs(last - want) <= 0.01f) return last;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return last;
    }

    /// <inheritdoc cref="SettledVolumeAsync"/>
    private static async Task<float> SettledChannelVolumeAsync(
        PipeWireNodeControl node, float want, CancellationToken cancellationToken)
    {
        float last = float.NaN;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            ImmutableArray<float> volumes =
                await node.GetChannelVolumesAsync(cancellationToken).ConfigureAwait(false);

            last = volumes.IsDefaultOrEmpty ? float.NaN : volumes[0];
            if (Math.Abs(last - want) <= 0.01f) return last;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return last;
    }
}
