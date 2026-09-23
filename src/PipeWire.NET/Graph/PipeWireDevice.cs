using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// A hardware device: a sound card, a camera, a Bluetooth headset.
/// </summary>
/// <remarks>
/// <para>
/// A device is not a node. It is the thing nodes are created <em>from</em>: choosing a profile on
/// a card is what makes its sinks and sources appear in the graph, and switching profiles
/// replaces them. A UI that lets someone pick "Pro Audio" over "Analog Stereo" is talking to a
/// device; one that routes audio between applications is talking to nodes.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed record PipeWireDevice : IPipeWireObject
{
    internal PipeWireDevice(
        uint Id,
        PipeWirePermissions Permissions,
        uint InterfaceVersion,
        string? DeviceName,
        string? Description,
        string? Nick,
        string? Api,
        string? MediaClass,
        string? ObjectPath,
        uint? FactoryId,
        uint? ClientId,
        uint? ModuleId = null,
        PipeWireProperties? Properties = null)
    {
        this.Properties = Properties ?? PipeWireProperties.Empty;
        this.ObjectSerial = this.Properties.Serial;
        this.ModuleId = ModuleId;
        this.Id = Id;
        this.Permissions = Permissions;
        this.InterfaceVersion = InterfaceVersion;
        this.DeviceName = DeviceName;
        this.Description = Description;
        this.Nick = Nick;
        this.Api = Api;
        this.MediaClass = MediaClass;
        this.ObjectPath = ObjectPath;
        this.FactoryId = FactoryId;
        this.ClientId = ClientId;
    }

    /// <inheritdoc/>
    public uint Id { get; }

    /// <inheritdoc/>
    public PipeWireObjectKind Kind => PipeWireObjectKind.Device;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint InterfaceVersion { get; }

    /// <inheritdoc/>
    public PipeWireProperties Properties { get; }

    /// <inheritdoc/>
    public ulong? ObjectSerial { get; }

    /// <summary>The module that provides this device.</summary>
    public uint? ModuleId { get; }

    /// <summary>Stable name, such as <c>alsa_card.pci-0000_e4_00.1</c>.</summary>
    public string? DeviceName { get; }

    /// <summary>Human-readable name as the device reports it.</summary>
    public string? Description { get; }

    /// <summary>Short display name, where the device offers one.</summary>
    public string? Nick { get; }

    /// <summary>Which backend owns it: <c>alsa</c>, <c>v4l2</c>, <c>bluez5</c>, <c>libcamera</c>.</summary>
    public string? Api { get; }

    /// <summary>What it carries, such as <c>Audio/Device</c> or <c>Video/Device</c>.</summary>
    public string? MediaClass { get; }

    /// <summary>The path its backend identifies it by.</summary>
    public string? ObjectPath { get; }

    /// <summary>The factory that created it, where the daemon said.</summary>
    public uint? FactoryId { get; }

    /// <summary>The client that created it, where the daemon said.</summary>
    public uint? ClientId { get; }
}
