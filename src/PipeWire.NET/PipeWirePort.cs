using System.Runtime.Versioning;
using System.Text;
using PipeWire.NET.Generated;

namespace PipeWire.NET;

/// <summary>
/// A discoverable port of a node in the local PipeWire graph.
/// Denotes an accessible endpoint for a PipeWire data stream.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record PipeWirePort
{
    internal readonly PipeWireRegistry _registry;

    internal PipeWirePort(
        PipeWireRegistry _registry,
        uint PortId,
        uint NodeId,
        string? PortName,
        PipeWirePortDirection PortDirection,
        bool Monitor,
        bool Exclusive)
    {
        this.PortId = PortId;
        this.NodeId = NodeId;
        this.PortName = PortName;
        this.PortDirection = PortDirection;
        this.Monitor = Monitor;
        this.Exclusive = Exclusive;
        this._registry = _registry;
    }

    /// <summary>
    /// The owning node of this port.
    /// May be <value>null</value> if the referenced node had been removed from the graph, but the link object was cached.
    /// </summary>
    public PipeWireSource? Node => _registry._sources.GetValueOrDefault(NodeId);

    /// <summary>
    /// The related links that feed into this port.
    /// </summary>
    public IEnumerable<PipeWireLink> InputLinks => _registry._links
        .Where(link => link.Value.InputPort?.PortId == PortId)
        .Select(link => link.Value);

    /// <summary>
    /// The related links that source from this port.
    /// </summary>
    public IEnumerable<PipeWireLink> OutputLinks => _registry._links
        .Where(link => link.Value.InputPort?.PortId == PortId)
        .Select(link => link.Value);

    /// <summary></summary>
    public uint PortId { get; }

    /// <summary></summary>
    public uint NodeId { get; }

    /// <summary></summary>
    public string? PortName { get; }

    /// <summary></summary>
    public PipeWirePortDirection PortDirection { get; }

    /// <summary></summary>
    public bool Monitor { get; }

    /// <summary></summary>
    public bool Exclusive { get; }
}

/// <summary>
/// The direction that a port is registered for.
/// Values are to be interpreted relational to the node that owns the port.
/// </summary>
public enum PipeWirePortDirection
{
    /// <summary>The port is an input to the owning node.</summary>
    In,

    /// <summary>The port is an output of the owning node.</summary>
    Out
}
