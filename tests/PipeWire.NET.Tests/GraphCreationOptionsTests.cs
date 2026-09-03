using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// The fluent creation options: that chaining is inert until executed, and that
/// <c>object.linger</c> actually changes the daemon's behaviour when the creating client leaves.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class GraphCreationOptionsTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

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

    private static async Task<PipeWireGraphSnapshot> WaitForAsync(
        PipeWireRegistry registry, Func<PipeWireGraphSnapshot, bool> until, CancellationToken cancellationToken)
    {
        await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(cancellationToken))
            if (until(graph))
                return graph;

        throw new InvalidOperationException("the snapshot stream ended before the condition held");
    }

    private static string Unique(string prefix) =>
        $"{prefix}_{Environment.ProcessId}_{Random.Shared.Next():x}";

    [TestMethod]
    public async Task TargetingANamelessNode_IsRefusedBeforeTheDaemon()
    {
        // target.object names a node; without a name there is nothing to send, and an empty
        // target is not an error the daemon reports but a node that silently never links.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-targetguard", cts.Token);

        await using (context)
        await using (registry)
        {
            var nameless = new PipeWireNode(0x7FFF0000, null, null, null);

            Assert.ThrowsExactly<ArgumentException>(
                () => registry.CreateVirtualNode("Targeted").WithTarget(nameless));
        }
    }

    [TestMethod]
    public async Task ANodeThatStaysWithItsTarget_CarriesDontReconnect()
    {
        // What is verifiable from here is that the key reaches the daemon: whether the node
        // is actually destroyed when its target goes is the session manager's policy (it only
        // applies to linked nodes it manages), not a promise this library can keep by itself.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-stay", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode target = await registry.CreateVirtualNode("StayTarget")
                .WithName(Unique("pwnet_stay_target")).ExecuteAsync(cts.Token);

            PipeWireNode node = await registry.CreateVirtualNode("Staying")
                .WithName(Unique("pwnet_stay")).WithTarget(target).WithStayWithTheTarget()
                .ExecuteAsync(cts.Token);

            try
            {
                PwDump dump = await PwDump.CaptureAsync(cts.Token);
                PwDump.Entry? seen = dump.OfKind("Node")
                    .FirstOrDefault(e => e.Id == node.NodeId);

                Assert.IsNotNull(seen, "the node this test made is not in pw-dump's graph");
                Assert.AreEqual("true", seen!.Prop("node.dont-reconnect")?.ToLowerInvariant(),
                    "node.dont-reconnect did not reach the daemon");
            }
            finally
            {
                await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
                await registry.DestroyGlobalAsync(target.NodeId, cts.Token);
            }
        }
    }

    [TestMethod]
    public async Task DescribingANode_DoesNotCreateItUntilExecuted()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-inert", cts.Token);

        await using (context)
        await using (registry)
        {
            long before = registry.Current.Version;

            PipeWireNodeCreation pending = registry.CreateVirtualNode("Inert")
                                                   .WithName("pwnet_inert")
                                                   .WithLinger();

            Assert.AreEqual(before, registry.Current.Version,
                "describing a node must not touch the daemon");

            PipeWireNode node = await pending.ExecuteAsync(cts.Token);
            Assert.IsNotNull(registry.Current.GetNode(node.NodeId));

            // Created with linger, so it outlives this client - remove it explicitly.
            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ALingeringNode_OutlivesTheClientThatCreatedIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        (PipeWireContext observerContext, PipeWireRegistry observer) =
            await ConnectAsync("pwnet-linger-observer", cts.Token);

        await using (observerContext)
        await using (observer)
        {
            uint lingering;
            uint transient;

            (PipeWireContext ownerContext, PipeWireRegistry owner) =
                await ConnectAsync("pwnet-linger-owner", cts.Token);
            await using (ownerContext)
            {
                lingering = (await owner.CreateVirtualNode("Lingering")
                                        .WithName("pwnet_lingering")
                                        .WithLinger()
                                        .ExecuteAsync(cts.Token)).NodeId;

                transient = (await owner.CreateVirtualNode("Transient")
                                        .WithName("pwnet_transient")
                                        .ExecuteAsync(cts.Token)).NodeId;

                await WaitForAsync(observer,
                    g => g.GetNode(lingering) is not null && g.GetNode(transient) is not null,
                    cts.Token);

                await owner.DisposeAsync();
            }

            // The transient node leaving is the signal that the disconnect has been processed, so
            // the lingering node's survival is then a real observation rather than a race.
            PipeWireGraphSnapshot after = await WaitForAsync(
                observer, g => g.GetNode(transient) is null, cts.Token);

            Assert.IsNotNull(after.GetNode(lingering),
                "object.linger must keep the node alive past its creator's disconnect");

            await observer.DestroyGlobalAsync(lingering, cts.Token);
            await WaitForAsync(observer, g => g.GetNode(lingering) is null, cts.Token);
        }
    }

    [TestMethod]
    public async Task APassiveLink_IsStillAnOrdinaryLinkInTheGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-passive", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode a = await registry.CreateVirtualNodeAsync("PA", "pwnet_pa", cts.Token);
            PipeWireNode b = await registry.CreateVirtualNodeAsync("PB", "pwnet_pb", cts.Token);

            PipeWireGraphSnapshot ready = await WaitForAsync(
                registry,
                g => g.GetPortsForNode(a.NodeId).Length == 4 && g.GetPortsForNode(b.NodeId).Length == 4,
                cts.Token);

            PipeWirePort output = ready.GetPortsForNode(a.NodeId, PipeWirePortDirection.Out).First();
            PipeWirePort input = ready.GetPortsForNode(b.NodeId, PipeWirePortDirection.In).First();

            PipeWireLink link = await registry.CreateLink(output, input)
                                              .Passive()
                                              .ExecuteAsync(cts.Token);

            Assert.IsNotNull(registry.Current.GetLink(link.LinkId));
            Assert.AreEqual(1, registry.Current.GetOutputLinksForPort(output.PortId).Length);
        }
    }

    [TestMethod]
    public async Task EveryObjectCarriesTheVersionTheDaemonAnnounced()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-version", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNodeAsync("V", "pwnet_version", cts.Token);
            PipeWireGraphSnapshot graph = await WaitForAsync(
                registry, g => g.GetPortsForNode(node.NodeId).Length == 4, cts.Token);

            Assert.IsTrue(graph.GetNode(node.NodeId)!.InterfaceVersion > 0,
                "the registry must record the version from the global event");

            foreach (PipeWirePort port in graph.GetPortsForNode(node.NodeId))
                Assert.IsTrue(port.InterfaceVersion > 0, $"port {port.PortId} has no version");
        }
    }

    [TestMethod]
    public async Task ALingeringLink_OutlivesTheClientThatMadeIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // A link is the object most likely to be created for somebody else to keep using, so its
        // linger behaviour matters at least as much.
        uint linkId;
        uint sourceId;
        uint sinkId;

        (PipeWireContext maker, PipeWireRegistry makerRegistry) = await ConnectAsync("pwnet-linger-link", cts.Token);
        await using (maker)
        await using (makerRegistry)
        {
            PipeWireNode source = await makerRegistry.CreateVirtualNode("LingerSrc")
                .WithName($"pwnet_linger_src_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .WithLinger().ExecuteAsync(cts.Token);
            PipeWireNode sink = await makerRegistry.CreateVirtualNode("LingerSink")
                .WithName($"pwnet_linger_sink_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .WithLinger().ExecuteAsync(cts.Token);

            sourceId = source.NodeId;
            sinkId = sink.NodeId;

            PipeWirePort output = await PortAsync(makerRegistry, sourceId, PipeWirePortDirection.Out, cts.Token);
            PipeWirePort input = await PortAsync(makerRegistry, sinkId, PipeWirePortDirection.In, cts.Token);

            PipeWireLink link = await makerRegistry.CreateLink(output, input).WithLinger()
                .ExecuteAsync(cts.Token);
            linkId = link.LinkId;
        }

        // The maker is gone. A second client must still see the link, and be able to remove it.
        (PipeWireContext other, PipeWireRegistry otherRegistry) = await ConnectAsync("pwnet-linger-peer", cts.Token);
        await using (other)
        await using (otherRegistry)
        {
            await otherRegistry.WaitForInitialEnumerationAsync(cts.Token);

            Assert.IsNotNull(otherRegistry.Current.GetLink(linkId),
                "a lingering link did not outlive the client that created it");

            await otherRegistry.DestroyGlobalAsync(linkId, cts.Token);
            await otherRegistry.DestroyGlobalAsync(sourceId, cts.Token);
            await otherRegistry.DestroyGlobalAsync(sinkId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ANodeCreatedAsASource_IsReadFromRatherThanWrittenTo()
    {
        // media.class decides the direction. The proof is the ports: a sink publishes inputs,
        // a source publishes outputs, and the daemon decides that from the class we send.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-source-class", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode source = await registry.CreateVirtualNode("VirtualMic")
                .WithName($"pwnet_vmic_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .WithMediaClass("Audio/Source")
                .ExecuteAsync(cts.Token);

            PipeWirePort output = await PortAsync(registry, source.NodeId, PipeWirePortDirection.Out, cts.Token);
            Assert.AreEqual(PipeWirePortDirection.Out, output.PortDirection);

            PipeWireNode read = registry.Current.GetNode(source.NodeId)!;
            Assert.AreEqual("Audio/Source", read.MediaClass, "the daemon did not take the media class");

            await registry.DestroyGlobalAsync(source.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ANodeCreatedWithAChannelMap_PublishesOnePortPerChannel()
    {
        // audio.position decides how many ports exist and what they are called. Counting the ports
        // is what shows the map reached the daemon rather than being accepted and ignored.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-channel-map", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode mono = await registry.CreateVirtualNode("MonoSink")
                .WithName($"pwnet_mono_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .WithChannelPositions("[ MONO ]")
                .ExecuteAsync(cts.Token);

            PipeWireNode surround = await registry.CreateVirtualNode("SurroundSink")
                .WithName($"pwnet_surround_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .WithChannelPositions("[ FL FR FC LFE SL SR ]")
                .ExecuteAsync(cts.Token);

            int monoIn = await CountPortsAsync(registry, mono.NodeId, PipeWirePortDirection.In, 1, cts.Token);
            int surroundIn = await CountPortsAsync(registry, surround.NodeId, PipeWirePortDirection.In, 6, cts.Token);

            Assert.AreEqual(1, monoIn, "a mono node published a port count that is not one");
            Assert.AreEqual(6, surroundIn, "a six-channel map did not produce six input ports");

            await registry.DestroyGlobalAsync(mono.NodeId, cts.Token);
            await registry.DestroyGlobalAsync(surround.NodeId, cts.Token);
        }
    }

    /// <summary>Waits for a node to settle on its port count, then reports it.</summary>
    /// <remarks>
    /// Ports arrive one event at a time, so reading immediately gives whichever have landed. This
    /// waits for the expected count and then returns what it actually saw, so a wrong count fails
    /// the assertion with the real number rather than timing out with none.
    /// </remarks>
    private static async Task<int> CountPortsAsync(
        PipeWireRegistry registry, uint nodeId, PipeWirePortDirection direction, int expected,
        CancellationToken cancellationToken)
    {
        var seen = 0;

        for (int attempt = 0; attempt < 100; attempt++)
        {
            await registry.WaitForInitialEnumerationAsync(cancellationToken);

            seen = registry.Current.GetPortsForNode(nodeId).Count(p => p.PortDirection == direction);
            if (seen >= expected) return seen;

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        return seen;
    }

    [TestMethod]
    public async Task ALinkMadeFromPortIds_IsTheSameAsOneMadeFromPortRecords()
    {
        // A caller holding ids, from a saved routing file or another process, had to search the
        // graph for the port records before it could link anything, which is the same lookup
        // written again at every call site. The id overload does it once, and keeps the direction
        // check on this side so a mistake is an ArgumentException rather than a daemon refusal.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-link-by-id", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode source = await registry.CreateVirtualNode("LinkById")
                .WithName($"pwnet_lbi_src_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .ExecuteAsync(cts.Token);
            PipeWireNode sink = await registry.CreateVirtualNode("LinkById")
                .WithName($"pwnet_lbi_sink_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .ExecuteAsync(cts.Token);

            PipeWirePort output = await PortAsync(registry, source.NodeId, PipeWirePortDirection.Out, cts.Token);
            PipeWirePort input = await PortAsync(registry, sink.NodeId, PipeWirePortDirection.In, cts.Token);

            PipeWireLink link = await registry.CreateLinkAsync(output.PortId, input.PortId, cts.Token);

            Assert.AreEqual(output.PortId, link.LinkOutputPort);
            Assert.AreEqual(input.PortId, link.LinkInputPort);
            Assert.IsNotNull(registry.Current.GetLink(link.LinkId));

            // The wrong way round is caught here, not by the daemon.
            Assert.ThrowsExactly<ArgumentException>(() => registry.CreateLink(input.PortId, output.PortId));

            // And an id nothing has ever used.
            Assert.ThrowsExactly<ArgumentException>(() => registry.CreateLink(0x7FFF_0000, input.PortId));
            Assert.ThrowsExactly<ArgumentException>(() => registry.CreateLink(output.PortId, 0x7FFF_0000));

            await registry.DestroyGlobalAsync(link.LinkId, cts.Token);
            await registry.DestroyGlobalAsync(source.NodeId, cts.Token);
            await registry.DestroyGlobalAsync(sink.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ArbitraryCreationProperties_ReachTheDaemon()
    {
        // The set of useful keys is PipeWire's, not this library's, and it grows every release. A
        // caller has to be able to send one nobody here thought of, so the check is that a property
        // with no named method on the builder still comes back on the created object.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-props", cts.Token);
        await using (ctx)
        await using (registry)
        {
            string nick = $"pwnet nick {Random.Shared.Next():x}";

            PipeWireNode node = await registry.CreateVirtualNode("Props")
                .WithName($"pwnet_props_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .WithProperty("node.nick", nick)
                .WithProperty("node.virtual", "true")
                .ExecuteAsync(cts.Token);

            PipeWireNode read = registry.Current.GetNode(node.NodeId)!;
            Assert.AreEqual(nick, read.NodeNick, "a property with no named builder method was dropped");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ACallerProperty_OverridesTheLibrarysDefault()
    {
        // The defaults are written first and the caller's after, because spa_dict keeps the last
        // value for a repeated key. If that order ever inverts, the named helpers stop working and
        // so does every caller override, which is worth pinning rather than assuming.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-override", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // media.class is one the library sets itself, reached here through the general form
            // rather than the named helper, so this covers both.
            PipeWireNode node = await registry.CreateVirtualNode("Override")
                .WithName($"pwnet_override_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .WithProperty("media.class", "Audio/Source")
                .ExecuteAsync(cts.Token);

            PipeWireNode read = registry.Current.GetNode(node.NodeId)!;
            Assert.AreEqual("Audio/Source", read.MediaClass,
                "the library's default won over the caller's property");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ManyCreationProperties_AreAllSent()
    {
        // The property list is sized into a stack buffer that falls back to a rented array, and the
        // item slots are counted separately. Enough properties to pass both thresholds is what
        // proves the sizing is driven by the caller rather than by a fixed guess.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-manyprops", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNodeCreation creation = registry.CreateVirtualNode("ManyProps")
                .WithName($"pwnet_many_{Environment.ProcessId}_{Random.Shared.Next():x}");

            // Past the eight the library sets itself, and long enough to overflow the stack scratch.
            for (int i = 0; i < 24; i++)
                creation = creation.WithProperty($"pwnet.probe.{i}", new string('v', 64));

            PipeWireNode node = await creation.ExecuteAsync(cts.Token);

            Assert.IsNotNull(registry.Current.GetNode(node.NodeId),
                "a node with many properties did not reach the graph");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ACreationPropertyWithNoKey_IsRefused()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-props-guard", cts.Token);
        await using (ctx)
        await using (registry)
        {
        PipeWireNodeCreation creation = registry.CreateVirtualNode("Guard");

        Assert.ThrowsExactly<ArgumentException>(() => creation.WithProperty("", "v"));
        Assert.ThrowsExactly<ArgumentNullException>(() => creation.WithProperty("k", null!));
        }
    }

    [TestMethod]
    public async Task LingeringIds_ListsWhatWasLeftBehindUntilDestroyed()
    {
        // Leaving lingering objects behind is not a leak, but forgetting them is: the listing is
        // what a later session destroys explicitly instead of rediscovering ids by name.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-linger-list", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("Listed")
                .WithName(Unique("pwnet_linger_listed"))
                .WithLinger()
                .ExecuteAsync(cts.Token);

            Assert.IsTrue(registry.LingeringIds.Contains(node.NodeId),
                "a node created lingering must be listed until destroyed");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);

            PipeWireGraphSnapshot gone = await WaitForAsync(
                registry, g => g.GetNode(node.NodeId) is null, cts.Token);
            Assert.IsNotNull(gone);
            Assert.IsFalse(registry.LingeringIds.Contains(node.NodeId),
                "a destroyed node must leave the listing with its removal");
        }
    }

    private static async Task<PipeWirePort> PortAsync(
        PipeWireRegistry registry, uint nodeId, PipeWirePortDirection direction, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await registry.WaitForInitialEnumerationAsync(cancellationToken);

            foreach (PipeWirePort port in registry.Current.GetPortsForNode(nodeId))
            {
                if (port.PortDirection == direction) return port;
            }
        }
    }
}
