namespace PipeWire.NET.Media;

/// <summary>Opto-electronic transfer characteristic (mirrors <c>SpaVideoTransferFunction</c>).</summary>
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

    /// <summary>ITU-R BT.601.</summary>
    Bt601 = 5,

    /// <summary>SMPTE ST 2084, the PQ curve of HDR10.</summary>
    Pq = 6,

    /// <summary>ARIB STD-B67, hybrid log-gamma.</summary>
    Hlg = 7,

    /// <summary>Linear light (gamma 1.0).</summary>
    Linear = 8,
}
