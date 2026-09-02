using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// A security context: a restricted way for a sandboxed application to connect.
/// </summary>
/// <remarks>
/// Created by a sandbox manager, not by ordinary clients. Its presence says the daemon can hand
/// out restricted sockets, which is how a Flatpak gets a connection with fewer permissions than
/// the user running it.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed record PipeWireSecurityContext : IPipeWireObject
{
    /// <param name="Id">PipeWire global id.</param>
    /// <param name="Permissions">What this client may do with the object.</param>
    /// <param name="InterfaceVersion">The interface version the daemon announced.</param>
    internal PipeWireSecurityContext(
        uint Id,
        PipeWirePermissions Permissions,
        uint InterfaceVersion)
    {
        this.Id = Id;
        this.Permissions = Permissions;
        this.InterfaceVersion = InterfaceVersion;
    }

    /// <inheritdoc/>
    public uint Id { get; }

    /// <inheritdoc/>
    public PipeWireObjectKind Kind => PipeWireObjectKind.SecurityContext;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint InterfaceVersion { get; }

}
