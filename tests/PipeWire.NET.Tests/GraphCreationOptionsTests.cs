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

            PipeWireNodeCreation pending = registry.CreateVirtualStereoNode("Inert")
                                                   .WithName("pwnet_inert")
                                                   .WithLinger();

            Assert.AreEqual(before, registry.Current.Version,
                "describing a node must not touch the daemon");

            PipeWireNode node = await pending.ExecuteAsync(cts.Token);
            Assert.IsNotNull(registry.Current.GetNode(node.NodeId));

            // Created with linger, so it outlives this client - remove it explicitly.
            await registry.RemoveObjectAsync(node.NodeId, cts.Token);
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
                lingering = (await owner.CreateVirtualStereoNode("Lingering")
                                        .WithName("pwnet_lingering")
                                        .WithLinger()
                                        .ExecuteAsync(cts.Token)).NodeId;

                transient = (await owner.CreateVirtualStereoNode("Transient")
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

            await observer.RemoveObjectAsync(lingering, cts.Token);
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
            PipeWireNode a = await registry.CreateVirtualStereoNodeAsync("PA", "pwnet_pa", cts.Token);
            PipeWireNode b = await registry.CreateVirtualStereoNodeAsync("PB", "pwnet_pb", cts.Token);

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
            PipeWireNode node = await registry.CreateVirtualStereoNodeAsync("V", "pwnet_version", cts.Token);
            PipeWireGraphSnapshot graph = await WaitForAsync(
                registry, g => g.GetPortsForNode(node.NodeId).Length == 4, cts.Token);

            Assert.IsTrue(graph.GetNode(node.NodeId)!.Version > 0,
                "the registry must record the version from the global event");

            foreach (PipeWirePort port in graph.GetPortsForNode(node.NodeId))
                Assert.IsTrue(port.Version > 0, $"port {port.PortId} has no version");
        }
    }
}
