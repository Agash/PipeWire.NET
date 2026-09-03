using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// What a published snapshot promises about itself, checked while the graph is changing.
/// </summary>
/// <remarks>
/// A snapshot is built from a stream of independent global events, and PipeWire removes a node
/// before the ports and links that referred to it. Any window where a snapshot has a port whose
/// node is gone is a window where a consumer resolving that reference gets null - so rather than
/// asserting one shape at rest, these check every snapshot published during churn.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class GraphInvariantTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    /// <summary>Every dangling reference in one snapshot, as readable text.</summary>
    private static List<string> DanglingReferences(PipeWireGraphSnapshot g)
    {
        var broken = new List<string>();

        foreach (PipeWirePort port in g.Ports)
        {
            if (g.GetNode(port.NodeId) is null)
                broken.Add($"port {port.PortId} refers to absent node {port.NodeId}");
        }

        foreach (PipeWireLink link in g.Links)
        {
            if (g.GetNode(link.LinkInputNode) is null)
                broken.Add($"link {link.LinkId} refers to absent input node {link.LinkInputNode}");
            if (g.GetNode(link.LinkOutputNode) is null)
                broken.Add($"link {link.LinkId} refers to absent output node {link.LinkOutputNode}");
        }

        return broken;
    }

    [TestMethod]
    public async Task TheRegistryReportsEachGlobalOnce_EvenWhileTheObjectChanges()
    {
        // Why there is no registry-wide NodeChanged. pw_registry_events has exactly two entries,
        // global and global_remove, and the header describes global as notifying of a NEW object.
        // Property and info changes travel on a bound proxy's info event instead, so an aggregate
        // "something changed" event would mean holding a proxy for every object in the graph. A
        // patchbay watching a few hundred nodes would pay a proxy each to learn about the one it
        // cares about, so change notification is opt-in per object through BindNode and
        // InfoChanged. This pins the assumption that costs.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-global-once", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);
        {
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<uint, int>();
            void OnAdded(PipeWireNode n) => seen.AddOrUpdate(n.NodeId, 1, static (_, c) => c + 1);

            registry.NodeAdded += OnAdded;
            try
            {
                PipeWireNode source = await registry.CreateVirtualNode("GlobalOnce")
                    .WithName($"pwnet_once_src_{Environment.ProcessId}_{Random.Shared.Next():x}").ExecuteAsync(cts.Token);
                PipeWireNode sink = await registry.CreateVirtualNode("GlobalOnce")
                    .WithName($"pwnet_once_sink_{Environment.ProcessId}_{Random.Shared.Next():x}").ExecuteAsync(cts.Token);

                // Things that change a node without creating or destroying it: linking it, which
                // moves it out of idle, and writing a param, which the daemon applies to it.
                await using (PipeWireNodeControl control = registry.BindNode(source.NodeId))
                {
                    await control.ReadyAsync(cts.Token);
                    await control.SetVolumeAsync(0.4f, cts.Token);
                }

                PipeWirePort[] outs = await PortsAsync(source.NodeId, PipeWirePortDirection.Out);
                PipeWirePort[] ins = await PortsAsync(sink.NodeId, PipeWirePortDirection.In);

                async Task<PipeWirePort[]> PortsAsync(uint nodeId, PipeWirePortDirection direction)
                {
                    while (true)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        await registry.WaitForInitialEnumerationAsync(cts.Token);

                        PipeWirePort[] found =
                            [.. registry.Current.GetPortsForNode(nodeId).Where(p => p.PortDirection == direction)];
                        if (found.Length > 0) return found;
                    }
                }
                PipeWireLink link = await registry.CreateLinkAsync(outs[0], ins[0], cts.Token);

                await registry.WaitForInitialEnumerationAsync(cts.Token);

                Assert.AreEqual(1, seen.GetValueOrDefault(source.NodeId),
                    "the registry announced a node more than once, so it does carry updates and "
                    + "the graph could raise a change event without binding anything");

                await registry.DestroyGlobalAsync(link.LinkId, cts.Token);
                await registry.DestroyGlobalAsync(source.NodeId, cts.Token);
                await registry.DestroyGlobalAsync(sink.NodeId, cts.Token);
            }
            finally
            {
                registry.NodeAdded -= OnAdded;
            }
        }
    }

    [TestMethod]
    public async Task DanglingReferencesSeenDuringChurn_DoNotSurviveIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-invariant", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        var observed = new ConcurrentQueue<(long Version, List<string> Broken)>();
        int snapshots = 0;

        void OnChanged(PipeWireRegistry _, PipeWireGraphSnapshot g)
        {
            Interlocked.Increment(ref snapshots);
            List<string> broken = DanglingReferences(g);
            if (broken.Count > 0) observed.Enqueue((g.Version, broken));
        }

        registry.GraphChanged += OnChanged;
        try
        {
            for (int round = 0; round < 25; round++)
            {
                PipeWireNode node = await registry.CreateVirtualNode("Invariant")
                    .WithName($"pwnet_inv_{Environment.ProcessId}_{round}_{Random.Shared.Next():x}")
                    .ExecuteAsync(cts.Token);

                await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
            }

            await registry.WaitForInitialEnumerationAsync(cts.Token);
        }
        finally
        {
            registry.GraphChanged -= OnChanged;
        }

        Assert.IsTrue(Volatile.Read(ref snapshots) > 0, "the churn published no snapshots to check");

        // Not asserted per snapshot: a node and its ports are removed by separate global_remove
        // events, so a snapshot published between them legitimately shows ports whose node is
        // already gone. What must hold is that none of it survives the churn. The mid-churn
        // observations are carried into the failure message, because "the settled graph has a
        // dangling port" is only diagnosable alongside when that reference first appeared.
        List<string> settled = DanglingReferences(registry.Current);
        Assert.AreEqual(0, settled.Count,
            $"the settled graph has dangling references: {string.Join("; ", settled)}. "
            + $"{observed.Count} of {Volatile.Read(ref snapshots)} snapshots dangled mid-churn"
            + (observed.TryPeek(out (long Version, List<string> Broken) first)
                ? $", first at version {first.Version}: {string.Join("; ", first.Broken)}"
                : string.Empty));
    }

    [TestMethod]
    public async Task ASnapshotHeldAcrossChurn_KeepsEveryReferenceItCouldResolveWhenTaken()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-invariant-held", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode node = await registry.CreateVirtualNode("Held")
            .WithName($"pwnet_held_{Environment.ProcessId}_{Random.Shared.Next():x}")
            .ExecuteAsync(cts.Token);

        // Wait for the node's ports, so the snapshot has references worth resolving.
        while (registry.Current.GetPortsForNode(node.NodeId).Length < 4)
        {
            cts.Token.ThrowIfCancellationRequested();
            await registry.WaitForInitialEnumerationAsync(cts.Token);
        }

        PipeWireGraphSnapshot held = registry.Current;
        ImmutableArray<PipeWirePort> ports = held.GetPortsForNode(node.NodeId);
        Assert.IsTrue(ports.Length >= 4);

        // The object it described goes away underneath it. A snapshot is immutable, so everything
        // it could answer before must still answer the same afterwards.
        await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        Assert.IsNotNull(held.GetNode(node.NodeId), "a held snapshot lost a node it had");
        Assert.AreEqual(ports.Length, held.GetPortsForNode(node.NodeId).Length,
            "a held snapshot lost ports it had");
        Assert.AreEqual(0, DanglingReferences(held).Count,
            "a held snapshot developed dangling references after the graph moved on");
    }
}
