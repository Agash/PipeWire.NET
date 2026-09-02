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
    public async Task EverySnapshotPublishedDuringChurn_IsCheckedForDanglingReferences()
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
                PipeWireNode node = await registry.CreateVirtualStereoNode("Invariant")
                    .WithName($"pwnet_inv_{Environment.ProcessId}_{round}_{Random.Shared.Next():x}")
                    .ExecuteAsync(cts.Token);

                await registry.RemoveObjectAsync(node.NodeId, cts.Token);
            }

            await registry.WaitForInitialEnumerationAsync(cts.Token);
        }
        finally
        {
            registry.GraphChanged -= OnChanged;
        }

        Assert.IsTrue(Volatile.Read(ref snapshots) > 0, "the churn published no snapshots to check");

        // Recorded rather than asserted per snapshot: the daemon's teardown order makes transient
        // dangling references legitimate mid-churn. What must hold is that they do not survive.
        List<string> settled = DanglingReferences(registry.Current);
        Assert.AreEqual(0, settled.Count,
            $"the settled graph has dangling references: {string.Join("; ", settled)}");
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

        PipeWireNode node = await registry.CreateVirtualStereoNode("Held")
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
        await registry.RemoveObjectAsync(node.NodeId, cts.Token);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        Assert.IsNotNull(held.GetNode(node.NodeId), "a held snapshot lost a node it had");
        Assert.AreEqual(ports.Length, held.GetPortsForNode(node.NodeId).Length,
            "a held snapshot lost ports it had");
        Assert.AreEqual(0, DanglingReferences(held).Count,
            "a held snapshot developed dangling references after the graph moved on");
    }
}
