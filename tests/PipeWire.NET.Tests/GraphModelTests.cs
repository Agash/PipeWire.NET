using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// The graph model has no native dependency once the property strings have been read, so the
/// parsing and every snapshot query run on any OS without a daemon.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class GraphModelTests
{
    private static PipeWireGraphSnapshot Build(
        PipeWireNode[]? nodes = null, PipeWirePort[]? ports = null, PipeWireLink[]? links = null) =>
        new(1, nodes ?? [], ports ?? [], links ?? []);

    private static PipeWirePort Port(uint id, uint nodeId, PipeWirePortDirection dir) =>
        new(id, nodeId, $"p{id}", dir, Monitor: false, Exclusive: false);

    [TestMethod]
    public void GetPortsForNode_ReturnsOnlyThatNodesPorts()
    {
        var graph = Build(
            nodes: [new(1, "a", null, null), new(2, "b", null, null)],
            ports: [Port(10, 1, PipeWirePortDirection.Out), Port(11, 1, PipeWirePortDirection.In),
                    Port(20, 2, PipeWirePortDirection.In)]);

        CollectionAssert.AreEquivalent(new uint[] { 10, 11 }, graph.GetPortsForNode(1).Select(p => p.PortId).ToArray());
        CollectionAssert.AreEquivalent(new uint[] { 20 }, graph.GetPortsForNode(2).Select(p => p.PortId).ToArray());
        Assert.AreEqual(0, graph.GetPortsForNode(99).Length);
    }

    [TestMethod]
    public void InputAndOutputLinks_AreNotTheSameSet()
    {
        // The original bug: OutputLinks filtered on the input port, so both sides matched.
        var graph = Build(
            ports: [Port(10, 1, PipeWirePortDirection.Out), Port(20, 2, PipeWirePortDirection.In)],
            links: [new(100, LinkInputNode: 2, LinkInputPort: 20, LinkOutputNode: 1, LinkOutputPort: 10)]);

        Assert.AreEqual(1, graph.GetOutputLinksForPort(10).Length);
        Assert.AreEqual(0, graph.GetInputLinksForPort(10).Length);
        Assert.AreEqual(1, graph.GetInputLinksForPort(20).Length);
        Assert.AreEqual(0, graph.GetOutputLinksForPort(20).Length);
    }

    [TestMethod]
    public void LinkToAnAbsentPort_IsStillReachable()
    {
        // Resolving through ids rather than port objects means a link survives its port not yet
        // having been announced.
        var graph = Build(links: [new(100, 2, 20, 1, 10)]);
        Assert.AreEqual(1, graph.GetOutputLinksForPort(10).Length);
        Assert.IsNull(graph.GetPort(10));
    }

    [TestMethod]
    public void GetLinksForNode_CoversBothDirections()
    {
        var graph = Build(
            ports: [Port(10, 1, PipeWirePortDirection.Out), Port(11, 1, PipeWirePortDirection.In)],
            links: [new(100, 2, 20, 1, 10), new(101, 1, 11, 3, 30)]);

        CollectionAssert.AreEquivalent(new uint[] { 100, 101 },
            graph.GetLinksForNode(1).Select(l => l.LinkId).ToArray());
    }

    [TestMethod]
    public void GetLinksForNode_DoesNotReportAnIntraNodeLinkTwice()
    {
        // A link whose two ends belong to the same node - an internal loopback - is reachable from
        // that node's input port and from its output port. Walking both must still describe one
        // link, or a caller counting connections double-counts exactly the case a filter creates.
        var graph = Build(
            nodes: [new(1, "loopback", null, null)],
            ports: [Port(10, 1, PipeWirePortDirection.Out), Port(11, 1, PipeWirePortDirection.In)],
            links: [new(100, LinkInputNode: 1, LinkInputPort: 11, LinkOutputNode: 1, LinkOutputPort: 10)]);

        PipeWireLink[] links = [.. graph.GetLinksForNode(1)];

        Assert.AreEqual(1, links.Length,
            $"one link reported {links.Length} times; intra-node links are reachable from both ends");
        Assert.AreEqual(100u, links[0].LinkId);
    }

    [TestMethod]
    public void GetLinksForNode_StillReportsBothSidesOfSeparateLinks()
    {
        // Deduplicating must not collapse genuinely distinct links.
        var graph = Build(
            ports: [Port(10, 1, PipeWirePortDirection.Out), Port(11, 1, PipeWirePortDirection.In)],
            links:
            [
                new(100, LinkInputNode: 2, LinkInputPort: 20, LinkOutputNode: 1, LinkOutputPort: 10),
                new(101, LinkInputNode: 1, LinkInputPort: 11, LinkOutputNode: 3, LinkOutputPort: 30),
            ]);

        CollectionAssert.AreEquivalent(new uint[] { 100, 101 },
            graph.GetLinksForNode(1).Select(l => l.LinkId).ToArray());
    }

    [TestMethod]
    public void TryGetObject_DispatchesByKind()
    {
        var graph = Build(
            nodes: [new(1, "a", null, null)],
            ports: [Port(10, 1, PipeWirePortDirection.Out)],
            links: [new(100, 2, 20, 1, 10)]);

        Assert.IsTrue(graph.TryGetObject(1, out var node));
        Assert.AreEqual(PipeWireObjectKind.Node, node!.Kind);
        Assert.IsTrue(graph.TryGetObject(10, out var port));
        Assert.AreEqual(PipeWireObjectKind.Port, port!.Kind);
        Assert.IsTrue(graph.TryGetObject(100, out var link));
        Assert.AreEqual(PipeWireObjectKind.Link, link!.Kind);
        Assert.IsFalse(graph.TryGetObject(999, out _));
    }

    [TestMethod]
    public void Permissions_DecodeFromTheOctalBitsPipeWireReports()
    {
        // 0710 octal is what the daemon reported for a node we created.
        var node = new PipeWireNode(1, "a", null, null, null, (PipeWirePermissions)Convert.ToUInt32("710", 8));
        Assert.IsTrue(node.Permissions.HasFlag(PipeWirePermissions.Read));
        Assert.IsTrue(node.Permissions.HasFlag(PipeWirePermissions.Write));
        Assert.IsTrue(node.Permissions.HasFlag(PipeWirePermissions.Execute));
        Assert.IsTrue(node.Permissions.HasFlag(PipeWirePermissions.Metadata));
        Assert.IsFalse(node.Permissions.HasFlag(PipeWirePermissions.Link));
    }

    [TestMethod]
    public void TryGetPairs_AgreeWithTheNullableGetters()
    {
        var graph = Build(
            nodes: [new(1, "a", null, null)],
            ports: [Port(10, 1, PipeWirePortDirection.Out)],
            links: [new(100, 2, 20, 1, 10)]);

        Assert.IsTrue(graph.TryGetNode(1, out PipeWireNode? node));
        Assert.AreSame(graph.GetNode(1), node);
        Assert.IsTrue(graph.TryGetPort(10, out PipeWirePort? port));
        Assert.AreSame(graph.GetPort(10), port);
        Assert.IsTrue(graph.TryGetLink(100, out PipeWireLink? link));
        Assert.AreSame(graph.GetLink(100), link);

        Assert.IsFalse(graph.TryGetNode(10, out _), "a port id must not resolve as a node");
        Assert.IsFalse(graph.TryGetPort(1, out _));
        Assert.IsFalse(graph.TryGetLink(1, out _));
    }

    [TestMethod]
    public void EmptySnapshot_AnswersEverythingWithoutThrowing()
    {
        var graph = PipeWireGraphSnapshot.Empty;
        Assert.AreEqual(0, graph.Nodes.Length);
        Assert.IsNull(graph.GetNode(1));
        Assert.AreEqual(0, graph.GetPortsForNode(1).Length);
        Assert.AreEqual(0, graph.GetLinksForNode(1).Count());
        Assert.IsFalse(graph.TryGetObject(1, out _));
    }

    [TestMethod]
    public void PortDirectionHelpers_DoNotConflateControlWithDataFlow()
    {
        var control = Port(1, 1, PipeWirePortDirection.Control);
        var notify = Port(2, 1, PipeWirePortDirection.Notify);

        Assert.IsFalse(control.IsDataInput, "a control port is not a data input");
        Assert.IsFalse(notify.IsDataOutput, "a notify port is not a data output");
        Assert.IsTrue(control.IsControl);
        Assert.IsTrue(notify.IsNotify);
    }
}
