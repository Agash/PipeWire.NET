namespace PipeWire.NET.Media;

/// <summary>YUV-to-RGB color matrix coefficients (mirrors <c>SpaVideoColorMatrix</c>).</summary>
public enum VideoColorMatrix
{
    /// <summary>Unspecified.</summary>
    Unknown = 0,
    /// <summary>Identity (RGB).</summary>
    Rgb = 1,
    /// <summary>ITU-R BT.709 (HD).</summary>
    Bt709 = 2,
    /// <summary>ITU-R BT.601 (SD).</summary>
    Bt601 = 3,
    /// <summary>ITU-R BT.2020 (UHD / wide gamut).</summary>
    Bt2020 = 4,
}
