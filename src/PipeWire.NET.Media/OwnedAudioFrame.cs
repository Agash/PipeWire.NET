using System.Collections.Immutable;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>An audio chunk copied out of the stream's buffer, so it can outlive the handler.</summary>
/// <inheritdoc cref="OwnedVideoFrame" path="/remarks"/>
/// <param name="Samples">The interleaved sample bytes.</param>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="Format">Negotiated sample format.</param>
/// <param name="SequenceNumber">The chunk index within this session.</param>
/// <param name="PresentationTimestampNs">The header <c>pts</c>, or null when the buffer carried none.</param>
/// <param name="QueuedTimeNs">The cycle time the buffer was queued in (<c>pw_buffer.time</c>), or null.</param>
/// <param name="GraphTimeNs">Graph clock time of the cycle, or null.</param>
/// <param name="StreamPositionNs">Media position at the cycle, or null.</param>
/// <param name="DelayNs">Signal delay between the source and this stream.</param>
public sealed record OwnedAudioFrame(
    ImmutableArray<byte> Samples,
    int SampleRate,
    int Channels,
    AudioSampleFormat Format,
    ulong SequenceNumber,
    long? PresentationTimestampNs,
    long? QueuedTimeNs,
    long? GraphTimeNs,
    long? StreamPositionNs,
    long DelayNs)
{
    // By content, for the same reason as OwnedVideoFrame above.
    /// <inheritdoc/>
    public bool Equals(OwnedAudioFrame? other) =>
        other is not null
        && SpaValueEquality.SequenceEqual(Samples, other.Samples)
        && SampleRate == other.SampleRate
        && Channels == other.Channels
        && Format == other.Format
        && SequenceNumber == other.SequenceNumber
        && PresentationTimestampNs == other.PresentationTimestampNs
        && QueuedTimeNs == other.QueuedTimeNs
        && GraphTimeNs == other.GraphTimeNs
        && StreamPositionNs == other.StreamPositionNs
        && DelayNs == other.DelayNs;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SpaValueEquality.Combine(Samples));
        hash.Add(SampleRate);
        hash.Add(Channels);
        hash.Add(Format);
        hash.Add(SequenceNumber);
        hash.Add(PresentationTimestampNs);
        hash.Add(QueuedTimeNs);
        hash.Add(GraphTimeNs);
        hash.Add(StreamPositionNs);
        hash.Add(DelayNs);
        return hash.ToHashCode();
    }
}
