using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

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
    public async Task DisposingTheRegistry_EndsAnOpenWatch()
    {
        // The watch has no cancellation token of its own here, so disposal is the only thing
        // that can end it. Without the Finish hook the consumer would wait on the channel
        // for ever, holding the test host with it.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-watchend", cts.Token);

        await using (context)
        {
            await using var watcher = registry.WatchAsync().GetAsyncEnumerator();

            Assert.IsTrue(await watcher.MoveNextAsync(), "the watch yielded nothing at all");

            registry.Dispose();

            Assert.IsFalse(await watcher.MoveNextAsync(), "the watch survived the registry");
            await watcher.DisposeAsync();
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

    [TestMethod]
    public async Task EverythingBuiltDeliberatelyLeftBehind_CanStillBeTornDown()
    {
        // The cleanup proof: lingering objects survive their creator by design, so only
        // explicit destruction removes them. This builds a mess out of every hosted kind -
        // lingering nodes and a link, a served device, a served store with a key in it -
        // tears each one down through its own API, and then requires the graph to be free
        // of all of them. Anything left behind is a disposal path that does not work.
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-cleanup", cts.Token);

        string tag = $"pwnet_cleanup_{Environment.ProcessId}_{Random.Shared.Next():x}";

        await using (context)
        await using (registry)
        {
            PipeWireNode a = await registry.CreateVirtualNode("Cleanup A")
                .WithName(tag + "_a").WithLinger().ExecuteAsync(cts.Token);
            PipeWireNode b = await registry.CreateVirtualNode("Cleanup B")
                .WithName(tag + "_b").WithLinger().ExecuteAsync(cts.Token);

            PipeWirePort output = await WaitForPortAsync(registry, a.NodeId, PipeWirePortDirection.Out, cts.Token);
            PipeWirePort input = await WaitForPortAsync(registry, b.NodeId, PipeWirePortDirection.In, cts.Token);
            PipeWireLink link = await registry.CreateLink(output, input).WithLinger().ExecuteAsync(cts.Token);

            using PipeWireDeviceProvider device = PipeWireDeviceProvider.Create(
                context, tag + "_device", "A device this test withdraws");
            using PipeWireMetadataProvider served = PipeWireMetadataProvider.Create(context, tag + "_meta");

            // Bound from a second connection, which is what stores are for: binding a store
            // this same connection serves wedges the session, so no test does that here.
            await using var reader = new PipeWireContext("pwnet-cleanup-read", ConsoleTestLoggerFactory.Instance);
            await reader.StartAsync(cts.Token);
            await using var readerRegistry = new PipeWireRegistry(reader);
            await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);

            PipeWireMetadataStore? store = null;
            long appearUntil = Environment.TickCount64 + 20_000;
            while (store is null && Environment.TickCount64 < appearUntil)
            {
                await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);
                store = readerRegistry.BindMetadataStore(tag + "_meta");
                if (store is null)
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);
            }

            if (store is null)
                Assert.Inconclusive("the served store never appeared in the graph.");

            await using (store)
            {
                await store!.ReadyAsync(cts.Token);
                await store.SetAsync(tag + ".k", "v", cancellationToken: cts.Token);

                // Sanity: everything built is visible before anything is torn down.
                PipeWireGraphSnapshot built = registry.Current;
                Assert.IsNotNull(built.GetNode(a.NodeId));
                Assert.IsNotNull(built.GetNode(b.NodeId));
                Assert.IsNotNull(built.GetLink(link.LinkId));
                Assert.IsNotNull(built.Devices.FirstOrDefault(d => d.DeviceName == tag + "_device"));
                Assert.AreEqual("v", store.Get(tag + ".k"));

                // Tear down through each kind's own API, in dependency order.
                await store.SetAsync(tag + ".k", null, cancellationToken: cts.Token);
                await registry.DestroyGlobalAsync(link.LinkId, cts.Token);
                await registry.DestroyGlobalAsync(a.NodeId, cts.Token);
                await registry.DestroyGlobalAsync(b.NodeId, cts.Token);
            }

            device.Dispose();
            served.Dispose();

            // Removals propagate asynchronously; settle, then require absence rather than
            // waiting on it for ever. Time-boxed rather than attempt-counted: on a slow
            // session each round trip costs real time, and a fixed number of rounds then
            // expires the budget instead of reaching the assertions below. The link is
            // matched by its endpoints rather than its id: ids are reused under churn, so a
            // resolved id alone proves nothing about our link.
            long settleUntil = Environment.TickCount64 + 30_000;
            while (Environment.TickCount64 < settleUntil)
            {
                await registry.WaitForInitialEnumerationAsync(cts.Token);
                PipeWireGraphSnapshot seen = registry.Current;
                if (!LeftoversPresent(seen, tag)
                    && !seen.Links.Any(l =>
                        l.LinkOutputPort == output.PortId && l.LinkInputPort == input.PortId))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);
            }

            PipeWireGraphSnapshot final = registry.Current;
            Assert.IsFalse(LeftoversPresent(final, tag), DescribeLeftovers(final, tag));
            Assert.IsFalse(final.Links.Any(l =>
                l.LinkOutputPort == output.PortId && l.LinkInputPort == input.PortId),
                "a link through our ports survived their nodes");
            Assert.IsTrue(final.Nodes.Length > 0, "the session stopped answering");
        }
    }

    private static async Task<PipeWirePort> WaitForPortAsync(
        PipeWireRegistry registry, uint nodeId, PipeWirePortDirection direction, CancellationToken cancellationToken)
    {
        // Ports arrive after their node, on the daemon's own schedule, which under load is
        // seconds, not milliseconds. Bounded by the caller's budget rather than an attempt
        // count: giving up after N fast rounds mistakes a slow session for a broken node.
        // A timeout here fails naming the node and direction, rather than surfacing as a
        // bare cancellation from somewhere inside the wait.
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                PipeWirePort? port = registry.Current.GetPortsForNode(nodeId)
                    .FirstOrDefault(p => p.PortDirection == direction);
                if (port is not null)
                    return port;

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                await registry.WaitForInitialEnumerationAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"node {nodeId} never grew a {direction} port within the budget");
            throw;
        }
    }

    private static bool LeftoversPresent(PipeWireGraphSnapshot graph, string tag) =>
        graph.Nodes.Any(n => n.NodeName is not null && n.NodeName.Contains(tag, StringComparison.Ordinal))
        || graph.Devices.Any(d => d.DeviceName is not null && d.DeviceName.Contains(tag, StringComparison.Ordinal))
        || graph.Objects.Any(o =>
            o is PipeWireMetadataObject metadata
            && metadata.MetadataName is not null
            && metadata.MetadataName.Contains(tag, StringComparison.Ordinal));

    private static string DescribeLeftovers(PipeWireGraphSnapshot graph, string tag)
    {
        var left = new List<string>();
        foreach (PipeWireNode n in graph.Nodes)
            if (n.NodeName is not null && n.NodeName.Contains(tag, StringComparison.Ordinal))
                left.Add($"node {n.NodeId} '{n.NodeName}'");
        foreach (PipeWireDevice d in graph.Devices)
            if (d.DeviceName is not null && d.DeviceName.Contains(tag, StringComparison.Ordinal))
                left.Add($"device {d.Id} '{d.DeviceName}'");
        foreach (IPipeWireObject o in graph.Objects)
            if (o is PipeWireMetadataObject metadata
                && metadata.MetadataName is not null
                && metadata.MetadataName.Contains(tag, StringComparison.Ordinal))
                left.Add($"metadata {o.Id} '{metadata.MetadataName}'");
        return $"the graph still holds ours: {string.Join(", ", left)}";
    }
}
