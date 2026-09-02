using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// The ordering guarantee everything else in the library rests on.
/// </summary>
/// <remarks>
/// PipeWire processes a client's requests and the events they produce in order, so a round-trip is
/// a barrier: once its <c>done</c> arrives, every event caused by anything issued before it has
/// already been dispatched. That is what lets a caller do work and then read <see
/// cref="PipeWireRegistry.Current"/> instead of polling for the result to show up.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class CoreSyncOrderingTests
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

    private static string Unique(string prefix) =>
        $"{prefix}_{Environment.ProcessId}_{Random.Shared.Next():x}";

    [TestMethod]
    public async Task AfterARoundTrip_EveryObjectCreatedBeforeItIsInTheGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-order-create", cts.Token);
        await using (ctx)
        await using (registry)
        {
            var created = new List<uint>();
            for (int i = 0; i < 12; i++)
            {
                PipeWireNode node = await registry.CreateVirtualStereoNode("Ordering")
                    .WithName(Unique("pwnet_order"))
                    .ExecuteAsync(cts.Token);

                created.Add(node.NodeId);
            }

            // One barrier, no polling. Every creation happened before it, so every node has to be
            // visible the moment it returns.
            await registry.WaitForInitialEnumerationAsync(cts.Token);

            PipeWireGraphSnapshot graph = registry.Current;
            foreach (uint id in created)
                Assert.IsNotNull(graph.GetNode(id), $"node {id} was created before the barrier but is absent after it");

            foreach (uint id in created)
                await registry.RemoveObjectAsync(id, cts.Token);

            await registry.WaitForInitialEnumerationAsync(cts.Token);

            PipeWireGraphSnapshot after = registry.Current;
            foreach (uint id in created)
                Assert.IsNull(after.GetNode(id), $"node {id} was removed before the barrier but is present after it");
        }
    }

    [TestMethod]
    public async Task AfterARoundTrip_ALinkAndItsPortsAreAllVisibleTogether()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-order-link", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode source = await registry.CreateVirtualStereoNode("OrderSrc")
                .WithName(Unique("pwnet_order_src")).ExecuteAsync(cts.Token);
            PipeWireNode sink = await registry.CreateVirtualStereoNode("OrderSink")
                .WithName(Unique("pwnet_order_sink")).ExecuteAsync(cts.Token);

            ImmutableArray<PipeWirePort> outputs = await PortsAsync(
                registry, source.NodeId, PipeWirePortDirection.Out, cts.Token);
            ImmutableArray<PipeWirePort> inputs = await PortsAsync(
                registry, sink.NodeId, PipeWirePortDirection.In, cts.Token);

            PipeWireLink link = await registry.CreateLinkAsync(outputs[0], inputs[0], cts.Token);
            await registry.WaitForInitialEnumerationAsync(cts.Token);

            // The link and both endpoints were all caused before the barrier, so the graph must be
            // able to resolve the whole path - not the link alone.
            PipeWireGraphSnapshot graph = registry.Current;
            Assert.IsNotNull(graph.GetLink(link.LinkId));
            Assert.IsNotNull(graph.GetNode(link.LinkOutputNode));
            Assert.IsNotNull(graph.GetNode(link.LinkInputNode));
            Assert.IsTrue(graph.GetLinksForNode(source.NodeId).Any(l => l.LinkId == link.LinkId),
                "the link is not reachable from the node it starts at");

            await registry.RemoveObjectAsync(link.LinkId, cts.Token);
            await registry.RemoveObjectAsync(source.NodeId, cts.Token);
            await registry.RemoveObjectAsync(sink.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task AfterARoundTrip_AMetadataWriteIsVisibleToASecondClient()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext a, PipeWireRegistry ra) = await ConnectAsync("pwnet-order-meta-a", cts.Token);
        (PipeWireContext b, PipeWireRegistry rb) = await ConnectAsync("pwnet-order-meta-b", cts.Token);
        await using (a)
        await using (ra)
        await using (b)
        await using (rb)
        {
            PipeWireMetadataStore? writer = ra.BindMetadataStore("default");
            PipeWireMetadataStore? reader = rb.BindMetadataStore("default");
            if (writer is null || reader is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (writer)
            await using (reader)
            {
                await Task.WhenAll(writer.ReadyAsync(cts.Token), reader.ReadyAsync(cts.Token));

                string key = Unique("pwnet.order");
                await writer.SetAsync(key, "v", cancellationToken: cts.Token);

                // The reader's barrier is issued after the write reached the daemon, so the echo
                // must already have been dispatched to it.
                await reader.ReadyAsync(cts.Token);
                Assert.AreEqual("v", reader.Get(key), "a write before the barrier is not readable after it");

                await writer.SetAsync(key, null, cancellationToken: cts.Token);
            }
        }
    }

    private static async Task<ImmutableArray<PipeWirePort>> PortsAsync(
        PipeWireRegistry registry, uint nodeId, PipeWirePortDirection direction, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await registry.WaitForInitialEnumerationAsync(cancellationToken);

            // Monitors included. A null-audio-sink has playback inputs and monitor outputs only, so
            // excluding monitors leaves the output side with nothing and waits for ever.
            ImmutableArray<PipeWirePort> ports =
            [
                .. registry.Current.GetPortsForNode(nodeId).Where(p => p.PortDirection == direction),
            ];

            if (ports.Length >= 1) return ports;
        }
    }
}
