using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// What the graph's resampler is doing on this stream's behalf.
/// </summary>
/// <remarks>
/// Present only while something between this stream and the graph is resampling. A transport
/// bridging the graph clock to an external one reads <see cref="Rate"/> and <see cref="Delay"/>
/// to work out how much of its own queue is already in flight, which is the error term its rate
/// correction is computed from.
/// </remarks>
/// <param name="Delay">Frames the resampler is holding.</param>
/// <param name="Size">Frames it will consume this cycle.</param>
/// <param name="Rate">The correction currently applied, 1.0 being none.</param>
/// <param name="Flags">Resampler flags, as the daemon reports them.</param>
[SupportedOSPlatform("linux")]
public readonly record struct PipeWireRateMatch(uint Delay, uint Size, double Rate, uint Flags);
