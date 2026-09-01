using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>Short aliases for the generated <see cref="spa_param_type"/> enum.</summary>
internal static class SpaParam
{
    internal const uint Invalid    = (uint)spa_param_type.SPA_PARAM_Invalid;
    internal const uint PropInfo   = (uint)spa_param_type.SPA_PARAM_PropInfo;
    internal const uint Props      = (uint)spa_param_type.SPA_PARAM_Props;
    internal const uint EnumFormat = (uint)spa_param_type.SPA_PARAM_EnumFormat;
    internal const uint Format     = (uint)spa_param_type.SPA_PARAM_Format;
    internal const uint Buffers    = (uint)spa_param_type.SPA_PARAM_Buffers;
    internal const uint Meta       = (uint)spa_param_type.SPA_PARAM_Meta;
    internal const uint IO         = (uint)spa_param_type.SPA_PARAM_IO;
    internal const uint Latency    = (uint)spa_param_type.SPA_PARAM_Latency;
}
