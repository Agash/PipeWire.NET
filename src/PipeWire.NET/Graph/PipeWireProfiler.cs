using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// The daemon profiler, which reports graph timing.
/// </summary>
/// <remarks>
/// One per daemon, and it carries no properties. It exists to be bound to for the profiling data
/// it streams, which is how xruns and driver timing are measured.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed record PipeWireProfiler : IPipeWireObject
{
    /// <param name="Id">PipeWire global id.</param>
    /// <param name="Permissions">What this client may do with the object.</param>
    /// <param name="InterfaceVersion">The interface version the daemon announced.</param>
    internal PipeWireProfiler(
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
    public PipeWireObjectKind Kind => PipeWireObjectKind.Profiler;

    /// <inheritdoc/>
    public PipeWirePermissions Permissions { get; }

    /// <inheritdoc/>
    public uint InterfaceVersion { get; }

}
