using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>Short aliases for the generated <see cref="spa_media_subtype"/> enum.</summary>
internal static class SpaMediaSubtype
{
    internal const uint Unknown = (uint)spa_media_subtype.SPA_MEDIA_SUBTYPE_unknown;
    internal const uint Raw     = (uint)spa_media_subtype.SPA_MEDIA_SUBTYPE_raw;
    internal const uint Dsp     = (uint)spa_media_subtype.SPA_MEDIA_SUBTYPE_dsp;
}
