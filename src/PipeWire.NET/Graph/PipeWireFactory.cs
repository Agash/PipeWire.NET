using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// A factory: something the daemon can create objects with.
/// </summary>
/// <remarks>
/// Creating a node or a link names a factory as a string, and this is how to discover which names
/// the daemon being talked to actually has rather than assuming the usual ones are present.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed record PipeWireFactory : IPipeWireObject
{
    internal PipeWireFactory(
        uint Id,
        PipeWirePermissions Permissions,
        uint InterfaceVersion,
        string? FactoryName,
        string? TypeName,
        uint? TypeVersion,
        uint? ModuleId,
        PipeWireProperties? Properties = null)
    {
        this.Properties = Properties ?? PipeWireProperties.Empty;
        this.ObjectSerial = this.Properties.Serial;
        this.Id = Id;
        this.Permissions = Permissions;
        this.InterfaceVersion = InterfaceVersion;
        this.FactoryName = FactoryName;
        this.TypeName = TypeName;
        this.TypeVersion = TypeVersion;
        this.ModuleId = ModuleId;
    }

    /// <inheritdoc/>
    public uint Id { get; }

    /// <inheritdoc/>
    public PipeWireObjectKind Kind => PipeWireObjectKind.Factory;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint InterfaceVersion { get; }

    /// <inheritdoc/>
    public PipeWireProperties Properties { get; }

    /// <inheritdoc/>
    public ulong? ObjectSerial { get; }

    /// <summary>The name to pass when creating an object, such as <c>adapter</c> or <c>link-factory</c>.</summary>
    public string? FactoryName { get; }

    /// <summary>The interface it produces, such as <c>PipeWire:Interface:Node</c>.</summary>
    public string? TypeName { get; }

    /// <summary>The version of that interface it produces.</summary>
    public uint? TypeVersion { get; }

    /// <summary>The module that registered it, where the daemon said.</summary>
    public uint? ModuleId { get; }
}
