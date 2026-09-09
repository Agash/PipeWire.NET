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
        uint? ModuleId,
        string? SecuritySocket = null,
        string? SecurityLabel = null,
        string? SecurityAppId = null,
        string? SecurityInstanceId = null,
        PipeWireProperties? Properties = null)
    {
        this.Properties = Properties ?? PipeWireProperties.Empty;
        this.ObjectSerial = this.Properties.Serial;
        this.SecuritySocket = SecuritySocket;
        this.SecurityLabel = SecurityLabel;
        this.SecurityAppId = SecurityAppId;
        this.SecurityInstanceId = SecurityInstanceId;
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

    /// <inheritdoc/>
    public PipeWireProperties Properties { get; }

    /// <inheritdoc/>
    public ulong? ObjectSerial { get; }

    /// <summary>Which socket the client connected on (<c>pipewire.sec.socket</c>).</summary>
    public string? SecuritySocket { get; }

    /// <summary>The client's security label (<c>pipewire.sec.label</c>), set by the protocol.</summary>
    public string? SecurityLabel { get; }

    /// <summary>
    /// The sandboxed application's id (<c>pipewire.sec.app-id</c>), set for a client that reached
    /// the daemon through a security context.
    /// </summary>
    /// <remarks>
    /// Present for a Flatpak or portal peer and null for an ordinary one, so it is what tells a
    /// sandboxed client from an unconfined one.
    /// </remarks>
    public string? SecurityAppId { get; }

    /// <summary>The sandbox instance the client belongs to (<c>pipewire.sec.instance-id</c>).</summary>
    public string? SecurityInstanceId { get; }

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
