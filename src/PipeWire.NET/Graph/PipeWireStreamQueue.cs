using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

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
