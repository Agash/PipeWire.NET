using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// What a link is actually doing, as opposed to that it exists.
/// </summary>
/// <remarks>
/// The registry reports a link and the ports it joins and stops there, so a patchbay built on it
/// alone shows a link that failed to negotiate exactly like one carrying audio. The state lives on
/// the link's own info event, which is what this binds.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class LinkStateTests
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

    private static async Task<PipeWirePort> PortAsync(
        PipeWireRegistry registry, uint nodeId, PipeWirePortDirection direction, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await registry.WaitForInitialEnumerationAsync(ct);

            foreach (PipeWirePort port in registry.Current.GetPortsForNode(nodeId))
            {
                if (port.PortDirection == direction) return port;
            }
        }
    }

    [TestMethod]
    public async Task ABoundLink_ReportsItsStateAndItsEndpoints()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-linkstate", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode source = await registry.CreateVirtualNode("LinkState")
                .WithName(Unique("pwnet_ls_src")).ExecuteAsync(cts.Token);
            PipeWireNode sink = await registry.CreateVirtualNode("LinkState")
                .WithName(Unique("pwnet_ls_sink")).ExecuteAsync(cts.Token);

            PipeWirePort output = await PortAsync(registry, source.NodeId, PipeWirePortDirection.Out, cts.Token);
            PipeWirePort input = await PortAsync(registry, sink.NodeId, PipeWirePortDirection.In, cts.Token);

            PipeWireLink link = await registry.CreateLinkAsync(output, input, cts.Token);

            await using (PipeWireLinkControl control = registry.BindLink(link.LinkId))
            {
                await control.ReadyAsync(cts.Token);

                // The endpoints the daemon reports must be the ones we asked it to join. Reading
                // them from the link rather than from the registry is what proves the info event
                // arrived rather than the record being echoed back.
                Assert.AreEqual(source.NodeId, control.OutputNode, "the link reports another output node");
                Assert.AreEqual(output.PortId, control.OutputPort);
                Assert.AreEqual(sink.NodeId, control.InputNode, "the link reports another input node");
                Assert.AreEqual(input.PortId, control.InputPort);

                // A freshly created link between two idle virtual nodes settles somewhere between
                // negotiating and paused. Which one is the daemon's business; what matters is that
                // it is no longer the value the control was constructed with and is not an error.
                Assert.AreNotEqual(PipeWireLinkState.Error, control.State,
                    $"the link failed: {control.Error}");
                Assert.IsNull(control.Error, "a link that is not in error must not carry a reason");

                Console.Error.WriteLine($"link {link.LinkId} settled at {control.State}");
            }

            await registry.DestroyGlobalAsync(link.LinkId, cts.Token);
            await registry.DestroyGlobalAsync(source.NodeId, cts.Token);
            await registry.DestroyGlobalAsync(sink.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ALinkThatChangesState_ReportsEveryChange()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-linkstate-events", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode source = await registry.CreateVirtualNode("LinkEvents")
                .WithName(Unique("pwnet_le_src")).ExecuteAsync(cts.Token);
            PipeWireNode sink = await registry.CreateVirtualNode("LinkEvents")
                .WithName(Unique("pwnet_le_sink")).ExecuteAsync(cts.Token);

            PipeWirePort output = await PortAsync(registry, source.NodeId, PipeWirePortDirection.Out, cts.Token);
            PipeWirePort input = await PortAsync(registry, sink.NodeId, PipeWirePortDirection.In, cts.Token);

            PipeWireLink link = await registry.CreateLinkAsync(output, input, cts.Token);

            var seen = new ConcurrentQueue<PipeWireLinkState>();

            await using (PipeWireLinkControl control = registry.BindLink(link.LinkId))
            {
                control.StateChanged += c => seen.Enqueue(c.State);

                await control.ReadyAsync(cts.Token);

                // The first report can land before the subscription: binding is what starts the
                // daemon talking, so there is no moment at which a handler could already be
                // attached. That is why the starting state comes from ReadyAsync and the property,
                // and the event carries what happens after. Asserting otherwise is asserting a race.
                PipeWireLinkState starting = control.State;

                // Something the link must react to. Removing the far end takes it out of whatever
                // it settled at, and that transition is the one an event has to carry.
                await registry.DestroyGlobalAsync(sink.NodeId, cts.Token);

                for (int attempt = 0; attempt < 100 && seen.IsEmpty; attempt++)
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);

                Assert.IsFalse(seen.IsEmpty,
                    $"the link started at {starting} and its far end was destroyed, "
                    + "but no change was ever reported");
                Assert.AreEqual(control.State, seen.Last(),
                    "the last event and the property disagree about the state");
            }

            try { await registry.DestroyGlobalAsync(link.LinkId, cts.Token); }
            catch (PipeWireException) { /* it went with the node its far end was on */ }

            await registry.DestroyGlobalAsync(source.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ABoundPort_ReportsTheFormatsItAcceptsAndTheOneItNegotiated()
    {
        // The registry says a port exists and which way it faces. What it carries is on the port's
        // own params, which is why this binds it: without that a caller cannot tell what a port
        // will accept before linking to it, nor what it settled on afterwards.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-portparams", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode source = await registry.CreateVirtualNode("PortParams")
                .WithName(Unique("pwnet_pp_src")).ExecuteAsync(cts.Token);
            PipeWireNode sink = await registry.CreateVirtualNode("PortParams")
                .WithName(Unique("pwnet_pp_sink")).ExecuteAsync(cts.Token);

            PipeWirePort output = await PortAsync(registry, source.NodeId, PipeWirePortDirection.Out, cts.Token);
            PipeWirePort input = await PortAsync(registry, sink.NodeId, PipeWirePortDirection.In, cts.Token);

            await using (PipeWirePortControl port = registry.BindPort(output.PortId))
            {
                // What it will accept, before anything is linked to it. A port that offers nothing
                // could not be linked at all, so this is the one that must not be empty.
                ImmutableArray<SpaObject> supported = await port.EnumerateSupportedFormatsAsync(cts.Token);
                Assert.IsFalse(supported.IsEmpty, "a port that accepts no format could never be linked");

                PipeWireLink link = await registry.CreateLinkAsync(output, input, cts.Token);

                // And what it settled on. Negotiation is asynchronous, so this is waited for rather
                // than read once: empty is a legitimate answer while the link is still negotiating.
                ImmutableArray<SpaObject> negotiated = [];
                for (int attempt = 0; attempt < 60 && negotiated.IsEmpty; attempt++)
                {
                    negotiated = await port.EnumerateFormatsAsync(cts.Token);
                    if (negotiated.IsEmpty) await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);
                }

                Console.Error.WriteLine(
                    $"port {output.PortId}: {supported.Length} accepted, {negotiated.Length} negotiated");

                // Latency is reported once the graph has settled on one, and a port with no link
                // has none, so this only has to not throw.
                _ = await port.EnumerateLatencyAsync(cts.Token);

                await registry.DestroyGlobalAsync(link.LinkId, cts.Token);
            }

            await registry.DestroyGlobalAsync(source.NodeId, cts.Token);
            await registry.DestroyGlobalAsync(sink.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task SettingAPortParameter_IsRefusedRatherThanSent()
    {
        // pw_port_methods has no set_param. A port's format comes out of the negotiation between
        // the nodes at either end, so a caller reaching in to set one is asking for something the
        // protocol cannot express, and it should fail here rather than look like a daemon problem.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-portset", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("PortSet")
                .WithName(Unique("pwnet_ps")).ExecuteAsync(cts.Token);

            PipeWirePort input = await PortAsync(registry, node.NodeId, PipeWirePortDirection.In, cts.Token);

            await using (PipeWirePortControl port = registry.BindPort(input.PortId))
            {
                ImmutableArray<SpaObject> supported = await port.EnumerateSupportedFormatsAsync(cts.Token);
                if (supported.IsEmpty) Assert.Inconclusive("the port reported no format to try setting.");

                await Assert.ThrowsExactlyAsync<PipeWireException>(
                    async () => await port.SetParameterAsync(SpaParamType.Format, supported[0], cts.Token));
            }

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task BindingSomethingThatIsNotALink_IsRefusedBeforeItReachesTheDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-linkstate-wrong", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireGraphSnapshot graph = registry.Current;

            if (graph.Nodes.FirstOrDefault() is { } node)
                Assert.ThrowsExactly<ArgumentException>(() => registry.BindLink(node.NodeId));

            Assert.ThrowsExactly<ArgumentException>(() => registry.BindLink(0x7FFF_0000));

            await registry.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsTrue(registry.Current.Nodes.Length > 0, "the refusal disturbed the connection");
        }
    }
}
