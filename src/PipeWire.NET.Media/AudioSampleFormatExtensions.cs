namespace PipeWire.NET.Media;

/// <summary>Extensions for <see cref="AudioSampleFormat"/>.</summary>
public static class AudioSampleFormatExtensions
{
    /// <summary>Bytes per sample (per channel).</summary>
    public static int BytesPerSample(this AudioSampleFormat fmt) => fmt switch
    {
        AudioSampleFormat.U8    => 1,
        AudioSampleFormat.S16Le => 2,
        AudioSampleFormat.S24Le => 3,
        AudioSampleFormat.S32Le => 4,
        AudioSampleFormat.F32Le => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(fmt)),
    };
}
