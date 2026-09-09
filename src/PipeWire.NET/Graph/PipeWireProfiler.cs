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
    internal PipeWireProfiler(
        uint Id,
        PipeWirePermissions Permissions,
        uint InterfaceVersion,
        PipeWireProperties? Properties = null)
    {
        this.Properties = Properties ?? PipeWireProperties.Empty;
        this.ObjectSerial = this.Properties.Serial;
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

    /// <inheritdoc/>
    public PipeWireProperties Properties { get; }

    /// <inheritdoc/>
    public ulong? ObjectSerial { get; }

}
