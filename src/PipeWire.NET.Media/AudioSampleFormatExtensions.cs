namespace PipeWire.NET.Media;

/// <summary>Extensions for <see cref="AudioSampleFormat"/>.</summary>
public static class AudioSampleFormatExtensions
{
    extension(AudioSampleFormat fmt)
    {
        /// <summary>Bytes per sample (per channel), or 0 for a format this library cannot interpret.</summary>
        /// <remarks>
        /// <see cref="AudioSampleFormat.Unknown"/> returns 0 rather than throwing: it arrives from a
        /// negotiation, not from a caller, so a consumer that divides by it should get an obviously
        /// wrong answer at the point of use rather than an exception from a property read.
        /// </remarks>
        public int BytesPerSample() => fmt switch
        {
            AudioSampleFormat.U8        => 1,
            AudioSampleFormat.S16Le     => 2,
            AudioSampleFormat.S24Le     => 3,
            AudioSampleFormat.S32Le     => 4,
            AudioSampleFormat.F32Le     => 4,
            AudioSampleFormat.S24_32Le  => 4,
            AudioSampleFormat.F64Le     => 8,
            AudioSampleFormat.Unknown   => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(fmt)),
        };
    }
}
