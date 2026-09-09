using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// A metadata store: a named bag of settings shared between clients.
/// </summary>
/// <remarks>
/// <para>
/// This is the store, not its contents. The <c>default</c> store is where the system default sink
/// and source live, and <c>settings</c> holds the graph clock settings.
/// </para>
/// <para>
/// Reading or writing entries needs the metadata interface bound to this id; the registry reports
/// only that the store exists and what it is called.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed record PipeWireMetadataObject : IPipeWireObject
{
    internal PipeWireMetadataObject(
        uint Id,
        PipeWirePermissions Permissions,
        uint InterfaceVersion,
        string? MetadataName,
        uint? ClientId = null,
        uint? FactoryId = null,
        uint? ModuleId = null,
        PipeWireProperties? Properties = null)
    {
        this.Properties = Properties ?? PipeWireProperties.Empty;
        this.ObjectSerial = this.Properties.Serial;
        this.ClientId = ClientId;
        this.FactoryId = FactoryId;
        this.ModuleId = ModuleId;
        this.Id = Id;
        this.Permissions = Permissions;
        this.InterfaceVersion = InterfaceVersion;
        this.MetadataName = MetadataName;
    }

    /// <inheritdoc/>
    public uint Id { get; }

    /// <inheritdoc/>
    public PipeWireObjectKind Kind => PipeWireObjectKind.Metadata;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint InterfaceVersion { get; }

    /// <inheritdoc/>
    public PipeWireProperties Properties { get; }

    /// <inheritdoc/>
    public ulong? ObjectSerial { get; }

    /// <summary>The client that owns this store.</summary>
    public uint? ClientId { get; }

    /// <summary>The factory that made this store.</summary>
    public uint? FactoryId { get; }

    /// <summary>The module that provides this store.</summary>
    public uint? ModuleId { get; }

    /// <summary>Which store this is, such as <c>default</c> or <c>settings</c>.</summary>
    public string? MetadataName { get; }
}
