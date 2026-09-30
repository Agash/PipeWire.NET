namespace PipeWire.NET.Media;

/// <summary>
/// Where chroma samples sit relative to luma in subsampled YUV (mirrors <c>SpaVideoChromaSite</c>).
/// A combination of flags: cosited horizontally, vertically, or both.
/// </summary>
[Flags]
public enum VideoChromaSite
{
    /// <summary>Unspecified.</summary>
    Unknown = 0,

    /// <summary>Centred between luma samples in both directions, as JPEG places it.</summary>
    None = 1 << 0,

    /// <summary>Cosited with luma horizontally, centred vertically: the MPEG-2, H.264 and H.265 default.</summary>
    HCosited = 1 << 1,

    /// <summary>Cosited with luma vertically.</summary>
    VCosited = 1 << 2,

    /// <summary>Chroma on alternate lines.</summary>
    AltLine = 1 << 3,

    /// <summary>Cosited in both directions, on the top-left luma sample, as BT.2020 places it.</summary>
    Cosited = HCosited | VCosited,
}
