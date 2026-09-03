using System.Collections.Immutable;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>A video frame copied out of the stream's buffer, so it can outlive the handler.</summary>
/// <remarks>
/// <see cref="VideoFrame"/> is a <see langword="ref struct"/> over memory the pool recycles, which
/// is what makes the capture path zero-copy and also what stops a frame crossing an
/// <see langword="await"/>, entering a <c>Channel</c>, or being held for A/V alignment. This is the
/// copy, taken deliberately and once, rather than each consumer hand-rolling one.
/// <para>
/// The descriptors are not carried over. A copy outlives the buffer they belong to, so keeping
/// them would keep numbers that name something else by the time anyone looked -
/// <see cref="VideoFrame.DuplicateFd"/> is how a caller keeps one on purpose.
/// </para>
/// </remarks>
/// <param name="Pixels">The frame's bytes.</param>
/// <param name="Stride">Bytes per row.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Format">Negotiated pixel format.</param>
/// <param name="SequenceNumber">The frame index within this session.</param>
/// <param name="Color">Negotiated colour metadata.</param>
/// <param name="PresentationTimeNs">Presentation timestamp, or null if the producer sent none.</param>
/// <param name="CaptureClockNs">Graph clock time of the cycle, or null.</param>
/// <param name="MediaClockNs">Media position at the cycle, or null.</param>
/// <param name="DelayNs">Signal delay between the source and this stream.</param>
public sealed record OwnedVideoFrame(
    ImmutableArray<byte> Pixels,
    int Stride,
    int Width,
    int Height,
    PixelFormat Format,
    ulong SequenceNumber,
    VideoColorInfo Color,
    long? PresentationTimeNs,
    long? CaptureClockNs,
    long? MediaClockNs,
    long DelayNs)
{
    // By content, not by array identity. A record compares its members with
    // EqualityComparer{T}.Default, and for ImmutableArray{T} that compares the wrapped array by
    // reference, so two snapshots of the same bytes would be unequal. A snapshot exists to be
    // held and compared across an await, which is exactly where that is wrong.
    /// <inheritdoc/>
    public bool Equals(OwnedVideoFrame? other) =>
        other is not null
        && SpaValueEquality.SequenceEqual(Pixels, other.Pixels)
        && Stride == other.Stride
        && Width == other.Width
        && Height == other.Height
        && Format == other.Format
        && SequenceNumber == other.SequenceNumber
        && Color.Equals(other.Color)
        && PresentationTimeNs == other.PresentationTimeNs
        && CaptureClockNs == other.CaptureClockNs
        && MediaClockNs == other.MediaClockNs
        && DelayNs == other.DelayNs;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SpaValueEquality.Combine(Pixels));
        hash.Add(Stride);
        hash.Add(Width);
        hash.Add(Height);
        hash.Add(Format);
        hash.Add(SequenceNumber);
        hash.Add(Color);
        hash.Add(PresentationTimeNs);
        hash.Add(CaptureClockNs);
        hash.Add(MediaClockNs);
        hash.Add(DelayNs);
        return hash.ToHashCode();
    }
}

/// <summary>An audio chunk copied out of the stream's buffer, so it can outlive the handler.</summary>
/// <inheritdoc cref="OwnedVideoFrame" path="/remarks"/>
/// <param name="Samples">The interleaved sample bytes.</param>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="Format">Negotiated sample format.</param>
/// <param name="SequenceNumber">The chunk index within this session.</param>
/// <param name="PresentationTimeNs">Presentation timestamp, or null.</param>
/// <param name="CaptureClockNs">Graph clock time of the cycle, or null.</param>
/// <param name="MediaClockNs">Media position at the cycle, or null.</param>
/// <param name="DelayNs">Signal delay between the source and this stream.</param>
public sealed record OwnedAudioFrame(
    ImmutableArray<byte> Samples,
    int SampleRate,
    int Channels,
    AudioSampleFormat Format,
    ulong SequenceNumber,
    long? PresentationTimeNs,
    long? CaptureClockNs,
    long? MediaClockNs,
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
        && PresentationTimeNs == other.PresentationTimeNs
        && CaptureClockNs == other.CaptureClockNs
        && MediaClockNs == other.MediaClockNs
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
        hash.Add(PresentationTimeNs);
        hash.Add(CaptureClockNs);
        hash.Add(MediaClockNs);
        hash.Add(DelayNs);
        return hash.ToHashCode();
    }
}
