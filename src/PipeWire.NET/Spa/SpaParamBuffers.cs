using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>Property keys of a ParamBuffers object (spa_param_buffers).</summary>
internal static class SpaParamBuffers
{
    internal const uint Buffers  = (uint)spa_param_buffers.SPA_PARAM_BUFFERS_buffers;
    internal const uint Blocks   = (uint)spa_param_buffers.SPA_PARAM_BUFFERS_blocks;
    internal const uint Size     = (uint)spa_param_buffers.SPA_PARAM_BUFFERS_size;
    internal const uint Stride   = (uint)spa_param_buffers.SPA_PARAM_BUFFERS_stride;
    internal const uint Align    = (uint)spa_param_buffers.SPA_PARAM_BUFFERS_align;
    internal const uint DataType = (uint)spa_param_buffers.SPA_PARAM_BUFFERS_dataType;
}
