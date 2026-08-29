using System.Runtime.Versioning;
namespace PipeWire.NET;

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
[SupportedOSPlatform("linux")]
public sealed record PipeWireSource(
    PipeWireRegistry _registry,
    uint NodeId,
    string? NodeName,
    string? Description,
    string? MediaClass,
    string? NodeNick = null)
{
    internal readonly PipeWireRegistry _registry = _registry;

    /// <summary>The strongly-typed view of <see cref="MediaClass"/>.</summary>
    public PipeWireMediaClass Class => PipeWireMediaClassExtensions.ParseMediaClass(MediaClass);

    /// <summary>True if this node produces video frames a consumer can capture.</summary>
    public bool IsVideoSource => Class.IsVideo();

    /// <summary>True if this node produces audio a consumer can capture.</summary>
    public bool IsAudioSource => Class.IsAudio();

    /// <summary>
    /// todo: write docs
    /// </summary>
    public IEnumerable<PipeWirePort> Ports => _registry._ports
        .Where(port => port.Value.NodeId == NodeId)
        .Select(port => port.Value);

    /// <summary>
    /// todo: write docs
    /// </summary>
    public IEnumerable<PipeWireLink> InputLinks => Ports.SelectMany(port => port.InputLinks);

    /// <summary>
    /// todo: write docs
    /// </summary>
    public IEnumerable<PipeWireLink> OutputLinks => Ports.SelectMany(port => port.OutputLinks);
}
