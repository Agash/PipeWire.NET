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

    public static ReadOnlySpan<byte> DeviceName => "device.name"u8;
    public static ReadOnlySpan<byte> DeviceDescription => "device.description"u8;
    public static ReadOnlySpan<byte> DeviceNick => "device.nick"u8;
    public static ReadOnlySpan<byte> DeviceApi => "device.api"u8;
    public static ReadOnlySpan<byte> ObjectPath => "object.path"u8;
    public static ReadOnlySpan<byte> FactoryId => "factory.id"u8;
    public static ReadOnlySpan<byte> ClientId => "client.id"u8;
    public static ReadOnlySpan<byte> ModuleId => "module.id"u8;

    public static ReadOnlySpan<byte> ApplicationName => "application.name"u8;
    public static ReadOnlySpan<byte> SecurityPid => "pipewire.sec.pid"u8;
    public static ReadOnlySpan<byte> SecurityUid => "pipewire.sec.uid"u8;
    public static ReadOnlySpan<byte> SecurityGid => "pipewire.sec.gid"u8;
    public static ReadOnlySpan<byte> Access => "pipewire.access"u8;
    public static ReadOnlySpan<byte> Protocol => "pipewire.protocol"u8;

    public static ReadOnlySpan<byte> FactoryTypeName => "factory.type.name"u8;
    public static ReadOnlySpan<byte> FactoryTypeVersion => "factory.type.version"u8;

    public static ReadOnlySpan<byte> ModuleName => "module.name"u8;
    public static ReadOnlySpan<byte> ModuleDescription => "module.description"u8;
    public static ReadOnlySpan<byte> ModuleAuthor => "module.author"u8;
    public static ReadOnlySpan<byte> ModuleVersion => "module.version"u8;

    public static ReadOnlySpan<byte> MetadataName => "metadata.name"u8;

    public static ReadOnlySpan<byte> CoreName => "core.name"u8;
    public static ReadOnlySpan<byte> CoreVersion => "core.version"u8;
    public static ReadOnlySpan<byte> HostName => "application.process.host"u8;
    public static ReadOnlySpan<byte> UserName => "application.process.user"u8;
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
    public static ReadOnlySpan<byte> InterfaceDevice => "PipeWire:Interface:Device"u8;
    public static ReadOnlySpan<byte> InterfaceClient => "PipeWire:Interface:Client"u8;
    public static ReadOnlySpan<byte> InterfaceFactory => "PipeWire:Interface:Factory"u8;
    public static ReadOnlySpan<byte> InterfaceModule => "PipeWire:Interface:Module"u8;
    public static ReadOnlySpan<byte> InterfaceMetadata => "PipeWire:Interface:Metadata"u8;
    public static ReadOnlySpan<byte> InterfaceProfiler => "PipeWire:Interface:Profiler"u8;
    public static ReadOnlySpan<byte> InterfaceSecurityContext => "PipeWire:Interface:SecurityContext"u8;
    public static ReadOnlySpan<byte> InterfaceCore => "PipeWire:Interface:Core"u8;

    public static ReadOnlySpan<byte> DirectionIn => "in"u8;
    public static ReadOnlySpan<byte> DirectionOut => "out"u8;
    public static ReadOnlySpan<byte> DirectionControl => "control"u8;
    public static ReadOnlySpan<byte> DirectionNotify => "notify"u8;
}
