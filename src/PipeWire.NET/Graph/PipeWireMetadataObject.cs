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
    /// <param name="Id">PipeWire global id.</param>
    /// <param name="Permissions">What this client may do with the object.</param>
    /// <param name="InterfaceVersion">The interface version the daemon announced.</param>
    /// <param name="MetadataName">Which store this is, such as <c>default</c> or <c>settings</c>.</param>
    internal PipeWireMetadataObject(
        uint Id,
        PipeWirePermissions Permissions,
        uint InterfaceVersion,
        string? MetadataName)
    {
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

    /// <summary>Which store this is, such as <c>default</c> or <c>settings</c>.</summary>
    public string? MetadataName { get; }
}
