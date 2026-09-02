namespace PipeWire.NET.Media;

/// <summary>Color quantization range (mirrors <c>SpaVideoColorRange</c>).</summary>
public enum VideoColorRange
{
    /// <summary>Unspecified.</summary>
    Unknown = 0,
    /// <summary>Full range, 0-255 ("PC" / JPEG range).</summary>
    Full_0_255 = 1,
    /// <summary>Limited range, 16-235 ("TV" / studio range).</summary>
    Limited_16_235 = 2,
}
