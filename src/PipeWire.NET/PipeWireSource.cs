using System.Runtime.Versioning;

namespace PipeWire.NET;

/// <summary>
/// A discoverable source node in the local PipeWire graph (camera, virtual camera,
/// screen-capture portal, or PipeWire-routed output).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record PipeWireSource
{
    internal readonly PipeWireRegistry _registry;

    /// <summary>
    /// A discoverable source node in the local PipeWire graph (camera, virtual camera,
    /// screen-capture portal, or PipeWire-routed output).
    /// </summary>
    /// <param name="_registry"></param>
    /// <param name="NodeId">PipeWire global id. Pass to <see cref="PipeWireVideoCapture.Connect(PipeWireSource, ReadOnlySpan{PixelFormat})"/>.</param>
    /// <param name="NodeName">Stable internal name, e.g. <c>v4l2_input.pci-0000_00_14.0-usb-0_1-1.0</c>.</param>
    /// <param name="Description">Human-readable name as the device reports it, e.g. <c>HD Pro Webcam C920</c>.</param>
    /// <param name="MediaClass">
    /// PipeWire media class. Common values:
    /// <c>Video/Source</c> (camera), <c>Stream/Output/Video</c> (virtual camera),
    /// <c>Video/Source/Virtual</c> (screen-capture portal), <c>Audio/Source</c> (mic).
    /// </param>
    /// <param name="NodeNick">Optional short display name reported via <c>node.nick</c>.</param>
    internal PipeWireSource(
        PipeWireRegistry _registry,
        uint NodeId,
        string? NodeName,
        string? Description,
        string? MediaClass,
        string? NodeNick = null)
    {
        this.NodeId = NodeId;
        this.NodeName = NodeName;
        this.Description = Description;
        this.MediaClass = MediaClass;
        this.NodeNick = NodeNick;
        this._registry = _registry;
    }

    /// <summary>The strongly-typed view of <see cref="MediaClass"/>.</summary>
    public PipeWireMediaClass Class => PipeWireMediaClassExtensions.ParseMediaClass(MediaClass);

    /// <summary>True if this node produces video frames a consumer can capture.</summary>
    public bool IsVideoSource => Class.IsVideo();

    /// <summary>True if this node produces audio a consumer can capture.</summary>
    public bool IsAudioSource => Class.IsAudio();

    /// <summary>All ports of this Node.</summary>
    public IEnumerable<PipeWirePort> Ports => _registry._ports
        .Where(port => port.Value.NodeId == NodeId)
        .Select(port => port.Value);

    /// <summary>All input ports of this Node.</summary>
    public IEnumerable<PipeWirePort> InputPorts => Ports
        .Where(port => port.PortDirection == PipeWirePortDirection.In);

    /// <summary>All output ports of this Node.</summary>
    public IEnumerable<PipeWirePort> OutputPorts => Ports
        .Where(port => port.PortDirection == PipeWirePortDirection.Out);

    /// <summary>All links that feed into this node.</summary>
    public IEnumerable<PipeWireLink> InputLinks => InputPorts
        .SelectMany(port => port.InputLinks);

    /// <summary>All links that start from this node.</summary>
    public IEnumerable<PipeWireLink> OutputLinks => OutputPorts
        .SelectMany(port => port.OutputLinks);

    /// <summary>All links related to this Node.</summary>
    public IEnumerable<PipeWireLink> Links => InputLinks.Concat(OutputLinks);

    /// <summary>PipeWire global id. Pass to <see cref="PipeWireVideoCapture.Connect(PipeWireSource, ReadOnlySpan{PixelFormat})"/>.</summary>
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
