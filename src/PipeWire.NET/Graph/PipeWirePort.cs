using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// A discoverable port of a node in the local PipeWire graph.
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
        uint? PortIndex = null,
        bool IsControl = false,
        bool IsPhysical = false,
        bool IsTerminal = false,
        string? DspFormat = null,
        string? AudioChannel = null,
        string? Alias = null,
        string? PortGroup = null,
        string? ObjectPath = null,
        PipeWirePermissions Permissions = PipeWirePermissions.None,
        uint InterfaceVersion = 0,
        PipeWireProperties? Properties = null)
    {
        this.Properties = Properties ?? PipeWireProperties.Empty;
        this.ObjectSerial = this.Properties.Serial;
        this.Permissions = Permissions;
        this.InterfaceVersion = InterfaceVersion;
        this.PortId = PortId;
        this.NodeId = NodeId;
        this.PortName = PortName;
        this.PortDirection = PortDirection;
        this.Monitor = Monitor;
        this.PortIndex = PortIndex;
        this.IsControl = IsControl;
        this.IsPhysical = IsPhysical;
        this.IsTerminal = IsTerminal;
        this.DspFormat = DspFormat;
        this.AudioChannel = AudioChannel;
        this.Alias = Alias;
        this.PortGroup = PortGroup;
        this.ObjectPath = ObjectPath;
    }

    /// <inheritdoc/>
    public uint Id => PortId;

    /// <inheritdoc/>
    public PipeWireObjectKind Kind => PipeWireObjectKind.Port;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint InterfaceVersion { get; }

    /// <inheritdoc/>
    public PipeWireProperties Properties { get; }

    /// <inheritdoc/>
    public ulong? ObjectSerial { get; }

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

    /// <summary>
    /// The port's index within its node (<c>port.id</c>), which is what <c>pw-link</c> prints after
    /// the dot in <c>node.port</c>.
    /// </summary>
    /// <remarks>Not the global id; that is <see cref="Id"/>, which is unique across the graph.</remarks>
    public uint? PortIndex { get; }

    /// <summary>
    /// True when this port carries control data rather than media.
    /// </summary>
    /// <remarks>
    /// A control port negotiates <c>application/control</c>, so linking one to a media port fails
    /// negotiation with EINVAL rather than being refused up front. Filter on this before offering a
    /// port as a link endpoint.
    /// <para>
    /// Two spellings mean this, and either one counts: <c>port.control</c>, and the JACK-style
    /// <c>port.direction=control</c>. PipeWire's own adapter ports use the first and keep the
    /// direction as in or out, so reading the direction alone misses them.
    /// </para>
    /// </remarks>
    public bool IsControl { get; }

    /// <summary>True when the port belongs to physical hardware (<c>port.physical</c>).</summary>
    public bool IsPhysical { get; }

    /// <summary>True when the port consumes the data it receives (<c>port.terminal</c>).</summary>
    public bool IsTerminal { get; }

    /// <summary>The internal buffer format of a DSP port (<c>format.dsp</c>), e.g. "32 bit float mono audio".</summary>
    public string? DspFormat { get; }

    /// <summary>Which channel this port carries (<c>audio.channel</c>), e.g. "FL".</summary>
    public string? AudioChannel { get; }

    /// <summary>An alternative name the port answers to (<c>port.alias</c>).</summary>
    public string? Alias { get; }

    /// <summary>
    /// The group this port belongs to (<c>port.group</c>), which is how a multi-channel stream's
    /// ports are told apart from an unrelated one on the same node.
    /// </summary>
    public string? PortGroup { get; }

    /// <summary>The port's stable path (<c>object.path</c>).</summary>
    public string? ObjectPath { get; }
}
