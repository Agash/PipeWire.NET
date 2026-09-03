using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Lifetime behaviour that only a second connection can observe: what the daemon does to a node's
/// ports when the node goes away, and that tearing down the creating client destroys its proxies
/// exactly once.
/// </summary>
/// <remarks>
/// Objects created without <c>object.linger</c> die with the client that made them, so disposing
/// one registry is how a test removes a node without a public destroy-any-global API.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class GraphLifetimeTests
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
        PipeWireRegistry registry,
        Func<PipeWireGraphSnapshot, bool> until,
        CancellationToken cancellationToken)
    {
        await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(cancellationToken))
            if (until(graph))
                return graph;

        throw new InvalidOperationException("the snapshot stream ended before the condition held");
    }

    [TestMethod]
    public async Task WhenANodeGoesAway_ItsPortsLeaveTheGraphToo()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // The observer outlives the owner, so it sees the whole removal sequence.
        (PipeWireContext observerContext, PipeWireRegistry observer) =
            await ConnectAsync("pwnet-observer", cts.Token);

        await using (observerContext)
        await using (observer)
        {
            uint nodeId;
            uint[] portIds;

            (PipeWireContext ownerContext, PipeWireRegistry owner) =
                await ConnectAsync("pwnet-owner", cts.Token);
            await using (ownerContext)
            {
                PipeWireNode node = await owner.CreateVirtualNodeAsync(
                    "Cascade", "pwnet_cascade", cts.Token);
                nodeId = node.NodeId;

                PipeWireGraphSnapshot seen = await WaitForAsync(
                    observer, g => g.GetPortsForNode(nodeId).Length == 4, cts.Token);
                portIds = [.. seen.GetPortsForNode(nodeId).Select(p => p.PortId)];

                await owner.DisposeAsync();
            }

            PipeWireGraphSnapshot after = await WaitForAsync(
                observer, g => g.GetNode(nodeId) is null, cts.Token);

            Assert.AreEqual(0, after.GetPortsForNode(nodeId).Length,
                "the node's ports must not outlive it in the adjacency index");
            foreach (uint portId in portIds)
                Assert.IsNull(after.GetPort(portId), $"port {portId} is still in the graph");
        }
    }

    [TestMethod]
    public async Task DisposingTheOwningRegistryTwice_DestroysEachProxyOnce()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-dispose", cts.Token);

        await using (context)
        {
            PipeWireNode node = await registry.CreateVirtualNodeAsync(
                "D", "pwnet_dispose_node", cts.Token);

            // A second pw_proxy_destroy trips an assertion in PipeWire and aborts the process, so
            // surviving this is necessary - but survival alone would also hold if disposal did
            // nothing, so a second connection has to confirm the node actually went away.
            await registry.DisposeAsync();
            await registry.DisposeAsync();

            (PipeWireContext watcherContext, PipeWireRegistry watcher) =
                await ConnectAsync("pwnet-dispose-watch", cts.Token);
            await using (watcherContext)
            await using (watcher)
            {
                PipeWireGraphSnapshot after = await WaitForAsync(
                    watcher, g => g.GetNode(node.NodeId) is null, cts.Token);
                Assert.IsNull(after.GetNode(node.NodeId),
                    "disposing the owner must destroy the node it created");
            }
        }
    }

    [TestMethod]
    public async Task ADestroyedNodesProxyIsReleasedByTheRemovalPath()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-release", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode a = await registry.CreateVirtualNodeAsync("L1", "pwnet_rel_a", cts.Token);
            PipeWireNode b = await registry.CreateVirtualNodeAsync("L2", "pwnet_rel_b", cts.Token);

            PipeWireGraphSnapshot ready = await WaitForAsync(
                registry,
                g => g.GetPortsForNode(a.NodeId).Length == 4 && g.GetPortsForNode(b.NodeId).Length == 4,
                cts.Token);

            PipeWireLink link = await registry.CreateLinkAsync(
                ready.GetPortsForNode(a.NodeId, PipeWirePortDirection.Out).First(),
                ready.GetPortsForNode(b.NodeId, PipeWirePortDirection.In).First(),
                cts.Token);

            // Removal arrives as global_remove, which hands the proxy to the handle; disposal then
            // must not destroy it a second time.
            Assert.IsNotNull(registry.Current.GetLink(link.LinkId), "the link must exist before removal");

            await registry.RemoveLinkAsync(link, cts.Token);
            PipeWireGraphSnapshot after = await WaitForAsync(
                registry, g => g.GetLink(link.LinkId) is null, cts.Token);

            Assert.IsNull(after.GetLink(link.LinkId));
            Assert.AreEqual(0, after.GetOutputLinksForPort(link.LinkOutputPort).Length,
                "the removed link must leave the adjacency index too");
        }
    }

    [TestMethod]
    public async Task WatchAsync_NeverGoesBackwardsAndEndsAtTheCurrentGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-watch", cts.Token);

        await using (context)
        await using (registry)
        {
            var seen = new List<long>();
            PipeWireNode node = await registry.CreateVirtualNodeAsync("W", "pwnet_watch", cts.Token);

            await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(cts.Token))
            {
                seen.Add(graph.Version);

                // A dropped snapshot is allowed; an out-of-order one is not.
                if (seen.Count > 1)
                    Assert.IsTrue(seen[^1] > seen[^2],
                        $"version went {seen[^2]} -> {seen[^1]}; the stream must only move forward");

                if (graph.GetPortsForNode(node.NodeId).Length == 4)
                    break;
            }

            Assert.IsTrue(seen.Count > 0);
        }
    }

    [TestMethod]
    public async Task ACreatedNodeCarriesUsablePermissions()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-perms", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNodeAsync("P", "pwnet_perms", cts.Token);

            Assert.AreNotEqual(PipeWirePermissions.None, node.Permissions,
                "the registry must decode the permission bits the daemon reports");
            Assert.IsTrue(node.Permissions.HasFlag(PipeWirePermissions.Read));
            Assert.IsTrue(node.CanInvokeMethods, "a node this client created is its own to drive");

            PipeWireGraphSnapshot graph = await WaitForAsync(
                registry, g => g.GetPortsForNode(node.NodeId).Length == 4, cts.Token);

            foreach (PipeWirePort port in graph.GetPortsForNode(node.NodeId))
                Assert.IsTrue(port.Permissions.HasFlag(PipeWirePermissions.Read),
                    $"port {port.PortId} came back unreadable");
        }
    }
}
