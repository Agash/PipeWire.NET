using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// A node in the local PipeWire graph: a device, an application stream, or a virtual sink.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record PipeWireNode : IPipeWireObject
{
    internal PipeWireNode(
        uint NodeId,
        string? NodeName,
        string? Description,
        string? MediaClass,
        string? NodeNick = null,
        PipeWirePermissions Permissions = PipeWirePermissions.None,
        uint InterfaceVersion = 0,
        uint? DeviceId = null,
        uint? ClientId = null,
        uint? FactoryId = null,
        string? ObjectPath = null,
        int? DriverPriority = null,
        int? SessionPriority = null,
        PipeWireProperties? Properties = null)
    {
        this.Properties = Properties ?? PipeWireProperties.Empty;
        this.ObjectSerial = this.Properties.Serial;
        this.Permissions = Permissions;
        this.InterfaceVersion = InterfaceVersion;
        this.DeviceId = DeviceId;
        this.ClientId = ClientId;
        this.FactoryId = FactoryId;
        this.ObjectPath = ObjectPath;
        this.DriverPriority = DriverPriority;
        this.SessionPriority = SessionPriority;
        this.NodeId = NodeId;
        this.NodeName = NodeName;
        this.Description = Description;
        this.MediaClass = MediaClass;
        this.NodeNick = NodeNick;
    }

    /// <inheritdoc/>
    public uint Id => NodeId;

    /// <inheritdoc/>
    public PipeWireObjectKind Kind => PipeWireObjectKind.Node;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint InterfaceVersion { get; }

    /// <inheritdoc/>
    public PipeWireProperties Properties { get; }

    /// <inheritdoc/>
    public ulong? ObjectSerial { get; }

    /// <summary>
    /// What kind of media this node carries, parsed from <see cref="MediaClass"/>.
    /// </summary>
    /// <remarks>
    /// Identity, not capability. Whether media can actually be captured from or sent to this node
    /// depends on its ports; ask the graph.
    /// </remarks>
    public PipeWireMediaKind Media => PipeWireMediaClass.ParseKind(MediaClass);

    /// <summary>
    /// Which way media moves through this node, relative to the graph.
    /// </summary>
    /// <remarks>
    /// A sink is still capturable when it exposes monitor ports, so this does not answer "can I
    /// read from it" - the graph does.
    /// </remarks>
    public PipeWireMediaFlow Flow => PipeWireMediaClass.ParseFlow(MediaClass);

    /// <summary>PipeWire global id, unique among live objects.</summary>
    public uint NodeId { get; }

    /// <summary>Stable internal name, e.g. <c>v4l2_input.pci-0000_00_14.0-usb-0_1-1.0</c>.</summary>
    public string? NodeName { get; }

    /// <summary>Human-readable name as the device reports it, e.g. <c>HD Pro Webcam C920</c>.</summary>
    public string? Description { get; }

    /// <summary>
    /// PipeWire media class. Common values:
    /// <c>Video/Source</c> (camera), <c>Stream/Output/Video</c> (virtual camera),
    /// <c>Video/Source/Virtual</c> (screen-capture portal), <c>Audio/Source</c> (mic).
    /// </summary>
    public string? MediaClass { get; }

    /// <summary>Optional short display name reported via <c>node.nick</c>.</summary>
    public string? NodeNick { get; }

    /// <summary>
    /// The device that provides this node, or null for one that no device backs.
    /// </summary>
    /// <remarks>
    /// A card's nodes belong to whichever profile is selected, so this is the only way to tell
    /// which nodes a profile change will take away. Application streams and virtual nodes have no
    /// device and report null.
    /// </remarks>
    public uint? DeviceId { get; }

    /// <summary>The client that created this node, or null for one the daemon made itself.</summary>
    public uint? ClientId { get; }

    /// <summary>The factory that made this node.</summary>
    public uint? FactoryId { get; }

    /// <summary>The node's stable path (<c>object.path</c>), which survives a reconnect that changes its id.</summary>
    public string? ObjectPath { get; }

    /// <summary>
    /// How willing this node is to drive the graph (<c>priority.driver</c>), higher first.
    /// </summary>
    /// <remarks>The daemon picks the highest-priority candidate as the driver for a cycle.</remarks>
    public int? DriverPriority { get; }

    /// <summary>
    /// How the session manager ranks this node among candidates (<c>priority.session</c>), higher
    /// first. This is what decides which sink becomes the default.
    /// </summary>
    public int? SessionPriority { get; }
}
