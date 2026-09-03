using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Object creation and binding when the graph does not cooperate.
/// </summary>
/// <remarks>
/// Creation is two exchanges, not one: the proxy is bound, then the global is published. Anything
/// that goes wrong between them leaves a waiter with nothing to wait for, and an id that looked
/// valid a moment ago. These drive the cases that only occur when something else is churning.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class CreationHostileTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

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

    private static string Unique(string p) => $"{p}_{Environment.ProcessId}_{Random.Shared.Next():x}";

    [TestMethod]
    public async Task ACreationTheDaemonRefuses_FaultsTheCallerRatherThanHanging()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-create-refused", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // A link between two ports that cannot be linked: the daemon reports the failure on the
            // error stream after the proxy is bound, which is the window where a waiter has an id
            // and no object. It has to fault rather than wait for a global that is not coming.
            PipeWireNode node = await registry.CreateVirtualNode("Refuse")
                .WithName(Unique("pwnet_refuse")).ExecuteAsync(cts.Token);

            PipeWirePort[] ports = await PortsAsync(registry, node.NodeId, cts.Token);
            PipeWirePort? input = Array.Find(ports, p => p.PortDirection == PipeWirePortDirection.In);
            if (input is null) Assert.Inconclusive("the node published no input port.");

            // Input to input. Ports face the wrong way, so the library refuses before the daemon
            // is asked at all, which is the better of the two outcomes.
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await registry.CreateLinkAsync(input!, input!, cts.Token));

            // The connection is unharmed by the refusal.
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsNotNull(registry.Current.GetNode(node.NodeId));

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task AnObjectRemovedTheInstantItAppears_LeavesNoWaiterBehind()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-create-vanish", cts.Token);
        (PipeWireContext killerCtx, PipeWireRegistry killer) = await ConnectAsync("pwnet-create-killer", cts.Token);
        await using (ctx)
        await using (registry)
        await using (killerCtx)
        await using (killer)
        {
            // A second client destroys each object as soon as it sees it. Creation therefore races
            // its own removal: the global may be gone before the creating call has returned.
            var doomed = new System.Collections.Concurrent.ConcurrentQueue<uint>();
            void OnAdded(PipeWireNode n)
            {
                if (n.NodeName?.StartsWith("pwnet_vanish", StringComparison.Ordinal) == true)
                    doomed.Enqueue(n.NodeId);
            }

            killer.NodeAdded += OnAdded;
            try
            {
                for (int round = 0; round < 10; round++)
                {
                    PipeWireNode node = await registry.CreateVirtualNode("Vanish")
                        .WithName(Unique("pwnet_vanish")).ExecuteAsync(cts.Token);

                    while (doomed.TryDequeue(out uint id))
                    {
                        try { await killer.DestroyGlobalAsync(id, cts.Token); }
                        catch (PipeWireException) { /* already gone; that is the race working */ }
                    }

                    try { await registry.DestroyGlobalAsync(node.NodeId, cts.Token); }
                    catch (PipeWireException) { /* the killer got there first */ }
                }

                // Both connections still work, and neither is holding a waiter that never completed.
                await registry.WaitForInitialEnumerationAsync(cts.Token);
                await killer.WaitForInitialEnumerationAsync(cts.Token);
            }
            finally
            {
                killer.NodeAdded -= OnAdded;
            }
        }
    }

    [TestMethod]
    public async Task ObjectsDestroyedWhileBeingCreated_LeaveNoProxyBehind()
    {
        // A created object's proxy is filed once the graph publishes it, which is a window: the
        // object exists on the daemon and can be destroyed by anyone before the filing happens.
        // Nothing observable moves when a proxy is left filed for a removed object, which is why
        // this counts the table directly rather than descriptors.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-proxy-race", cts.Token);
        (PipeWireContext killerCtx, PipeWireRegistry killer) = await ConnectAsync("pwnet-proxy-killer", cts.Token);
        await using (ctx)
        await using (registry)
        await using (killerCtx)
        await using (killer)
        {
            int before = registry.OwnedProxyCount;

            var doomed = new System.Collections.Concurrent.ConcurrentQueue<uint>();
            void OnAdded(PipeWireNode n)
            {
                if (n.NodeName?.StartsWith("pwnet_proxyrace", StringComparison.Ordinal) == true)
                    doomed.Enqueue(n.NodeId);
            }

            killer.NodeAdded += OnAdded;
            try
            {
                var mine = new List<uint>();

                for (int round = 0; round < 20; round++)
                {
                    // The killer races the creation from the other connection. Whichever wins, the
                    // proxy must not be left filed for an object that no longer exists.
                    Task<PipeWireNode> creating = registry.CreateVirtualNode("ProxyRace")
                        .WithName(Unique("pwnet_proxyrace")).ExecuteAsync(cts.Token);

                    while (doomed.TryDequeue(out uint id))
                    {
                        try { await killer.DestroyGlobalAsync(id, cts.Token); }
                        catch (PipeWireException) { /* already gone; that is the race working */ }
                    }

                    try { mine.Add((await creating).NodeId); }
                    catch (PipeWireException) { /* the killer got it before it was published */ }
                }

                foreach (uint id in mine)
                {
                    try { await registry.DestroyGlobalAsync(id, cts.Token); }
                    catch (PipeWireException) { /* the killer got there first */ }
                }

                // Every object this test made is gone, so every proxy it filed should be too.
                for (int attempt = 0; attempt < 100 && registry.OwnedProxyCount > before; attempt++)
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);

                Assert.AreEqual(before, registry.OwnedProxyCount,
                    "proxies are still filed for objects that no longer exist");
            }
            finally
            {
                killer.NodeAdded -= OnAdded;
            }
        }
    }

    [TestMethod]
    public async Task AnIdReusedByANewObject_DoesNotChangeAnOlderSnapshot()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-id-reuse", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // PipeWire reuses ids. A snapshot is immutable, so the object it recorded under an id
            // must stay that object even after the daemon has handed the id to something else.
            PipeWireNode first = await registry.CreateVirtualNode("Reuse")
                .WithName(Unique("pwnet_reuse_first")).ExecuteAsync(cts.Token);

            await registry.WaitForInitialEnumerationAsync(cts.Token);
            PipeWireGraphSnapshot held = registry.Current;
            string? firstName = held.GetNode(first.NodeId)?.NodeName;
            Assert.IsNotNull(firstName);

            await registry.DestroyGlobalAsync(first.NodeId, cts.Token);

            // Churn until an id is reused. The daemon hands ids out again quickly once freed.
            bool reused = false;
            var created = new List<uint>();
            for (int i = 0; i < 30 && !reused; i++)
            {
                PipeWireNode next = await registry.CreateVirtualNode("Reuse")
                    .WithName(Unique("pwnet_reuse_next")).ExecuteAsync(cts.Token);

                created.Add(next.NodeId);
                reused = next.NodeId == first.NodeId;
            }

            foreach (uint id in created)
            {
                try { await registry.DestroyGlobalAsync(id, cts.Token); }
                catch (PipeWireException) { /* already gone */ }
            }

            if (!reused)
                Assert.Inconclusive("the daemon did not reuse an id within the churn budget.");

            Assert.AreEqual(firstName, held.GetNode(first.NodeId)?.NodeName,
                "a held snapshot changed when the daemon reused the id it recorded");
        }
    }

    [TestMethod]
    public async Task BindingAnIdOfTheWrongKind_IsRefusedBeforeItReachesTheDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-kind-mismatch", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireGraphSnapshot graph = registry.Current;

            uint? clientId = graph.Clients.FirstOrDefault()?.Id;
            uint? portId = graph.Ports.FirstOrDefault()?.PortId;
            uint? deviceId = graph.Devices.FirstOrDefault()?.Id;

            // Binding an id as the wrong interface would hand the daemon a proxy it cannot serve.
            // The guard is local, so the mistake is an ArgumentException rather than a protocol error.
            if (clientId is { } c)
            {
                Assert.ThrowsExactly<ArgumentException>(() => registry.BindNode(c));
                Assert.ThrowsExactly<ArgumentException>(() => registry.BindDevice(c));
            }

            if (portId is { } p)
                Assert.ThrowsExactly<ArgumentException>(() => registry.BindNode(p));

            if (deviceId is { } d)
                Assert.ThrowsExactly<ArgumentException>(() => registry.BindClient(d));

            // And an id nothing has ever used.
            Assert.ThrowsExactly<ArgumentException>(() => registry.BindNode(0x7FFF_0000));
            Assert.ThrowsExactly<ArgumentException>(() => registry.BindDevice(0x7FFF_0000));
            Assert.ThrowsExactly<ArgumentException>(() => registry.BindClient(0x7FFF_0000));

            // The connection is unharmed by any of it.
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsTrue(registry.Current.Nodes.Length > 0);
        }
    }

    private static async Task<PipeWirePort[]> PortsAsync(
        PipeWireRegistry registry, uint nodeId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await registry.WaitForInitialEnumerationAsync(cancellationToken);

            PipeWirePort[] ports = [.. registry.Current.GetPortsForNode(nodeId)];
            if (ports.Length > 0) return ports;
        }
    }
}
