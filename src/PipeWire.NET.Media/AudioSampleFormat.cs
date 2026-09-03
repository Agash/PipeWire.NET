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

    /// <summary>24-bit signed integer in a 32-bit container, little-endian, interleaved.</summary>
    /// <remarks>
    /// Distinct from <see cref="S24Le"/>, which packs the same 24 bits into three bytes. Devices
    /// commonly report this one, and reading it as three-byte packed shifts every sample after the
    /// first.
    /// </remarks>
    S24_32Le,

    /// <summary>64-bit IEEE float, interleaved.</summary>
    F64Le,

    /// <summary>
    /// A format this library does not model. The samples are whatever the producer negotiated and
    /// cannot be interpreted without knowing which format that was.
    /// </summary>
    /// <remarks>
    /// Reported rather than guessed. Falling back to <see cref="F32Le"/> would make a consumer read
    /// four-byte floats out of, say, 24-in-32 integers: audio that plays, sounds wrong, and blames the device.
    /// </remarks>
    Unknown,
}
