using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// A stream's clock as of the moment it was asked (<c>pw_stream_get_time_n</c>): the graph time, the
/// stream's media position, the latency between it and the hardware, and how much it holds.
/// </summary>
/// <remarks>
/// For a playback stream, a sample written now reaches the device after <see cref="Delay"/> plus
/// whatever is still queued ahead of it, which is what
/// <c>PipeWireAudioOutput.PlaybackLatency</c> adds up. For a capture stream, the delay is how long
/// ago the data now arriving left the device.
/// </remarks>
/// <param name="GraphTimeNs">The graph time of the last cycle, in nanoseconds on CLOCK_MONOTONIC.</param>
/// <param name="StreamPositionNs">The stream's media position in nanoseconds, or -1 when unknown.</param>
/// <param name="Delay">The latency between this stream and the hardware.</param>
/// <param name="Rate">
/// The graph rate the queue and delay are counted in, in ticks per second, or 0 when unknown.
/// </param>
/// <param name="Queue">What the stream holds.</param>
[SupportedOSPlatform("linux")]
public readonly record struct PipeWireStreamTime(
    long GraphTimeNs,
    long StreamPositionNs,
    TimeSpan Delay,
    long Rate,
    PipeWireStreamQueue Queue
);
