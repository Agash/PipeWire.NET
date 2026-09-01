namespace PipeWire.NET.Media;

/// <summary>Opto-electronic transfer characteristic (mirrors <c>spa_video_transfer_function</c>).</summary>
public enum VideoTransferFunction
{
    /// <summary>Unspecified.</summary>
    Unknown = 0,
    /// <summary>Pure gamma 2.2.</summary>
    Gamma22 = 1,
    /// <summary>ITU-R BT.709.</summary>
    Bt709 = 2,
    /// <summary>sRGB.</summary>
    Srgb = 3,
    /// <summary>BT.2020 12-bit.</summary>
    Bt2020_12 = 4,
}
