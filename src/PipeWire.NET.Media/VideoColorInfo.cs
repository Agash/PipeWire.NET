namespace PipeWire.NET.Media;

/// <summary>
/// Color metadata for a video frame, parsed from the negotiated SPA format.
/// </summary>
/// <param name="Range">Quantization range (full vs limited).</param>
/// <param name="Matrix">YUV-to-RGB matrix coefficients.</param>
/// <param name="Transfer">Transfer characteristic (gamma curve).</param>
/// <param name="Primaries">Color primaries / gamut.</param>
/// <param name="ChromaSite">Where chroma samples sit, for subsampled YUV.</param>
public readonly record struct VideoColorInfo(
    VideoColorRange Range,
    VideoColorMatrix Matrix,
    VideoTransferFunction Transfer,
    VideoColorPrimaries Primaries,
    VideoChromaSite ChromaSite = VideoChromaSite.Unknown
)
{
    /// <summary>An all-unknown color info (source did not report color metadata).</summary>
    public static VideoColorInfo Unknown => default;
}
