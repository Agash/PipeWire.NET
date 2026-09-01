using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>Short aliases for the generated <see cref="spa_format"/> enum (object-property keys).</summary>
internal static class SpaFormatVideo
{
    internal const uint MediaType    = (uint)spa_format.SPA_FORMAT_mediaType;
    internal const uint MediaSubtype = (uint)spa_format.SPA_FORMAT_mediaSubtype;
    internal const uint Format       = (uint)spa_format.SPA_FORMAT_VIDEO_format;
    internal const uint Size         = (uint)spa_format.SPA_FORMAT_VIDEO_size;
    internal const uint Framerate    = (uint)spa_format.SPA_FORMAT_VIDEO_framerate;
    internal const uint Modifier     = (uint)spa_format.SPA_FORMAT_VIDEO_modifier;
    internal const uint ColorRange       = (uint)spa_format.SPA_FORMAT_VIDEO_colorRange;
    internal const uint ColorMatrix      = (uint)spa_format.SPA_FORMAT_VIDEO_colorMatrix;
    internal const uint TransferFunction = (uint)spa_format.SPA_FORMAT_VIDEO_transferFunction;
    internal const uint ColorPrimaries   = (uint)spa_format.SPA_FORMAT_VIDEO_colorPrimaries;
}
