using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

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
        uint InterfaceVersion = 0,
        bool IsPassive = false,
        uint? FactoryId = null,
        uint? ClientId = null,
        PipeWireProperties? Properties = null)
    {
        this.Properties = Properties ?? PipeWireProperties.Empty;
        this.ObjectSerial = this.Properties.Serial;
        this.IsPassive = IsPassive;
        this.FactoryId = FactoryId;
        this.ClientId = ClientId;
        this.Permissions = Permissions;
        this.InterfaceVersion = InterfaceVersion;
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
    public uint InterfaceVersion { get; }

    /// <inheritdoc/>
    public PipeWireProperties Properties { get; }

    /// <inheritdoc/>
    public ulong? ObjectSerial { get; }

    /// <summary>
    /// True when the link does not by itself keep its nodes running (<c>link.passive</c>).
    /// </summary>
    /// <remarks>
    /// A passive link carries data when something else already drives the graph, and does not
    /// count as a reason to start it. Monitor and metering links are made this way.
    /// </remarks>
    public bool IsPassive { get; }

    /// <summary>The factory that made this link.</summary>
    public uint? FactoryId { get; }

    /// <summary>The client that asked for this link.</summary>
    public uint? ClientId { get; }

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
