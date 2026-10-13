using System.Runtime.Versioning;
using System.Collections.Immutable;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Media;

/// <summary>
/// What a stream connects to: a node by id, an object by name (<c>target.object</c>), or, by default, the
/// node the session manager picks.
/// </summary>
/// <param name="NodeId">The node's global id; null for none in particular.</param>
/// <param name="ObjectName">The target object's name or serial; null for none.</param>
public readonly record struct PipeWireTarget(uint? NodeId, string? ObjectName)
{
    /// <summary>The node the session manager picks.</summary>
    public static PipeWireTarget Any => default;

    /// <summary>A node from the graph.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The target.</returns>
    [SupportedOSPlatform("linux")]
    public static PipeWireTarget Node(PipeWireNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new(node.NodeId, null);
    }

    /// <summary>A node by its global id.</summary>
    /// <param name="nodeId">The id.</param>
    /// <returns>The target.</returns>
    public static PipeWireTarget Id(uint nodeId) => new(nodeId, null);

    /// <summary>An object by name or serial, which the session manager resolves.</summary>
    /// <param name="objectName">The name or serial.</param>
    /// <returns>The target.</returns>
    public static PipeWireTarget Named(string objectName)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectName);
        return new(null, objectName);
    }

    internal uint NativeId => NodeId ?? NativeConstants.PW_ID_ANY;
}

/// <summary>How an output stream joins the graph.</summary>
public sealed record StreamOutputOptions
{
    /// <summary>The defaults: linked by the session manager, not driving the graph.</summary>
    public static StreamOutputOptions Default { get; } = new();

    /// <summary>Whether the session manager links the stream; false leaves the links to the caller.</summary>
    public bool AutoConnect { get; init; } = true;

    /// <summary>Whether the stream drives the graph's clock instead of following it.</summary>
    public bool Driver { get; init; }
}

/// <summary>How an audio capture stream connects and what it asks for.</summary>
public sealed record AudioCaptureOptions
{
    /// <summary>The defaults: 48 kHz stereo 32-bit float, linked by the session manager.</summary>
    public static AudioCaptureOptions Default { get; } = new();

    /// <summary>The sample rate asked for, in hertz.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>The channel count asked for.</summary>
    public int Channels { get; init; } = 2;

    /// <summary>The sample format asked for.</summary>
    public AudioSampleFormat Format { get; init; } = AudioSampleFormat.F32Le;

    /// <summary>Whether the stream stays on its target instead of moving with the default device.</summary>
    public bool StayWithTheSource { get; init; }

    /// <summary>Whether buffers are dequeued by the caller instead of delivered on the data loop.</summary>
    public bool PullMode { get; init; }

    /// <summary>Whether the session manager links the stream; false leaves the links to the caller.</summary>
    public bool AutoConnect { get; init; } = true;
}

/// <summary>How a video capture stream connects and what it asks for.</summary>
public sealed record VideoCaptureOptions
{
    /// <summary>The defaults: any format, 1920x1080 at 30 fps preferred, linked by the session manager.</summary>
    public static VideoCaptureOptions Default { get; } = new();

    /// <summary>The pixel formats asked for, most preferred first; empty for every supported one.</summary>
    public ImmutableArray<PixelFormat> PreferredFormats { get; init; } = [];

    /// <summary>The DRM modifiers accepted for DMA-BUF frames; empty for memory frames only.</summary>
    public ImmutableArray<long> Modifiers { get; init; } = [];

    /// <summary>
    /// Per-device DMA-BUF offers (device id negotiation, PipeWire 1.2); empty to offer
    /// <see cref="Modifiers"/> without a device.
    /// </summary>
    public ImmutableArray<DmaBufDeviceOffer> DeviceOffers { get; init; } = [];

    /// <summary>The preferred width.</summary>
    public int PreferredWidth { get; init; } = 1920;

    /// <summary>The preferred height.</summary>
    public int PreferredHeight { get; init; } = 1080;

    /// <summary>The preferred frame rate.</summary>
    public int PreferredFrameRate { get; init; } = 30;

    /// <summary>Whether DMA-BUF frames carry explicit sync timelines.</summary>
    public bool RequestExplicitSync { get; init; }

    /// <summary>Whether the stream stays on its target instead of moving with the default device.</summary>
    public bool StayWithTheSource { get; init; }

    /// <summary>Whether buffers are dequeued by the caller instead of delivered on the data loop.</summary>
    public bool PullMode { get; init; }

    /// <summary>Whether the session manager links the stream; false leaves the links to the caller.</summary>
    public bool AutoConnect { get; init; } = true;
}
