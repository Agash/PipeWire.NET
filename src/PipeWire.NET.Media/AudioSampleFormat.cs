namespace PipeWire.NET.Media;

/// <summary>PCM sample formats supported by <see cref="PipeWireAudioCapture"/> / <see cref="PipeWireAudioOutput"/>.</summary>
public enum AudioSampleFormat
{
    /// <summary>32-bit IEEE float, interleaved.</summary>
    F32Le,

    /// <summary>16-bit signed integer, little-endian, interleaved.</summary>
    S16Le,

    /// <summary>24-bit signed integer (3-byte packed), little-endian, interleaved.</summary>
    S24Le,

    /// <summary>32-bit signed integer, little-endian, interleaved.</summary>
    S32Le,

    /// <summary>8-bit unsigned integer, interleaved.</summary>
    U8,
}
