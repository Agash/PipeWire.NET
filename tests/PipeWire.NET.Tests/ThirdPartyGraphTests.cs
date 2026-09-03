using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// The core graph seen from outside: nodes and links this library creates must be visible to and
/// mutable by PipeWire's own tools, and changes those tools make must reach our snapshot.
/// </summary>
/// <remarks>
/// Every other graph test has the library on both sides of the exchange, which proves only that it
/// agrees with itself. Here <c>pw-link</c>, <c>pw-cli</c> and <c>pw-loopback</c> are separate
/// processes with their own connections, so agreement has to be with PipeWire rather than with us.
/// This is also the shape a patchbay runs in: something else is always editing the same graph.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class ThirdPartyGraphTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

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

    private static Task<PipeWireGraphSnapshot> WaitForPortsAsync(
        PipeWireRegistry r, uint nodeId, CancellationToken ct) =>
        WaitForAsync(r, g => g.GetPortsForNode(nodeId).Length == 4, ct);

    // ---------------------------------------------------------------- we create, they see

    [TestMethod]
    public async Task NodesWeCreate_AreVisibleToPwLinkWithTheSameIds()
    {
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-tp-visible", cts.Token);

        await using (ctx)
        await using (reg)
        {
            PipeWireNode node = await reg.CreateVirtualNode("Visible")
                                         .WithName("pwnet_tp_visible").ExecuteAsync(cts.Token);
            PipeWireGraphSnapshot graph = await WaitForPortsAsync(reg, node.NodeId, cts.Token);

            Dictionary<string, uint> outputs = await PwTools.ListPortsAsync(outputs: true, cts.Token);
            Dictionary<string, uint> inputs = await PwTools.ListPortsAsync(outputs: false, cts.Token);

            // Every port we report must exist for pw-link under the same id, or a user wiring by
            // hand and an app wiring by API are talking about different objects.
            foreach (PipeWirePort port in graph.GetPortsForNode(node.NodeId))
            {
                string full = $"pwnet_tp_visible:{port.PortName}";
                Dictionary<string, uint> side = port.IsDataOutput ? outputs : inputs;

                Assert.IsTrue(side.TryGetValue(full, out uint theirId),
                    $"pw-link cannot see {full}, which we report as port {port.PortId}");
                Assert.AreEqual(port.PortId, theirId,
                    $"{full}: we call it {port.PortId}, pw-link calls it {theirId}");
            }
        }
    }

    [TestMethod]
    public async Task ALinkWeCreate_IsListedByPwLinkWithMatchingEndpoints()
    {
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-tp-ourlink", cts.Token);

        await using (ctx)
        await using (reg)
        {
            (PipeWireNode a, PipeWireNode b, PipeWireGraphSnapshot ready) =
                await TwoNodesAsync(reg, "pwnet_tp_la", "pwnet_tp_lb", cts.Token);

            PipeWirePort output = ready.GetPortsForNode(a.NodeId, PipeWirePortDirection.Out)
                                       .OrderBy(p => p.PortId).First();
            PipeWirePort input = ready.GetPortsForNode(b.NodeId, PipeWirePortDirection.In)
                                      .OrderBy(p => p.PortId).First();

            PipeWireLink link = await reg.CreateLink(output, input).ExecuteAsync(cts.Token);

            List<(uint Link, uint Output, uint Input)> theirs = await PwTools.ListLinksAsync(cts.Token);
            (uint Link, uint Output, uint Input) match = theirs.FirstOrDefault(l => l.Link == link.LinkId);

            Assert.AreNotEqual(0u, match.Link, "pw-link does not list the link we created");
            Assert.AreEqual(output.PortId, match.Output, "we disagree on which port the link leaves");
            Assert.AreEqual(input.PortId, match.Input, "we disagree on which port the link enters");
        }
    }

    // ---------------------------------------------------------------- they create, we see

    [TestMethod]
    public async Task ALinkPwLinkCreates_ReachesOurSnapshotWithTheRightEndpoints()
    {
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-tp-theirlink", cts.Token);

        await using (ctx)
        await using (reg)
        {
            (PipeWireNode a, PipeWireNode b, PipeWireGraphSnapshot ready) =
                await TwoNodesAsync(reg, "pwnet_tp_ta", "pwnet_tp_tb", cts.Token);

            PipeWirePort output = ready.GetPortsForNode(a.NodeId, PipeWirePortDirection.Out)
                                       .OrderBy(p => p.PortId).First();
            PipeWirePort input = ready.GetPortsForNode(b.NodeId, PipeWirePortDirection.In)
                                      .OrderBy(p => p.PortId).First();

            // A user at a terminal, not us.
            await PwTools.LinkAsync($"pwnet_tp_ta:{output.PortName}", $"pwnet_tp_tb:{input.PortName}", cts.Token);

            PipeWireGraphSnapshot linked = await WaitForAsync(
                reg, g => g.GetOutputLinksForPort(output.PortId).Length == 1, cts.Token);

            PipeWireLink link = linked.GetOutputLinksForPort(output.PortId).Single();
            Assert.AreEqual(output.PortId, link.LinkOutputPort);
            Assert.AreEqual(input.PortId, link.LinkInputPort);
            Assert.AreEqual(a.NodeId, link.LinkOutputNode);
            Assert.AreEqual(b.NodeId, link.LinkInputNode);
            Assert.AreEqual(1, linked.GetInputLinksForPort(input.PortId).Length,
                "the link must be indexed from the input side too");
        }
    }

    [TestMethod]
    public async Task ALinkPwLinkRemoves_LeavesOurSnapshot()
    {
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-tp-unlink", cts.Token);

        await using (ctx)
        await using (reg)
        {
            (PipeWireNode a, PipeWireNode b, PipeWireGraphSnapshot ready) =
                await TwoNodesAsync(reg, "pwnet_tp_ua", "pwnet_tp_ub", cts.Token);

            PipeWirePort output = ready.GetPortsForNode(a.NodeId, PipeWirePortDirection.Out)
                                       .OrderBy(p => p.PortId).First();
            PipeWirePort input = ready.GetPortsForNode(b.NodeId, PipeWirePortDirection.In)
                                      .OrderBy(p => p.PortId).First();

            // We create it, they tear it down - the patchbay case exactly.
            PipeWireLink link = await reg.CreateLink(output, input).ExecuteAsync(cts.Token);
            await PwTools.DisconnectAsync(link.LinkId, cts.Token);

            PipeWireGraphSnapshot after = await WaitForAsync(
                reg, g => g.GetLink(link.LinkId) is null, cts.Token);

            Assert.AreEqual(0, after.GetOutputLinksForPort(output.PortId).Length);
            Assert.AreEqual(0, after.GetInputLinksForPort(input.PortId).Length);
        }
    }

    [TestMethod]
    public async Task ANodePwCliDestroys_LeavesOurSnapshotWithItsPorts()
    {
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-tp-destroy", cts.Token);

        await using (ctx)
        await using (reg)
        {
            PipeWireNode node = await reg.CreateVirtualNode("Doomed")
                                         .WithName("pwnet_tp_doomed").WithLinger().ExecuteAsync(cts.Token);
            PipeWireGraphSnapshot ready = await WaitForPortsAsync(reg, node.NodeId, cts.Token);
            uint[] portIds = [.. ready.GetPortsForNode(node.NodeId).Select(p => p.PortId)];

            await PwTools.DestroyAsync(node.NodeId, cts.Token);

            PipeWireGraphSnapshot after = await WaitForAsync(
                reg, g => g.GetNode(node.NodeId) is null, cts.Token);

            Assert.AreEqual(0, after.GetPortsForNode(node.NodeId).Length);
            foreach (uint portId in portIds)
                Assert.IsNull(after.GetPort(portId),
                    $"port {portId} outlived the node a third party destroyed");
        }
    }

    // ---------------------------------------------------------------- third-party nodes

    [TestMethod]
    public async Task AThirdPartyNodeAppearsAndWeCanLinkToIt()
    {
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-tp-loopback", cts.Token);

        await using (ctx)
        await using (reg)
        {
            await using PwTools.Loopback loop = await PwTools.StartLoopbackAsync("pwnet_tp_loop", cts.Token);

            // pw-loopback publishes a pair: input.NAME (Stream/Input/Audio) and output.NAME
            // (Stream/Output/Audio). Both are nodes we did not create, from a process that knows
            // nothing about us.
            PipeWireGraphSnapshot graph = await WaitForAsync(
                reg,
                // Both directions, not just "has any port". A loopback's input node publishes its
                // inputs first and its monitors a moment later, so waiting for one port lets the
                // walk continue before the monitors exist - and the capability assertion below is
                // about exactly those.
                g => g.Nodes.Any(n => n.NodeName == "input.pwnet_tp_loop"
                                      && g.GetPortsForNode(n.NodeId, PipeWirePortDirection.In).Any()
                                      && g.GetPortsForNode(n.NodeId, PipeWirePortDirection.Out).Any())
                     && g.Nodes.Any(n => n.NodeName == "output.pwnet_tp_loop"),
                cts.Token);

            PipeWireNode theirs = graph.Nodes.First(n => n.NodeName == "input.pwnet_tp_loop");
            PipeWireNode theirOutputSide = graph.Nodes.First(n => n.NodeName == "output.pwnet_tp_loop");

            // Identity parsed from a media.class we did not invent, on a node we did not create.
            Assert.AreEqual(PipeWireMediaKind.Audio, theirs.Media);
            Assert.AreEqual(PipeWireMediaFlow.Sink, theirs.Flow,
                "Stream/Input/Audio is the graph writing into it");
            Assert.AreEqual(PipeWireMediaFlow.Source, theirOutputSide.Flow,
                "Stream/Output/Audio is the graph reading from it");

            // Capability comes from ports, and the loopback input node has both: inputs to feed it
            // and monitors to read back from.
            Assert.IsTrue(graph.CanSendTo(theirs), "the loopback input must accept audio");
            Assert.IsTrue(graph.CanCaptureFrom(theirs), "and expose monitors to read it back");

            PipeWirePort theirInput = graph.GetPortsForNode(theirs.NodeId, PipeWirePortDirection.In)
                                           .OrderBy(p => p.PortId).First();

            PipeWireNode ours = await reg.CreateVirtualNode("Feeder")
                                         .WithName("pwnet_tp_feeder").ExecuteAsync(cts.Token);
            PipeWireGraphSnapshot ready = await WaitForPortsAsync(reg, ours.NodeId, cts.Token);
            PipeWirePort ourOutput = ready.GetPortsForNode(ours.NodeId, PipeWirePortDirection.Out)
                                          .OrderBy(p => p.PortId).First();

            PipeWireLink link = await reg.CreateLink(ourOutput, theirInput).ExecuteAsync(cts.Token);

            Assert.IsNotNull(reg.Current.GetLink(link.LinkId));
            Assert.AreEqual(theirs.NodeId, reg.Current.GetLink(link.LinkId)!.LinkInputNode,
                "the link must land on the third-party node we targeted");
        }
    }

    [TestMethod]
    public async Task AThirdPartyNodeDisappearing_TakesItsPortsAndLinksWithIt()
    {
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-tp-vanish", cts.Token);

        await using (ctx)
        await using (reg)
        {
            uint theirNode;
            {
                await using PwTools.Loopback loop =
                    await PwTools.StartLoopbackAsync("pwnet_tp_vanish", cts.Token);

                PipeWireGraphSnapshot graph = await WaitForAsync(
                    reg,
                    g => g.Nodes.Any(n => n.NodeName == "input.pwnet_tp_vanish"
                                          && g.GetPortsForNode(n.NodeId).Length > 0),
                    cts.Token);

                theirNode = graph.Nodes.First(n => n.NodeName == "input.pwnet_tp_vanish").NodeId;
                Assert.IsTrue(graph.GetPortsForNode(theirNode).Length > 0,
                    "the node must have ports before we can prove they leave with it");
            }   // the loopback process is killed here

            PipeWireGraphSnapshot after = await WaitForAsync(
                reg, g => g.GetNode(theirNode) is null, cts.Token);

            Assert.AreEqual(0, after.GetPortsForNode(theirNode).Length,
                "a third party going away must not leave its ports in our graph");
        }
    }

    // ---------------------------------------------------------------- concurrent editing

    [TestMethod]
    public async Task AThirdPartyChurningLinks_KeepsOurSnapshotConsistent()
    {
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-tp-churn", cts.Token);

        await using (ctx)
        await using (reg)
        {
            (PipeWireNode a, PipeWireNode b, PipeWireGraphSnapshot ready) =
                await TwoNodesAsync(reg, "pwnet_tp_ca", "pwnet_tp_cb", cts.Token);

            PipeWirePort output = ready.GetPortsForNode(a.NodeId, PipeWirePortDirection.Out)
                                       .OrderBy(p => p.PortId).First();
            PipeWirePort input = ready.GetPortsForNode(b.NodeId, PipeWirePortDirection.In)
                                      .OrderBy(p => p.PortId).First();

            // Read the graph continuously while an outside process rewires it underneath.
            using var stop = new CancellationTokenSource();
            Exception? torn = null;
            long reads = 0;

            Task reader = Task.Run(() =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        PipeWireGraphSnapshot g = reg.Current;
                        foreach (PipeWireLink link in g.Links)
                        {
                            if (!g.GetOutputLinksForPort(link.LinkOutputPort).Contains(link))
                                throw new InvalidOperationException($"link {link.LinkId} missing from its own index");
                            if (!g.GetInputLinksForPort(link.LinkInputPort).Contains(link))
                                throw new InvalidOperationException($"link {link.LinkId} missing from the input index");
                        }
                        Interlocked.Increment(ref reads);
                    }
                }
                catch (Exception ex) { torn ??= ex; }
            }, CancellationToken.None);

            for (int i = 0; i < 6; i++)
            {
                await PwTools.LinkAsync($"pwnet_tp_ca:{output.PortName}", $"pwnet_tp_cb:{input.PortName}", cts.Token);
                PipeWireGraphSnapshot on = await WaitForAsync(
                    reg, g => g.GetOutputLinksForPort(output.PortId).Length == 1, cts.Token);

                await PwTools.DisconnectAsync(on.GetOutputLinksForPort(output.PortId).Single().LinkId, cts.Token);
                await WaitForAsync(reg, g => g.GetOutputLinksForPort(output.PortId).Length == 0, cts.Token);
            }

            await stop.CancelAsync();
            await reader;

            Assert.IsNull(torn, $"a reader saw an inconsistent graph while a third party edited it: {torn}");
            Assert.IsTrue(Volatile.Read(ref reads) > 50, $"the reader barely ran ({reads} reads)");
        }
    }

    [TestMethod]
    public async Task OurViewOfLinks_MatchesPwLinkExactlyAfterChurn()
    {
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-tp-agree", cts.Token);

        await using (ctx)
        await using (reg)
        {
            (PipeWireNode a, PipeWireNode b, PipeWireGraphSnapshot ready) =
                await TwoNodesAsync(reg, "pwnet_tp_aa", "pwnet_tp_ab", cts.Token);

            PipeWirePort[] outputs = [.. ready.GetPortsForNode(a.NodeId, PipeWirePortDirection.Out).OrderBy(p => p.PortId)];
            PipeWirePort[] inputs = [.. ready.GetPortsForNode(b.NodeId, PipeWirePortDirection.In).OrderBy(p => p.PortId)];

            // Wire both channels, one by each side, then compare the whole picture.
            PipeWireLink ourLink = await reg.CreateLink(outputs[0], inputs[0]).ExecuteAsync(cts.Token);
            await PwTools.LinkAsync($"pwnet_tp_aa:{outputs[1].PortName}", $"pwnet_tp_ab:{inputs[1].PortName}", cts.Token);

            PipeWireGraphSnapshot graph = await WaitForAsync(
                reg,
                g => g.GetOutputLinksForPort(outputs[0].PortId).Length == 1
                     && g.GetOutputLinksForPort(outputs[1].PortId).Length == 1,
                cts.Token);

            List<(uint Link, uint Output, uint Input)> theirs = await PwTools.ListLinksAsync(cts.Token);

            foreach (PipeWirePort port in outputs)
            {
                PipeWireLink mine = graph.GetOutputLinksForPort(port.PortId).Single();
                (uint Link, uint Output, uint Input) match = theirs.FirstOrDefault(l => l.Link == mine.LinkId);

                Assert.AreNotEqual(0u, match.Link, $"pw-link does not know link {mine.LinkId}");
                Assert.AreEqual(mine.LinkOutputPort, match.Output);
                Assert.AreEqual(mine.LinkInputPort, match.Input);
            }

            Assert.IsNotNull(graph.GetLink(ourLink.LinkId), "our own link must still be there among theirs");
        }
    }

    // ---------------------------------------------------------------- helper

    private static async Task<(PipeWireNode A, PipeWireNode B, PipeWireGraphSnapshot Ready)> TwoNodesAsync(
        PipeWireRegistry reg, string nameA, string nameB, CancellationToken ct)
    {
        PipeWireNode a = await reg.CreateVirtualNode(nameA).WithName(nameA).ExecuteAsync(ct);
        PipeWireNode b = await reg.CreateVirtualNode(nameB).WithName(nameB).ExecuteAsync(ct);

        PipeWireGraphSnapshot ready = await WaitForAsync(
            reg,
            g => g.GetPortsForNode(a.NodeId).Length == 4 && g.GetPortsForNode(b.NodeId).Length == 4,
            ct);

        return (a, b, ready);
    }
}
