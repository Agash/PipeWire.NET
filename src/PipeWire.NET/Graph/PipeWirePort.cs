using System.Runtime.Versioning;
using PipeWire.NET.Generated;

namespace PipeWire.NET.Graph;

/// <summary>
/// A discoverable port of a node in the local PipeWire graph.
/// Denotes an accessible endpoint for a PipeWire data stream.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record PipeWirePort : IPipeWireObject
{
    internal PipeWirePort(
        uint PortId,
        uint NodeId,
        string? PortName,
        PipeWirePortDirection PortDirection,
        bool Monitor,
        bool Exclusive,
        PipeWirePermissions Permissions = PipeWirePermissions.None,
        uint Version = 0)
    {
        this.Permissions = Permissions;
        this.Version = Version;
        this.PortId = PortId;
        this.NodeId = NodeId;
        this.PortName = PortName;
        this.PortDirection = PortDirection;
        this.Monitor = Monitor;
        this.Exclusive = Exclusive;
    }

    /// <inheritdoc/>
    public uint Id => PortId;

    /// <inheritdoc/>
    public PipeWireObjectKind Kind => PipeWireObjectKind.Port;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint Version { get; }

    /// <summary>The PipeWire global id of this port.</summary>
    public uint PortId { get; }

    /// <summary>The global id of the node that owns this port.</summary>
    public uint NodeId { get; }

    /// <summary>The port's <c>port.name</c>, if it reported one.</summary>
    public string? PortName { get; }

    /// <summary>The direction this port carries data in.</summary>
    public PipeWirePortDirection PortDirection { get; }

    /// <summary>True when this is a monitor port (<c>port.monitor</c>).</summary>
    public bool Monitor { get; }

    /// <summary>True when this port may only be linked once (<c>port.exclusive</c>, PipeWire 1.6.0 and later).</summary>
    public bool Exclusive { get; }
}
