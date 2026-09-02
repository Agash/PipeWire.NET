using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// A module loaded into the daemon.
/// </summary>
/// <remarks>
/// Introspection only. Modules are what provide factories and protocols, so this answers "why is
/// that factory here" and little else.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed record PipeWireModule : IPipeWireObject
{
    /// <param name="Id">PipeWire global id.</param>
    /// <param name="Permissions">What this client may do with the object.</param>
    /// <param name="InterfaceVersion">The interface version the daemon announced.</param>
    /// <param name="ModuleName">The library name, such as <c>libpipewire-module-protocol-native</c>.</param>
    /// <param name="Description">What the module says it does.</param>
    /// <param name="Author">Who wrote it.</param>
    /// <param name="ModuleVersion">The version it reports, which need not match the daemon.</param>
    internal PipeWireModule(
        uint Id,
        PipeWirePermissions Permissions,
        uint InterfaceVersion,
        string? ModuleName,
        string? Description,
        string? Author,
        string? ModuleVersion)
    {
        this.Id = Id;
        this.Permissions = Permissions;
        this.InterfaceVersion = InterfaceVersion;
        this.ModuleName = ModuleName;
        this.Description = Description;
        this.Author = Author;
        this.ModuleVersion = ModuleVersion;
    }

    /// <inheritdoc/>
    public uint Id { get; }

    /// <inheritdoc/>
    public PipeWireObjectKind Kind => PipeWireObjectKind.Module;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint InterfaceVersion { get; }

    /// <summary>The library name, such as <c>libpipewire-module-protocol-native</c>.</summary>
    public string? ModuleName { get; }

    /// <summary>What the module says it does.</summary>
    public string? Description { get; }

    /// <summary>Who wrote it.</summary>
    public string? Author { get; }

    /// <summary>The version it reports, which need not match the daemon.</summary>
    public string? ModuleVersion { get; }
}
