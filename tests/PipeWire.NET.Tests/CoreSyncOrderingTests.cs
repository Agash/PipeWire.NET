using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Interop;

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
                PipeWireNode node = await registry.CreateVirtualNode("Ordering")
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
                await registry.DestroyGlobalAsync(id, cts.Token);

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
            PipeWireNode source = await registry.CreateVirtualNode("OrderSrc")
                .WithName(Unique("pwnet_order_src")).ExecuteAsync(cts.Token);
            PipeWireNode sink = await registry.CreateVirtualNode("OrderSink")
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

            await registry.DestroyGlobalAsync(link.LinkId, cts.Token);
            await registry.DestroyGlobalAsync(source.NodeId, cts.Token);
            await registry.DestroyGlobalAsync(sink.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task AMetadataWriteReachesASecondClient_ButNotBecauseOfABarrier()
    {
        // A round trip orders one connection against the daemon and nothing else. The default store
        // is served by the session manager, a third process, so a write travels
        // writer -> daemon -> session manager -> daemon -> reader, and neither client's barrier
        // creates a happens-before with the middle hop. What can be relied on is that the change
        // arrives, so that is what is waited for; the timeout is the assertion.
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
                try
                {
                    string? seen = await MetadataRelay.AwaitRelayAsync(
                        reader,
                        key,
                        () => writer.SetAsync(key, "v", cancellationToken: cts.Token),
                        cts.Token);

                    Assert.AreEqual("v", seen, "the reader was told about the write, with the wrong value");
                    Assert.AreEqual("v", reader.Get(key),
                        "the change was raised but the store it came from does not hold it");
                }
                finally
                {
                    await writer.SetAsync(key, null, cancellationToken: CancellationToken.None);
                }
            }
        }
    }

    [TestMethod]
    public async Task ARoundTrip_OrdersOnlyItsOwnConnection()
    {
        // The half of the contract that does hold: everything this connection sent before the
        // barrier has been processed by the daemon when the barrier completes. A global created
        // before it is therefore in our own registry by then, with no polling.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-order-own", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("Barrier")
                .WithName(Unique("pwnet_barrier")).ExecuteAsync(cts.Token);

            await CoreSync.RoundTripAsync(ctx, cts.Token);

            Assert.IsNotNull(registry.Current.GetNode(node.NodeId),
                "a global created before the barrier is not in the graph after it");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
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
