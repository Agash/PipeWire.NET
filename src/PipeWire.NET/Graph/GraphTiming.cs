using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// Where the graph clock stands, as of the last cycle this stream saw.
/// </summary>
/// <remarks>
/// The daemon updates this every cycle in memory shared with the stream, so a read is a snapshot
/// of a value that is still moving. It is what makes two streams on one connection comparable:
/// they are driven by the same clock, so their <see cref="Position"/> and <see cref="TimeNs"/> are
/// on the same timeline. That is the basis for lining audio up with video.
/// </remarks>
/// <param name="TimeNs">Monotonic time of the current cycle.</param>
/// <param name="Position">Frames the graph has advanced, on this clock's rate.</param>
/// <param name="Duration">Frames in this cycle - the quantum.</param>
/// <param name="RateNum">Numerator of the clock rate, e.g. 1 for 1/48000.</param>
/// <param name="RateDen">Denominator of the clock rate, e.g. 48000.</param>
/// <param name="Delay">Frames of delay the driver reports.</param>
/// <param name="RateDiff">
/// How fast this clock runs against the monotonic clock. 1.0 is exact; a transport bridging to
/// another clock uses this, or the resampler's own rate, as the drift term.
/// </param>
/// <param name="NextTimeNs">Monotonic time the next cycle is expected at.</param>
[SupportedOSPlatform("linux")]
public readonly record struct PipeWireGraphClock(
    ulong TimeNs,
    ulong Position,
    ulong Duration,
    uint RateNum,
    uint RateDen,
    long Delay,
    double RateDiff,
    ulong NextTimeNs);

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
    uint FrameRateDen);

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

/// <summary>
/// How much the stream currently holds, as of the last time it was asked.
/// </summary>
/// <remarks>
/// <para>
/// The occupancy half of the stream's timing, reported alongside the clock by the same call. A rate
/// controller needs an error term, and this is where a stream's own queue depth comes from:
/// upstream's RTP modules read their own ring buffer because they own one, but a consumer built on
/// a stream does not, and asks here instead.
/// </para>
/// <para>
/// <see cref="Queued"/> is only meaningful if the producer sets the size on each buffer it queues.
/// A producer that leaves it zero makes the stream look permanently empty, which reads as a
/// persistent underrun to anything computing a correction from it.
/// </para>
/// </remarks>
/// <param name="Queued">
/// Data queued on the stream and not yet consumed, in the unit the producer used when queueing
/// (frames, for audio).
/// </param>
/// <param name="Buffered">
/// Extra frames an audio stream's resampler is holding (<c>pw_time.buffered</c>); 0 for other media.
/// </param>
/// <param name="QueuedBuffers">Number of buffers currently queued.</param>
/// <param name="AvailableBuffers">Number of buffers available to dequeue.</param>
[SupportedOSPlatform("linux")]
public readonly record struct PipeWireStreamQueue(
    ulong Queued,
    ulong Buffered,
    uint QueuedBuffers,
    uint AvailableBuffers);
