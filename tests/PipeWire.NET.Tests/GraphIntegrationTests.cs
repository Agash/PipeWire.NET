using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Graph discovery and mutation against a running daemon: creating a virtual sink, linking its
/// ports, removing the link, and the ordering between events and <see cref="PipeWireRegistry.Current"/>.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class GraphIntegrationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<(PipeWireContext Context, PipeWireRegistry Registry)> ConnectAsync(
        CancellationToken cancellationToken)
    {
        var context = new PipeWireContext("pwnet-graph-tests", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cancellationToken);
        var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cancellationToken);
        return (context, registry);
    }

    /// <summary>Waits for a predicate to hold on a published snapshot.</summary>
    private static async Task<PipeWireGraphSnapshot> WaitForAsync(
        PipeWireRegistry registry,
        Func<PipeWireGraphSnapshot, bool> until,
        CancellationToken cancellationToken)
    {
        await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(cancellationToken))
            if (until(graph))
                return graph;

        throw new InvalidOperationException("the snapshot stream ended before the condition held");
    }

    private static Task<PipeWireGraphSnapshot> WaitForPortsAsync(
        PipeWireRegistry registry, uint nodeId, CancellationToken cancellationToken) =>
        WaitForAsync(registry, g => g.GetPortsForNode(nodeId).Length == 4, cancellationToken);

    [TestMethod]
    public async Task InitialEnumeration_ReportsTheGraphWithoutASettleDelay()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync(cts.Token);
        await using (context)
        await using (registry)
        {
            // The sync barrier means the graph is already populated when the wait returns; the old
            // 250ms settle delay was the only thing that used to make this true.
            Assert.IsTrue(registry.Current.Nodes.Length > 0, "a running daemon always has nodes");
            Assert.IsTrue(registry.Current.Version > 0, "a snapshot should have been published");
        }
    }

    [TestMethod]
    public async Task CreateVirtualStereoNode_AppearsInTheGraphWithFourPorts()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync(cts.Token);
        await using (context)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualStereoNodeAsync(
                "PipeWire.NET test sink", "pwnet_test_sink", cts.Token);

            Assert.IsNotNull(registry.Current.GetNode(node.NodeId),
                "the node must be in the graph by the time the call returns");

            PipeWireGraphSnapshot graph = await WaitForPortsAsync(registry, node.NodeId, cts.Token);

            Assert.AreEqual(2, graph.GetPortsForNode(node.NodeId, PipeWirePortDirection.In).Count());
            Assert.AreEqual(2, graph.GetPortsForNode(node.NodeId, PipeWirePortDirection.Out).Count());
        }
    }

    [TestMethod]
    public async Task CreateLink_ThenRemove_IsVisibleFromBothPorts()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync(cts.Token);
        await using (context)
        await using (registry)
        {
            PipeWireNode a = await registry.CreateVirtualStereoNodeAsync("A", "pwnet_link_a", cts.Token);
            PipeWireNode b = await registry.CreateVirtualStereoNodeAsync("B", "pwnet_link_b", cts.Token);

            PipeWireGraphSnapshot ready = await WaitForAsync(
                registry,
                g => g.GetPortsForNode(a.NodeId).Length == 4 && g.GetPortsForNode(b.NodeId).Length == 4,
                cts.Token);

            PipeWirePort output = ready.GetPortsForNode(a.NodeId, PipeWirePortDirection.Out).First();
            PipeWirePort input = ready.GetPortsForNode(b.NodeId, PipeWirePortDirection.In).First();

            PipeWireLink link = await registry.CreateLinkAsync(output, input, cts.Token);

            PipeWireGraphSnapshot linked = registry.Current;
            Assert.IsNotNull(linked.GetLink(link.LinkId));
            Assert.AreEqual(1, linked.GetOutputLinksForPort(output.PortId).Length,
                "the link must be reachable from the output port");
            Assert.AreEqual(1, linked.GetInputLinksForPort(input.PortId).Length,
                "and from the input port");
            Assert.AreEqual(0, linked.GetInputLinksForPort(output.PortId).Length,
                "but not as an input of the output port");

            await registry.RemoveLinkAsync(link, cts.Token);
            await WaitForAsync(registry, g => g.GetLink(link.LinkId) is null, cts.Token);
        }
    }

    [TestMethod]
    public async Task CreateLink_RejectsPortsFacingTheWrongWay()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync(cts.Token);
        await using (context)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualStereoNodeAsync("R", "pwnet_reject", cts.Token);
            PipeWireGraphSnapshot graph = await WaitForPortsAsync(registry, node.NodeId, cts.Token);

            PipeWirePort output = graph.GetPortsForNode(node.NodeId, PipeWirePortDirection.Out).First();
            PipeWirePort input = graph.GetPortsForNode(node.NodeId, PipeWirePortDirection.In).First();

            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => registry.CreateLinkAsync(input, output, cts.Token));
        }
    }

    [TestMethod]
    public async Task GranularEvents_NeverPrecedeTheSnapshotTheyDescribe()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync(cts.Token);
        await using (context)
        await using (registry)
        {
            var violations = new List<string>();
            registry.PortAdded += p =>
            {
                if (registry.Current.GetPort(p.PortId) is null)
                    violations.Add($"port {p.PortId}");
            };
            registry.SourceAdded += n =>
            {
                if (registry.Current.GetNode(n.NodeId) is null)
                    violations.Add($"node {n.NodeId}");
            };

            PipeWireNode node = await registry.CreateVirtualStereoNodeAsync("O", "pwnet_order", cts.Token);
            await WaitForPortsAsync(registry, node.NodeId, cts.Token);

            CollectionAssert.AreEqual(Array.Empty<string>(), violations,
                "Current must already contain whatever an event announces");
        }
    }

    [TestMethod]
    public async Task CreateVirtualStereoNode_HonoursCancellation()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync(cts.Token);
        await using (context)
        await using (registry)
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => registry.CreateVirtualStereoNodeAsync("C", "pwnet_cancel", cancelled.Token));
        }
    }

    [TestMethod]
    public async Task PublishedSnapshots_DoNotChangeUnderTheirHolder()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync(cts.Token);
        await using (context)
        await using (registry)
        {
            PipeWireGraphSnapshot before = registry.Current;
            int nodesBefore = before.Nodes.Length;

            await registry.CreateVirtualStereoNodeAsync("I", "pwnet_immutable", cts.Token);

            Assert.AreEqual(nodesBefore, before.Nodes.Length, "an already-published snapshot must not change");
            Assert.IsTrue(registry.Current.Version > before.Version, "a new snapshot must have been published");
        }
    }
}
