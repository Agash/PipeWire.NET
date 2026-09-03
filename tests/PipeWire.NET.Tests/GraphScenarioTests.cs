using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// End-to-end scenarios taken from the three real consumers rather than from the API surface: a
/// patchbay that shows and rewires the graph, a transport agent that publishes a node and finds it
/// again by id, and a routing setup that has to survive the process that created it.
/// </summary>
/// <remarks>
/// These exist because the unit tests each prove one call works, which is not the same as the
/// sequence a consumer actually performs working. The risk is in the joins between calls, not the calls.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class GraphScenarioTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

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

    private static Task<PipeWireGraphSnapshot> WaitForPortsAsync(
        PipeWireRegistry registry, uint nodeId, CancellationToken cancellationToken) =>
        WaitForAsync(registry, g => g.GetPortsForNode(nodeId).Length == 4, cancellationToken);

    // ---------------------------------------------------------------- patchbay

    /// <summary>
    /// The Patchfeld shape: render every node with its ports, wire two of them together, redraw
    /// from the change notification, then unwire. A patchbay reads the graph far more often than it
    /// writes it, so the read path has to be complete on its own.
    /// </summary>
    [TestMethod]
    public async Task Patchbay_RendersRewiresAndRedrawsFromNotifications()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-patchbay", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode source = await registry.CreateVirtualNodeAsync("Deck A", "pwnet_pb_a", cts.Token);
            PipeWireNode sink = await registry.CreateVirtualNodeAsync("Deck B", "pwnet_pb_b", cts.Token);

            PipeWireGraphSnapshot ready = await WaitForAsync(
                registry,
                g => g.GetPortsForNode(source.NodeId).Length == 4 && g.GetPortsForNode(sink.NodeId).Length == 4,
                cts.Token);

            // 1. Draw. Everything the UI needs must come off one snapshot, with no second call that
            //    could observe a different graph half way through rendering.
            foreach (PipeWireNode node in ready.Nodes)
            {
                Assert.IsNotNull(ready.GetNode(node.NodeId));
                foreach (PipeWirePort port in ready.GetPortsForNode(node.NodeId))
                {
                    Assert.AreEqual(node.NodeId, port.NodeId, "a port must agree with the node it is filed under");
                    Assert.IsNotNull(ready.GetPort(port.PortId));
                }
            }

            // 2. Redraw on change, which is how a patchbay stays live.
            var redraws = 0;
            registry.GraphChanged += (_, _) => Interlocked.Increment(ref redraws);

            PipeWirePort outLeft = ready.GetPortsForNode(source.NodeId, PipeWirePortDirection.Out)
                                       .OrderBy(p => p.PortId).First();
            PipeWirePort inLeft = ready.GetPortsForNode(sink.NodeId, PipeWirePortDirection.In)
                                       .OrderBy(p => p.PortId).First();

            PipeWireLink link = await registry.CreateLink(outLeft, inLeft).ExecuteAsync(cts.Token);

            // 3. The cable is drawn from the link's own endpoints, which must resolve back to ports.
            PipeWireGraphSnapshot wired = registry.Current;
            (PipeWirePort? from, PipeWirePort? to) = wired.GetEndpoints(link);
            Assert.IsNotNull(from, "the link's output port must resolve for a cable to be drawn");
            Assert.IsNotNull(to, "the link's input port must resolve");
            Assert.AreEqual(outLeft.PortId, from!.PortId);
            Assert.AreEqual(inLeft.PortId, to!.PortId);
            Assert.AreEqual(source.NodeId, wired.GetNodeForPort(from)!.NodeId);

            // 4. Unwire, and confirm the cable disappears from both endpoints, not just one.
            await registry.RemoveLinkAsync(link, cts.Token);
            PipeWireGraphSnapshot unwired = await WaitForAsync(
                registry, g => g.GetLink(link.LinkId) is null, cts.Token);

            Assert.AreEqual(0, unwired.GetOutputLinksForPort(outLeft.PortId).Length);
            Assert.AreEqual(0, unwired.GetInputLinksForPort(inLeft.PortId).Length);
            Assert.IsTrue(Volatile.Read(ref redraws) > 0, "the patchbay was never told to redraw");
        }
    }

    /// <summary>
    /// Ids are unique among live objects only, and the daemon reissues them straight away. A
    /// patchbay that keys its view on an id must therefore treat a stale one as a different object,
    /// which is the trap this pins.
    /// </summary>
    [TestMethod]
    public async Task Patchbay_MustNotTrustAStaleIdAfterRemoval()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-staleid", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode first = await registry.CreateVirtualNodeAsync("First", "pwnet_stale_1", cts.Token);
            uint id = first.NodeId;

            await registry.DestroyGlobalAsync(id, cts.Token);
            await WaitForAsync(registry, g => g.GetNode(id) is null, cts.Token);

            PipeWireNode second = await registry.CreateVirtualNodeAsync("Second", "pwnet_stale_2", cts.Token);

            if (second.NodeId != id)
                Assert.Inconclusive("the daemon did not reuse the id this run; the hazard is unchanged");

            // Same id, different object. Nothing on the entity distinguishes them, so a consumer
            // has to compare against the snapshot it is currently rendering, not a remembered id.
            PipeWireNode? live = registry.Current.GetNode(id);
            Assert.IsNotNull(live);
            Assert.AreEqual("pwnet_stale_2", live!.NodeName,
                "the reused id now names a different node; holding the old reference is a bug");
            Assert.AreNotEqual(first.NodeName, live.NodeName);
        }
    }

    // ---------------------------------------------------------- transport agent

    /// <summary>
    /// The StreamTransport shape: publish a virtual node, then find it in the graph by name and
    /// confirm the id the stream reports is the id the registry filed it under. The agent captures
    /// by node id, so a disagreement here means it captures from the wrong node or from nothing.
    /// </summary>
    [TestMethod]
    public async Task TransportAgent_PublishedNodeIsDiscoverableByNameAndAgreesOnItsId()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-agent", cts.Token);

        await using (context)
        await using (registry)
        {
            const string NodeName = "pwnet_agent_publish";

            await using var publisher = new PipeWireVideoOutput(context, NodeName, 320, 240, PixelFormat.Bgra);
            publisher.Connect();

            PipeWireGraphSnapshot graph = await WaitForAsync(
                registry, g => g.Nodes.Any(n => n.NodeName == NodeName), cts.Token);

            PipeWireNode published = graph.Nodes.First(n => n.NodeName == NodeName);

            Assert.IsNotNull(publisher.NodeId,
                "a connected stream must expose its node id, that is how a consumer targets it");
            Assert.AreEqual(published.NodeId, publisher.NodeId!.Value,
                "the stream and the registry must agree on which node this is");

            // The agent then looks up ports to link or to inspect; they must be filed under that id.
            PipeWireGraphSnapshot withPorts = await WaitForAsync(
                registry, g => g.GetPortsForNode(published.NodeId).Length > 0, cts.Token);

            Assert.IsTrue(withPorts.GetPortsForNode(published.NodeId).Length > 0);
            foreach (PipeWirePort port in withPorts.GetPortsForNode(published.NodeId))
                Assert.AreEqual(published.NodeId, port.NodeId);
        }
    }

    /// <summary>
    /// A capture agent picks its target by media class. Control and notify ports must not be
    /// mistaken for data flow when it decides what it can pull from.
    /// </summary>
    [TestMethod]
    public async Task TransportAgent_SelectsTargetsByClassAndDataDirectionOnly()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-select", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode sink = await registry.CreateVirtualNodeAsync("Target", "pwnet_select", cts.Token);
            PipeWireGraphSnapshot graph = await WaitForPortsAsync(registry, sink.NodeId, cts.Token);

            PipeWireNode node = graph.GetNode(sink.NodeId)!;
            Assert.AreEqual("Audio/Sink", node.MediaClass);

            // Identity says what it is: audio, and a sink.
            Assert.AreEqual(PipeWireMediaKind.Audio, node.Media);
            Assert.AreEqual(PipeWireMediaFlow.Sink, node.Flow);

            // Capability says what you can do with it, and the two disagree on purpose: a sink is
            // still readable through its monitor ports.
            Assert.IsTrue(graph.CanCaptureFrom(node), "a sink is readable through its monitor ports");
            Assert.IsTrue(graph.CanSendTo(node), "and writable through its playback ports");

            // And it must not appear as video by either measure.
            Assert.AreNotEqual(PipeWireMediaKind.Video, node.Media);
            CollectionAssert.DoesNotContain(graph.GetVideoSources().ToArray(), node);
            CollectionAssert.Contains(graph.GetAudioSources().ToArray(), node,
                "an audio sink with monitor ports is a legitimate audio source to capture from");

            foreach (PipeWirePort port in graph.GetPortsForNode(sink.NodeId))
            {
                // Exactly one of the four categories, never two.
                int categories = (port.IsDataInput ? 1 : 0) + (port.IsDataOutput ? 1 : 0)
                               + (port.IsControl ? 1 : 0) + (port.IsNotify ? 1 : 0);
                Assert.AreEqual(1, categories, $"port {port.PortId} classified as {categories} kinds");
            }

            // And the audio-source view must not invent nodes that are not in the graph.
            foreach (PipeWireNode candidate in graph.GetAudioSources())
                Assert.IsNotNull(graph.GetNode(candidate.NodeId));
        }
    }

    // ------------------------------------------------------------- routing setup

    /// <summary>
    /// The StreamWeaver shape: a routing setup built by one process, still standing after that
    /// process exits, and then taken down deliberately by the next one. This is the whole reason
    /// <c>object.linger</c> exists, and it is the case a stream-shaped API gets wrong.
    /// </summary>
    [TestMethod]
    public async Task RoutingSetup_SurvivesTheProcessThatBuiltItAndIsRemovableLater()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        (PipeWireContext observerContext, PipeWireRegistry observer) =
            await ConnectAsync("pwnet-routing-observer", cts.Token);

        await using (observerContext)
        await using (observer)
        {
            uint sourceId, sinkId, linkId;

            // --- first "run of the app": build the routing and leave.
            (PipeWireContext builderContext, PipeWireRegistry builder) =
                await ConnectAsync("pwnet-routing-builder", cts.Token);
            await using (builderContext)
            {
                PipeWireNode a = await builder.CreateVirtualNode("Routed A")
                                              .WithName("pwnet_route_a").WithLinger().ExecuteAsync(cts.Token);
                PipeWireNode b = await builder.CreateVirtualNode("Routed B")
                                              .WithName("pwnet_route_b").WithLinger().ExecuteAsync(cts.Token);
                sourceId = a.NodeId;
                sinkId = b.NodeId;

                PipeWireGraphSnapshot ready = await WaitForAsync(
                    builder,
                    g => g.GetPortsForNode(sourceId).Length == 4 && g.GetPortsForNode(sinkId).Length == 4,
                    cts.Token);

                PipeWireLink link = await builder.CreateLink(
                        ready.GetPortsForNode(sourceId, PipeWirePortDirection.Out).OrderBy(p => p.PortId).First(),
                        ready.GetPortsForNode(sinkId, PipeWirePortDirection.In).OrderBy(p => p.PortId).First())
                    .WithLinger()
                    .ExecuteAsync(cts.Token);
                linkId = link.LinkId;

                await WaitForAsync(observer, g => g.GetLink(linkId) is not null, cts.Token);
                await builder.DisposeAsync();
            }

            // --- the app is gone. The routing must not be.
            PipeWireGraphSnapshot after = await WaitForAsync(
                observer,
                g => g.Nodes.All(n => n.NodeName != "pwnet-routing-builder"),
                cts.Token);
            await Task.Delay(250, cts.Token);
            after = observer.Current;

            Assert.IsNotNull(after.GetNode(sourceId), "a lingering source node must outlive its creator");
            Assert.IsNotNull(after.GetNode(sinkId), "a lingering sink node must outlive its creator");
            Assert.IsNotNull(after.GetLink(linkId), "a lingering link must outlive its creator");
            Assert.AreEqual(1, after.GetOutputLinksForPort(after.GetLink(linkId)!.LinkOutputPort).Length,
                "the surviving link must still be wired to its ports");

            // --- second "run": tear the setup down deliberately.
            await observer.DestroyGlobalAsync(linkId, cts.Token);
            await WaitForAsync(observer, g => g.GetLink(linkId) is null, cts.Token);
            await observer.DestroyGlobalAsync(sourceId, cts.Token);
            await observer.DestroyGlobalAsync(sinkId, cts.Token);

            PipeWireGraphSnapshot cleaned = await WaitForAsync(
                observer, g => g.GetNode(sourceId) is null && g.GetNode(sinkId) is null, cts.Token);

            Assert.AreEqual(0, cleaned.GetPortsForNode(sourceId).Length, "removed nodes must leave no ports behind");
            Assert.AreEqual(0, cleaned.GetPortsForNode(sinkId).Length);
        }
    }

    /// <summary>
    /// A routing setup is rebuilt on every start, so the full build/teardown cycle has to be
    /// repeatable without accumulating anything.
    /// </summary>
    [TestMethod]
    public async Task RoutingSetup_IsRebuildableWithoutAccumulating()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-rebuild", cts.Token);

        await using (context)
        await using (registry)
        {
            // Counted by name, not graph-wide. The suite shares one session and other classes are
            // creating and destroying nodes throughout, so a total that has not returned to its
            // starting value says nothing about whether this test cleaned up after itself.
            const string prefix = "pwnet_rb_";
            static int Ours(PipeWireGraphSnapshot graph) =>
                graph.Nodes.Count(n => n.NodeName?.StartsWith(prefix, StringComparison.Ordinal) == true);

            int nodesAtStart = Ours(registry.Current);
            int linksAtStart = registry.Current.Links.Length;

            for (int cycle = 0; cycle < 5; cycle++)
            {
                PipeWireNode a = await registry.CreateVirtualNodeAsync($"RB A{cycle}", $"{prefix}a{cycle}", cts.Token);
                PipeWireNode b = await registry.CreateVirtualNodeAsync($"RB B{cycle}", $"{prefix}b{cycle}", cts.Token);

                PipeWireGraphSnapshot ready = await WaitForAsync(
                    registry,
                    g => g.GetPortsForNode(a.NodeId).Length == 4 && g.GetPortsForNode(b.NodeId).Length == 4,
                    cts.Token);

                PipeWireLink link = await registry.CreateLinkAsync(
                    ready.GetPortsForNode(a.NodeId, PipeWirePortDirection.Out).OrderBy(p => p.PortId).First(),
                    ready.GetPortsForNode(b.NodeId, PipeWirePortDirection.In).OrderBy(p => p.PortId).First(),
                    cts.Token);

                await registry.DestroyGlobalAsync(link.LinkId, cts.Token);
                await registry.DestroyGlobalAsync(a.NodeId, cts.Token);
                await registry.DestroyGlobalAsync(b.NodeId, cts.Token);

                await WaitForAsync(
                    registry,
                    g => g.GetNode(a.NodeId) is null && g.GetNode(b.NodeId) is null && g.GetLink(link.LinkId) is null,
                    cts.Token);
            }

            PipeWireGraphSnapshot end = registry.Current;
            Assert.AreEqual(nodesAtStart, Ours(end),
                $"this test's nodes accumulated across five build/teardown cycles: "
                + $"{nodesAtStart} -> {Ours(end)}");

            // Links carry no name, so this one stays graph-wide and only has to not grow. The
            // cycles above each waited for their own link to disappear before the next began.
            Assert.IsTrue(end.Links.Length <= linksAtStart + 1,
                $"links accumulated across five build/teardown cycles: {linksAtStart} -> {end.Links.Length}");
        }
    }
}
