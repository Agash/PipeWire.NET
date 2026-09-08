using System.Runtime.Versioning;
using System.Text;
using PipeWire.NET.Generated;

namespace PipeWire.NET;

/// <summary>
/// A discoverable link in the local PipeWire graph.
/// Denotes a connection between two <see langword="ports" cref="PipeWirePort"/> on the graph.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record PipeWireLink
{
    internal readonly PipeWireRegistry _registry;

    internal PipeWireLink(
        PipeWireRegistry _registry,
        uint LinkId,
        uint LinkInputNode,
        uint LinkInputPort,
        uint LinkOutputNode,
        uint LinkOutputPort)
    {
        this.LinkId = LinkId;
        this.LinkInputNode = LinkInputNode;
        this.LinkInputPort = LinkInputPort;
        this.LinkOutputNode = LinkOutputNode;
        this.LinkOutputPort = LinkOutputPort;
        this._registry = _registry;
    }

    /// <summary>
    /// The related node on the sourcing start of this link.
    /// May be <value>null</value> if the referenced node had been removed from the graph, but the link object was cached.
    /// </summary>
    public PipeWireSource? OutputNode => _registry._sources.GetValueOrDefault(LinkOutputNode);

    /// <summary>
    /// The related port on the sourcing start of this link.
    /// May be <value>null</value> if the referenced port had been removed from the graph, but the link object was cached.
    /// </summary>
    public PipeWirePort? OutputPort => _registry._ports.GetValueOrDefault(LinkOutputPort);

    /// <summary>
    /// The related node on the feeding end of this link.
    /// May be <value>null</value> if the referenced node had been removed from the graph, but the link object was cached.
    /// </summary>
    public PipeWireSource? InputNode => _registry._sources.GetValueOrDefault(LinkInputNode);

    /// <summary>
    /// The related port on the feeding end of this link.
    /// May be <value>null</value> if the referenced port had been removed from the graph, but the link object was cached.
    /// </summary>
    public PipeWirePort? InputPort => _registry._ports.GetValueOrDefault(LinkInputPort);

    /// <summary></summary>
    public uint LinkId { get; }

    /// <summary></summary>
    public uint LinkInputNode { get; }

    /// <summary></summary>
    public uint LinkInputPort { get; }

    /// <summary></summary>
    public uint LinkOutputNode { get; }

    /// <summary></summary>
    public uint LinkOutputPort { get; }

    /// <summary>
    /// Removes this link from the graph and returns its previous ID, as well as referencing IDs of relational nodes and ports.
    /// </summary>
    /// <param name="id">The ID of the former link.</param>
    /// <param name="feedNodeId">The ID of the feeding node of the former link.</param>
    /// <param name="feedPortId">The ID of the feeding port of the former link.</param>
    /// <param name="sinkNodeId">The ID of the target node of the former link.</param>
    /// <param name="sinkPortId">The ID of the target port of the former link.</param>
    /// <exception cref="InvalidOperationException">If the link could not be removed due to internal errors inside pipewire.</exception>
    public unsafe void Deconstruct(out uint id, out uint feedNodeId, out uint feedPortId, out uint sinkNodeId, out uint sinkPortId)
    {
        int result;
        using (_registry._ctx.Lock())
        {
            Native.GetInterface(_registry._registry, out pw_registry_methods* methods, out void* data);
            result = methods->destroy(data, LinkId);
        }

        if (result == 0)
        {
            throw new InvalidOperationException($"Removing link {LinkId} failed");
        }

        id = LinkId;
        feedNodeId = LinkOutputNode;
        feedPortId = LinkOutputPort;
        sinkNodeId = LinkInputNode;
        sinkPortId = LinkInputPort;
    }
}
