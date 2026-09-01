using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>Short aliases for the generated <see cref="spa_audio_format"/> enum.</summary>
internal static class SpaAudioFormat
{
    internal const uint Unknown = (uint)spa_audio_format.SPA_AUDIO_FORMAT_UNKNOWN;
    internal const uint U8      = (uint)spa_audio_format.SPA_AUDIO_FORMAT_U8;
    internal const uint S16Le   = (uint)spa_audio_format.SPA_AUDIO_FORMAT_S16_LE;
    internal const uint S24Le   = (uint)spa_audio_format.SPA_AUDIO_FORMAT_S24_LE;
    internal const uint S32Le   = (uint)spa_audio_format.SPA_AUDIO_FORMAT_S32_LE;
    internal const uint F32Le   = (uint)spa_audio_format.SPA_AUDIO_FORMAT_F32_LE;
}
