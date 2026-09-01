using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>Short aliases for the audio side of <see cref="spa_format"/>.</summary>
internal static class SpaFormatAudio
{
    internal const uint Format   = (uint)spa_format.SPA_FORMAT_AUDIO_format;
    internal const uint Rate     = (uint)spa_format.SPA_FORMAT_AUDIO_rate;
    internal const uint Channels = (uint)spa_format.SPA_FORMAT_AUDIO_channels;
}
