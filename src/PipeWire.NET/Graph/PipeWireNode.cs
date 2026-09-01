using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// A node in the local PipeWire graph: a device, an application stream, or a virtual sink.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record PipeWireNode : IPipeWireObject
{
    /// <param name="NodeId">PipeWire global id.</param>
    /// <param name="NodeName">Stable internal name, e.g. <c>v4l2_input.pci-0000_00_14.0-usb-0_1-1.0</c>.</param>
    /// <param name="Description">Human-readable name as the device reports it.</param>
    /// <param name="MediaClass">
    /// PipeWire media class: <c>Video/Source</c> (camera), <c>Stream/Output/Video</c> (virtual
    /// camera), <c>Video/Source/Virtual</c> (screen-capture portal), <c>Audio/Source</c> (mic).
    /// </param>
    /// <param name="NodeNick">Optional short display name from <c>node.nick</c>.</param>
    /// <param name="Permissions">What this client may do with the node.</param>
    /// <param name="Version">The interface version the daemon announced.</param>
    internal PipeWireNode(
        uint NodeId,
        string? NodeName,
        string? Description,
        string? MediaClass,
        string? NodeNick = null,
        PipeWirePermissions Permissions = PipeWirePermissions.None,
        uint Version = 0)
    {
        this.Permissions = Permissions;
        this.Version = Version;
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
    public uint Version { get; }

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
}
