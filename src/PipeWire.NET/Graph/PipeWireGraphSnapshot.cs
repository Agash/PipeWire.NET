using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// An immutable, internally consistent view of the PipeWire graph at one moment.
/// </summary>
/// <remarks>
/// <para>
/// The registry publishes a new snapshot on every graph change. Holding one is free and reading it
/// needs no lock, so a consumer can walk the whole graph without racing the loop thread or seeing
/// membership shift mid-enumeration.
/// </para>
/// <para>
/// Relationships are resolved here rather than on the entities, which keeps those pure data with no
/// reference back to the registry.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PipeWireGraphSnapshot
{
    /// <summary>An empty graph, published before the first enumeration completes.</summary>
    public static PipeWireGraphSnapshot Empty { get; } = new(0, [], [], []);

    internal PipeWireGraphSnapshot(
        long version,
        IEnumerable<PipeWireNode> nodes,
        IEnumerable<PipeWirePort> ports,
        IEnumerable<PipeWireLink> links)
    {
        Version = version;
        Nodes = [.. nodes];
        Ports = [.. ports];
        Links = [.. links];
    }

    // Indexes are built on first use, not on construction.
    //
    // A snapshot is published for every registry event, and connecting to a busy session publishes
    // one per global - measured at 913 publishes against a 259-node, 654-port graph. Building all
    // six indexes eagerly cost 478us and 177KB each time, so enumeration alone burned about half a
    // second and allocated on the order of 150MB rebuilding indexes that the next event replaced
    // before anything read them. Deferring makes an unread snapshot almost free and leaves the read
    // path unchanged.
    //
    // The lazy fields race benignly: two threads may each build an index, but the two are
    // equivalent and a reference assignment is atomic, so no caller can observe a partial one.

    private FrozenDictionary<uint, PipeWireNode> NodesById =>
        field ??= Nodes.ToFrozenDictionary(static n => n.NodeId);

    private FrozenDictionary<uint, PipeWirePort> PortsById =>
        field ??= Ports.ToFrozenDictionary(static p => p.PortId);

    private FrozenDictionary<uint, PipeWireLink> LinksById =>
        field ??= Links.ToFrozenDictionary(static l => l.LinkId);

    private FrozenDictionary<uint, ImmutableArray<PipeWirePort>> PortsByNode =>
        field ??= Ports.GroupBy(static p => p.NodeId)
                       .ToFrozenDictionary(static g => g.Key, static g => g.ToImmutableArray());

    private FrozenDictionary<uint, ImmutableArray<PipeWireLink>> InputLinksByPort =>
        field ??= Links.GroupBy(static l => l.LinkInputPort)
                       .ToFrozenDictionary(static g => g.Key, static g => g.ToImmutableArray());

    private FrozenDictionary<uint, ImmutableArray<PipeWireLink>> OutputLinksByPort =>
        field ??= Links.GroupBy(static l => l.LinkOutputPort)
                       .ToFrozenDictionary(static g => g.Key, static g => g.ToImmutableArray());

    /// <summary>
    /// Increases by one per published snapshot. A local counter for spotting missed publications;
    /// unrelated to any PipeWire sequence number.
    /// </summary>
    public long Version { get; }

    /// <summary>Every node in the graph.</summary>
    public ImmutableArray<PipeWireNode> Nodes { get; }

    /// <summary>Every port in the graph.</summary>
    public ImmutableArray<PipeWirePort> Ports { get; }

    /// <summary>Every link in the graph.</summary>
    public ImmutableArray<PipeWireLink> Links { get; }

    /// <summary>The node with this id, or <see langword="null"/> if the graph has none.</summary>
    public PipeWireNode? GetNode(uint id) => NodesById.GetValueOrDefault(id);

    /// <summary>The port with this id, or <see langword="null"/> if the graph has none.</summary>
    public PipeWirePort? GetPort(uint id) => PortsById.GetValueOrDefault(id);

    /// <summary>The link with this id, or <see langword="null"/> if the graph has none.</summary>
    public PipeWireLink? GetLink(uint id) => LinksById.GetValueOrDefault(id);

    /// <summary>The node with this id, if the graph has one.</summary>
    public bool TryGetNode(uint id, [NotNullWhen(true)] out PipeWireNode? value) =>
        NodesById.TryGetValue(id, out value);

    /// <summary>The port with this id, if the graph has one.</summary>
    public bool TryGetPort(uint id, [NotNullWhen(true)] out PipeWirePort? value) =>
        PortsById.TryGetValue(id, out value);

    /// <summary>The link with this id, if the graph has one.</summary>
    public bool TryGetLink(uint id, [NotNullWhen(true)] out PipeWireLink? value) =>
        LinksById.TryGetValue(id, out value);

    /// <summary>Looks up any object by id, whatever kind it is.</summary>
    public bool TryGetObject(uint id, [NotNullWhen(true)] out IPipeWireObject? value)
    {
        value = GetNode(id) ?? (IPipeWireObject?)GetPort(id) ?? GetLink(id);
        return value is not null;
    }

    /// <summary>The ports belonging to a node, or empty if it has none.</summary>
    public ImmutableArray<PipeWirePort> GetPortsForNode(uint nodeId) =>
        PortsByNode.GetValueOrDefault(nodeId, []);

    /// <summary>The links feeding into a port.</summary>
    public ImmutableArray<PipeWireLink> GetInputLinksForPort(uint portId) =>
        InputLinksByPort.GetValueOrDefault(portId, []);

    /// <summary>The links sourcing from a port.</summary>
    public ImmutableArray<PipeWireLink> GetOutputLinksForPort(uint portId) =>
        OutputLinksByPort.GetValueOrDefault(portId, []);

    /// <summary>Every link attached to a node, in either direction, each reported once.</summary>
    /// <remarks>
    /// A link whose two ends belong to the same node - an internal loopback, which a filter creates
    /// - is reachable from that node's input port and from its output port. Walking the ports alone
    /// would report it twice and make a caller counting connections double-count exactly that case,
    /// so the ids seen are tracked.
    /// </remarks>
    public IEnumerable<PipeWireLink> GetLinksForNode(uint nodeId)
    {
        HashSet<uint>? seen = null;

        foreach (PipeWirePort port in GetPortsForNode(nodeId))
        {
            foreach (PipeWireLink link in GetInputLinksForPort(port.PortId))
            {
                seen ??= [];
                if (seen.Add(link.LinkId)) yield return link;
            }

            foreach (PipeWireLink link in GetOutputLinksForPort(port.PortId))
            {
                seen ??= [];
                if (seen.Add(link.LinkId)) yield return link;
            }
        }
    }
}
