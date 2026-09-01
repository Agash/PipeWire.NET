using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>
/// Flags for <c>spa_data.flags</c> (spa/buffer/buffer.h). A dmabuf a producer hands over for a
/// consumer to read is marked <see cref="Readable"/>.
/// </summary>
internal static class SpaDataFlag
{
    internal const uint Readable    = 1u << 0;
    internal const uint Writable    = 1u << 1;
    internal const uint Dynamic     = 1u << 2;
    internal const uint ReadWrite   = Readable | Writable;
}
