namespace PipeWire.NET.Media;

/// <summary>Color primaries / gamut (mirrors <c>spa_video_color_primaries</c>).</summary>
public enum VideoColorPrimaries
{
    /// <summary>Unspecified.</summary>
    Unknown = 0,
    /// <summary>ITU-R BT.709 (Rec.709 / sRGB gamut).</summary>
    Bt709 = 1,
    /// <summary>ITU-R BT.2020 (wide gamut).</summary>
    Bt2020 = 2,
}
