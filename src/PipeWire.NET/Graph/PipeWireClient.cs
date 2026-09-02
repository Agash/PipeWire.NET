using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// A connection to the daemon: another application, or this one.
/// </summary>
/// <remarks>
/// <para>
/// What answers "which program owns this stream". A node created by an application carries that
/// client id, so a mixer resolves the name to show beside a stream through here rather than by
/// guessing from the node name.
/// </para>
/// <para>
/// The security fields come from the socket credentials the daemon read at connect time, not from
/// anything the client said about itself - so they can be trusted in a way
/// <see cref="ApplicationName"/> cannot.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed record PipeWireClient : IPipeWireObject
{
    /// <param name="Id">PipeWire global id.</param>
    /// <param name="Permissions">What this client may do with the object.</param>
    /// <param name="InterfaceVersion">The interface version the daemon announced.</param>
    /// <param name="ApplicationName">What the application calls itself. Self-reported, so not an identity to trust.</param>
    /// <param name="ProcessId">The process id the daemon read from the socket.</param>
    /// <param name="UserId">The user id the daemon read from the socket.</param>
    /// <param name="GroupId">The group id the daemon read from the socket.</param>
    /// <param name="Access">How it connected, such as <c>portal</c> or <c>flatpak</c>. Decides what it may see.</param>
    /// <param name="Protocol">The protocol it speaks, normally <c>protocol-native</c>.</param>
    /// <param name="ModuleId">The module serving the connection, where the daemon said.</param>
    internal PipeWireClient(
        uint Id,
        PipeWirePermissions Permissions,
        uint InterfaceVersion,
        string? ApplicationName,
        int? ProcessId,
        uint? UserId,
        uint? GroupId,
        string? Access,
        string? Protocol,
        uint? ModuleId)
    {
        this.Id = Id;
        this.Permissions = Permissions;
        this.InterfaceVersion = InterfaceVersion;
        this.ApplicationName = ApplicationName;
        this.ProcessId = ProcessId;
        this.UserId = UserId;
        this.GroupId = GroupId;
        this.Access = Access;
        this.Protocol = Protocol;
        this.ModuleId = ModuleId;
    }

    /// <inheritdoc/>
    public uint Id { get; }

    /// <inheritdoc/>
    public PipeWireObjectKind Kind => PipeWireObjectKind.Client;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint InterfaceVersion { get; }

    /// <summary>What the application calls itself. Self-reported, so not an identity to trust.</summary>
    public string? ApplicationName { get; }

    /// <summary>The process id the daemon read from the socket.</summary>
    public int? ProcessId { get; }

    /// <summary>The user id the daemon read from the socket.</summary>
    public uint? UserId { get; }

    /// <summary>The group id the daemon read from the socket.</summary>
    public uint? GroupId { get; }

    /// <summary>How it connected, such as <c>portal</c> or <c>flatpak</c>. Decides what it may see.</summary>
    public string? Access { get; }

    /// <summary>The protocol it speaks, normally <c>protocol-native</c>.</summary>
    public string? Protocol { get; }

    /// <summary>The module serving the connection, where the daemon said.</summary>
    public uint? ModuleId { get; }
}
