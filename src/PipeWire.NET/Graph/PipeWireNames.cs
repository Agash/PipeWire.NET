namespace PipeWire.NET.Graph;

/// <summary>
/// The PipeWire property names this library reads, for use against
/// <see cref="IPipeWireObject.Properties"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each is a key from PipeWire's own <c>keys.h</c>. They are here so a caller reading a property
/// this library does not model does not have to spell the key itself and get it silently wrong: a
/// mistyped key reads as an absent property, with nothing to notice.
/// </para>
/// <para>
/// This is not the whole of <c>keys.h</c>. PipeWire's property set is open and modules add their
/// own keys, so a key absent here is a key this library does not read, not a key that cannot exist.
/// </para>
/// </remarks>
public static class PipeWireNames
{
    /// <summary><c>factory.name</c></summary>
    public const string FactoryName = "factory.name";

    /// <summary><c>object.linger</c></summary>
    public const string ObjectLinger = "object.linger";

    /// <summary><c>object.serial</c></summary>
    public const string ObjectSerial = "object.serial";

    /// <summary><c>priority.session</c></summary>
    public const string PrioritySession = "priority.session";

    /// <summary><c>priority.driver</c></summary>
    public const string PriorityDriver = "priority.driver";

    /// <summary><c>node.id</c></summary>
    public const string NodeId = "node.id";

    /// <summary><c>node.name</c></summary>
    public const string NodeName = "node.name";

    /// <summary><c>node.nick</c></summary>
    public const string NodeNick = "node.nick";

    /// <summary><c>node.description</c></summary>
    public const string NodeDescription = "node.description";

    /// <summary><c>media.class</c></summary>
    public const string MediaClass = "media.class";

    /// <summary><c>device.id</c></summary>
    public const string DeviceId = "device.id";

    /// <summary><c>device.name</c></summary>
    public const string DeviceName = "device.name";

    /// <summary><c>device.description</c></summary>
    public const string DeviceDescription = "device.description";

    /// <summary><c>device.nick</c></summary>
    public const string DeviceNick = "device.nick";

    /// <summary><c>device.api</c></summary>
    public const string DeviceApi = "device.api";

    /// <summary><c>object.path</c></summary>
    public const string ObjectPath = "object.path";

    /// <summary><c>factory.id</c></summary>
    public const string FactoryId = "factory.id";

    /// <summary><c>client.id</c></summary>
    public const string ClientId = "client.id";

    /// <summary><c>module.id</c></summary>
    public const string ModuleId = "module.id";

    /// <summary><c>application.name</c></summary>
    public const string ApplicationName = "application.name";

    /// <summary><c>pipewire.sec.pid</c></summary>
    public const string SecurityPid = "pipewire.sec.pid";

    /// <summary><c>pipewire.sec.uid</c></summary>
    public const string SecurityUid = "pipewire.sec.uid";

    /// <summary><c>pipewire.sec.gid</c></summary>
    public const string SecurityGid = "pipewire.sec.gid";

    /// <summary><c>pipewire.sec.socket</c></summary>
    public const string SecuritySocket = "pipewire.sec.socket";

    /// <summary><c>pipewire.sec.label</c></summary>
    public const string SecurityLabel = "pipewire.sec.label";

    /// <summary><c>pipewire.sec.app-id</c></summary>
    public const string SecurityAppId = "pipewire.sec.app-id";

    /// <summary><c>pipewire.sec.instance-id</c></summary>
    public const string SecurityInstanceId = "pipewire.sec.instance-id";

    /// <summary><c>pipewire.access</c></summary>
    public const string Access = "pipewire.access";

    /// <summary><c>pipewire.protocol</c></summary>
    public const string Protocol = "pipewire.protocol";

    /// <summary><c>factory.type.name</c></summary>
    public const string FactoryTypeName = "factory.type.name";

    /// <summary><c>factory.type.version</c></summary>
    public const string FactoryTypeVersion = "factory.type.version";

    /// <summary><c>module.name</c></summary>
    public const string ModuleName = "module.name";

    /// <summary><c>module.filename</c>, from the module's info event rather than its properties.</summary>
    public const string ModuleFilename = "module.filename";

    /// <summary><c>module.args</c>, from the module's info event rather than its properties.</summary>
    public const string ModuleArguments = "module.args";

    /// <summary><c>module.description</c></summary>
    public const string ModuleDescription = "module.description";

    /// <summary><c>module.author</c></summary>
    public const string ModuleAuthor = "module.author";

    /// <summary><c>module.version</c></summary>
    public const string ModuleVersion = "module.version";

    /// <summary><c>metadata.name</c></summary>
    public const string MetadataName = "metadata.name";

    /// <summary><c>core.name</c></summary>
    public const string CoreName = "core.name";

    /// <summary><c>core.version</c></summary>
    public const string CoreVersion = "core.version";

    /// <summary><c>application.process.host</c></summary>
    public const string HostName = "application.process.host";

    /// <summary><c>application.process.user</c></summary>
    public const string UserName = "application.process.user";

    /// <summary><c>audio.position</c></summary>
    public const string AudioPosition = "audio.position";

    /// <summary><c>port.name</c></summary>
    public const string PortName = "port.name";

    /// <summary><c>port.direction</c></summary>
    public const string PortDirection = "port.direction";

    /// <summary><c>port.monitor</c></summary>
    public const string PortMonitor = "port.monitor";

    /// <summary><c>port.id</c></summary>
    public const string PortIndex = "port.id";

    /// <summary><c>port.control</c></summary>
    public const string PortControl = "port.control";

    /// <summary><c>port.physical</c></summary>
    public const string PortPhysical = "port.physical";

    /// <summary><c>port.terminal</c></summary>
    public const string PortTerminal = "port.terminal";

    /// <summary><c>port.alias</c></summary>
    public const string PortAlias = "port.alias";

    /// <summary><c>port.group</c></summary>
    public const string PortGroup = "port.group";

    /// <summary><c>format.dsp</c></summary>
    public const string FormatDsp = "format.dsp";

    /// <summary><c>audio.channel</c></summary>
    public const string AudioChannel = "audio.channel";

    /// <summary><c>link.output.node</c></summary>
    public const string LinkOutputNode = "link.output.node";

    /// <summary><c>link.output.port</c></summary>
    public const string LinkOutputPort = "link.output.port";

    /// <summary><c>link.input.node</c></summary>
    public const string LinkInputNode = "link.input.node";

    /// <summary><c>link.input.port</c></summary>
    public const string LinkInputPort = "link.input.port";

    /// <summary><c>link.passive</c></summary>
    public const string LinkPassive = "link.passive";
}
