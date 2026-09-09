using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// Where the graph clock stands, as of the last cycle this stream saw.
/// </summary>
/// <remarks>
/// The daemon updates this every cycle in memory shared with the stream, so a read is a snapshot
/// of a value that is still moving. It is what makes two streams on one connection comparable:
/// they are driven by the same clock, so their <see cref="Position"/> and <see cref="Nsec"/> are
/// on the same timeline. That is the basis for lining audio up with video.
/// </remarks>
/// <param name="Nsec">Monotonic time of the current cycle.</param>
/// <param name="Position">Frames the graph has advanced, on this clock's rate.</param>
/// <param name="Duration">Frames in this cycle - the quantum.</param>
/// <param name="RateNum">Numerator of the clock rate, e.g. 1 for 1/48000.</param>
/// <param name="RateDen">Denominator of the clock rate, e.g. 48000.</param>
/// <param name="Delay">Frames of delay the driver reports.</param>
/// <param name="RateDiff">
/// How fast this clock runs against the monotonic clock. 1.0 is exact; a transport bridging to
/// another clock uses this, or the resampler's own rate, as the drift term.
/// </param>
/// <param name="NextNsec">Monotonic time the next cycle is expected at.</param>
[SupportedOSPlatform("linux")]
public readonly record struct PipeWireGraphClock(
    ulong Nsec,
    ulong Position,
    ulong Duration,
    uint RateNum,
    uint RateDen,
    long Delay,
    double RateDiff,
    ulong NextNsec);

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
