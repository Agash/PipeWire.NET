using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// The daemon core itself, always global id 0.
/// </summary>
/// <remarks>
/// Always present, and the object every other one hangs off. Carries the daemon name and version,
/// which is what to check before using anything version-gated.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed record PipeWireCoreObject : IPipeWireObject
{
    /// <param name="Id">PipeWire global id.</param>
    /// <param name="Permissions">What this client may do with the object.</param>
    /// <param name="InterfaceVersion">The interface version the daemon announced.</param>
    /// <param name="CoreName">The daemon instance name, such as <c>pipewire-0</c>.</param>
    /// <param name="CoreVersion">The daemon version string.</param>
    /// <param name="HostName">The host the daemon runs on.</param>
    /// <param name="UserName">The user it runs as.</param>
    internal PipeWireCoreObject(
        uint Id,
        PipeWirePermissions Permissions,
        uint InterfaceVersion,
        string? CoreName,
        string? CoreVersion,
        string? HostName,
        string? UserName)
    {
        this.Id = Id;
        this.Permissions = Permissions;
        this.InterfaceVersion = InterfaceVersion;
        this.CoreName = CoreName;
        this.CoreVersion = CoreVersion;
        this.HostName = HostName;
        this.UserName = UserName;
    }

    /// <inheritdoc/>
    public uint Id { get; }

    /// <inheritdoc/>
    public PipeWireObjectKind Kind => PipeWireObjectKind.Core;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint InterfaceVersion { get; }

    /// <summary>The daemon instance name, such as <c>pipewire-0</c>.</summary>
    public string? CoreName { get; }

    /// <summary>The daemon version string.</summary>
    public string? CoreVersion { get; }

    /// <summary>The host the daemon runs on.</summary>
    public string? HostName { get; }

    /// <summary>The user it runs as.</summary>
    public string? UserName { get; }
}
