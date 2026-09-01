using System.Runtime.Versioning;
using PipeWire.NET.Generated;

namespace PipeWire.NET.Graph;

/// <summary>
/// A link in the local PipeWire graph: a connection between two <see cref="PipeWirePort"/>s.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record PipeWireLink : IPipeWireObject
{
    internal PipeWireLink(
        uint LinkId,
        uint LinkInputNode,
        uint LinkInputPort,
        uint LinkOutputNode,
        uint LinkOutputPort,
        PipeWirePermissions Permissions = PipeWirePermissions.None,
        uint Version = 0)
    {
        this.Permissions = Permissions;
        this.Version = Version;
        this.LinkId = LinkId;
        this.LinkInputNode = LinkInputNode;
        this.LinkInputPort = LinkInputPort;
        this.LinkOutputNode = LinkOutputNode;
        this.LinkOutputPort = LinkOutputPort;
    }

    /// <inheritdoc/>
    public uint Id => LinkId;

    /// <inheritdoc/>
    public PipeWireObjectKind Kind => PipeWireObjectKind.Link;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint Version { get; }

    /// <summary>The PipeWire global id of this link.</summary>
    public uint LinkId { get; }

    /// <summary>The global id of the node this link feeds into.</summary>
    public uint LinkInputNode { get; }

    /// <summary>The global id of the port this link feeds into.</summary>
    public uint LinkInputPort { get; }

    /// <summary>The global id of the node this link starts from.</summary>
    public uint LinkOutputNode { get; }

    /// <summary>The global id of the port this link starts from.</summary>
    public uint LinkOutputPort { get; }
}
