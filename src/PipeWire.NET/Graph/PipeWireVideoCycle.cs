using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// The frame geometry the graph is running this cycle, for a node carrying video.
/// </summary>
/// <remarks>
/// <para>
/// A filter's video port does not negotiate a size. Upstream's <c>video-dsp-play</c> reads
/// <c>position-&gt;video.size</c> every cycle for that reason: the geometry belongs to the graph,
/// which can change it between cycles, and a port that cached it would read the wrong number of
/// pixels the moment it did.
/// </para>
/// <para>
/// <see cref="IsValid"/> is the graph's own flag. It is false when nothing in the driver group has
/// published a video size, in which case the other fields are meaningless rather than zero-sized.
/// </para>
/// </remarks>
/// <param name="IsValid">Whether the graph has published a size at all this cycle.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Stride">Row stride in bytes.</param>
/// <param name="FrameRateNum">Numerator of the minimum frame rate.</param>
/// <param name="FrameRateDen">Denominator of the minimum frame rate.</param>
[SupportedOSPlatform("linux")]
public readonly record struct PipeWireVideoCycle(
    bool IsValid,
    uint Width,
    uint Height,
    uint Stride,
    uint FrameRateNum,
    uint FrameRateDen
);
