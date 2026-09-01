using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>Short aliases for the generated <see cref="spa_media_type"/> enum.</summary>
internal static class SpaMediaType
{
    internal const uint Unknown = (uint)spa_media_type.SPA_MEDIA_TYPE_unknown;
    internal const uint Audio   = (uint)spa_media_type.SPA_MEDIA_TYPE_audio;
    internal const uint Video   = (uint)spa_media_type.SPA_MEDIA_TYPE_video;
    internal const uint Image   = (uint)spa_media_type.SPA_MEDIA_TYPE_image;
    internal const uint Binary  = (uint)spa_media_type.SPA_MEDIA_TYPE_binary;
    internal const uint Stream  = (uint)spa_media_type.SPA_MEDIA_TYPE_stream;
    internal const uint Application = (uint)spa_media_type.SPA_MEDIA_TYPE_application;
}
