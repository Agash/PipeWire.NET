namespace PipeWire.NET.Spa;

/// <summary>
/// The property keys the graph layer reads and writes, as UTF-8.
/// </summary>
/// <remarks>
/// <para>
/// <c>Native.PW_KEY_*</c> holds the same names as <see cref="string"/>, which cannot reach a
/// <c>spa_dict</c> without transcoding. These are <c>u8</c> literals, so they compile to a span over
/// static data - no allocation, no marshalling - and each key is spelled once rather than repeated
/// at every parse and creation site.
/// </para>
/// <para>
/// A <c>u8</c> literal's representation is NUL-terminated beyond its logical length, so taking a
/// pointer to one already satisfies libpipewire's C-string contract.
/// </para>
/// </remarks>
internal static class PipeWireKeys
{
    public static ReadOnlySpan<byte> FactoryName => "factory.name"u8;
    public static ReadOnlySpan<byte> ObjectLinger => "object.linger"u8;

    public static ReadOnlySpan<byte> NodeId => "node.id"u8;
    public static ReadOnlySpan<byte> NodeName => "node.name"u8;
    public static ReadOnlySpan<byte> NodeNick => "node.nick"u8;
    public static ReadOnlySpan<byte> NodeDescription => "node.description"u8;
    public static ReadOnlySpan<byte> MediaClass => "media.class"u8;
    public static ReadOnlySpan<byte> AudioPosition => "audio.position"u8;

    public static ReadOnlySpan<byte> PortName => "port.name"u8;
    public static ReadOnlySpan<byte> PortDirection => "port.direction"u8;
    public static ReadOnlySpan<byte> PortMonitor => "port.monitor"u8;
    public static ReadOnlySpan<byte> PortExclusive => "port.exclusive"u8;

    public static ReadOnlySpan<byte> LinkOutputNode => "link.output.node"u8;
    public static ReadOnlySpan<byte> LinkOutputPort => "link.output.port"u8;
    public static ReadOnlySpan<byte> LinkInputNode => "link.input.node"u8;
    public static ReadOnlySpan<byte> LinkInputPort => "link.input.port"u8;
    public static ReadOnlySpan<byte> LinkPassive => "link.passive"u8;

    // - Values -

    public static ReadOnlySpan<byte> True => "true"u8;
    public static ReadOnlySpan<byte> NullAudioSink => "support.null-audio-sink"u8;
    public static ReadOnlySpan<byte> Adapter => "adapter"u8;
    public static ReadOnlySpan<byte> LinkFactory => "link-factory"u8;
    public static ReadOnlySpan<byte> AudioSink => "Audio/Sink"u8;
    public static ReadOnlySpan<byte> StereoPosition => "[ FL FR ]"u8;

    public static ReadOnlySpan<byte> InterfaceNode => "PipeWire:Interface:Node"u8;
    public static ReadOnlySpan<byte> InterfacePort => "PipeWire:Interface:Port"u8;
    public static ReadOnlySpan<byte> InterfaceLink => "PipeWire:Interface:Link"u8;

    public static ReadOnlySpan<byte> DirectionIn => "in"u8;
    public static ReadOnlySpan<byte> DirectionOut => "out"u8;
    public static ReadOnlySpan<byte> DirectionControl => "control"u8;
    public static ReadOnlySpan<byte> DirectionNotify => "notify"u8;
}
