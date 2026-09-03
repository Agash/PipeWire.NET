using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// A port bound for reading its parameters.
/// </summary>
/// <remarks>
/// <para>
/// The registry reports that a port exists, its direction and which node it belongs to. What it
/// carries is a different question: the format actually negotiated on it, and the latency the graph
/// has settled on for it. Both live on the port's own params, which need the proxy bound.
/// </para>
/// <para>
/// Read-only, because the interface is. A port has <c>enum_params</c> and <c>subscribe_params</c>
/// and no <c>set_param</c>: a port's format is decided by the negotiation between the nodes at
/// either end, not by a third party reaching in to set it. The inherited
/// <see cref="PipeWireParameterObject.SetParameterAsync"/> therefore fails with ENOTSUP rather
/// than reaching the daemon. Set the parameter on the node instead.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWirePortControl : PipeWireParameterObject
{
    private readonly ILogger _logger;

    private PipeWirePortControl(PipeWireContext ctx, uint id, ILogger logger)
        : base(ctx, id) => _logger = logger;

    internal static unsafe PipeWirePortControl Bind(
        PipeWireContext ctx, pw_registry* registry, uint id, uint version, ILogger logger)
    {
        var control = new PipeWirePortControl(ctx, id, logger);
        control.Attach(BoundProxy.Bind(
            ctx, registry, id, Native.PW_TYPE_INTERFACE_PORT, version, Native.PW_VERSION_PORT,
            sizeof(pw_port_events),
            events =>
            {
                var table = (pw_port_events*)events;
                table->version = Native.PW_VERSION_PORT_EVENTS;
                table->info = &OnInfoCallback;
                table->param = &OnParamCallback;
            },
            static (proxy, hook, events, data) => Native.pw_port_add_listener(
                (pw_port*)proxy, (spa_hook*)hook, (pw_port_events*)events, (void*)data),
            control));

        return control;
    }

    /// <summary>The format currently negotiated on this port, or none if nothing is negotiated.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// Empty is a real answer rather than a failure: a port that nothing is linked to has no
    /// negotiated format, and neither does one whose link is still negotiating.
    /// </remarks>
    public Task<ImmutableArray<SpaObject>> EnumerateFormatsAsync(
        CancellationToken cancellationToken = default) =>
        EnumerateParametersAsync(SpaParamType.Format, cancellationToken);

    /// <summary>Every format this port is willing to accept.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// What the port offers, as opposed to what it settled on. A caller deciding whether two ports
    /// can be linked at all wants this; one asking what is flowing wants
    /// <see cref="EnumerateFormatsAsync"/>.
    /// </remarks>
    public Task<ImmutableArray<SpaObject>> EnumerateSupportedFormatsAsync(
        CancellationToken cancellationToken = default) =>
        EnumerateParametersAsync(SpaParamType.EnumFormat, cancellationToken);

    /// <summary>The latency the graph has settled on for this port, in both directions.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// Reported per direction, so a full path's latency is the sum along it rather than any single
    /// port's figure.
    /// </remarks>
    public Task<ImmutableArray<SpaObject>> EnumerateLatencyAsync(
        CancellationToken cancellationToken = default) =>
        EnumerateParametersAsync(SpaParamType.Latency, cancellationToken);

    /// <summary>The settled latency of this port, read as a typed value per direction.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// The same parameter as <see cref="EnumerateLatencyAsync"/>, read into
    /// <see cref="PipeWireLatency"/>. An object that is not a latency pod is skipped rather than
    /// failing the read: the enumeration reports what the port has, and a port is entitled to hold
    /// something this version does not model.
    /// </remarks>
    public async Task<ImmutableArray<PipeWireLatency>> GetLatenciesAsync(
        CancellationToken cancellationToken = default)
    {
        ImmutableArray<SpaObject> raw =
            await EnumerateLatencyAsync(cancellationToken).ConfigureAwait(false);

        var latencies = ImmutableArray.CreateBuilder<PipeWireLatency>(raw.Length);
        foreach (SpaObject param in raw)
        {
            if (PipeWireLatency.From(param) is { } latency) latencies.Add(latency);
        }

        return latencies.ToImmutable();
    }

    /// <summary>The metadata travelling through this port, per direction.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public async Task<ImmutableArray<PipeWireTag>> GetTagsAsync(
        CancellationToken cancellationToken = default)
    {
        ImmutableArray<SpaObject> raw =
            await EnumerateParametersAsync(SpaParamType.Tag, cancellationToken).ConfigureAwait(false);

        var tags = ImmutableArray.CreateBuilder<PipeWireTag>(raw.Length);
        foreach (SpaObject param in raw)
        {
            if (PipeWireTag.From(param) is { } tag) tags.Add(tag);
        }

        return tags.ToImmutable();
    }

    private protected override unsafe int EnumParamsNative(void* proxy, int seq, uint id, uint start, uint num) =>
        Native.pw_port_enum_params((pw_port*)proxy, seq, id, start, num, null);

    /// <remarks>
    /// ENOTSUP, not a call. <c>pw_port_methods</c> has no <c>set_param</c>: a port's format is
    /// decided by the negotiation between the nodes at either end, so there is nothing to send and
    /// the base class reports the refusal through its ordinary failure path. Inheriting
    /// <c>SetParameterAsync</c> and hiding it would be worse, because a caller holding the base
    /// type would walk straight past the override.
    /// </remarks>
    private protected override unsafe int SetParamNative(void* proxy, uint id, uint flags, spa_pod* param) =>
        -95;

    private protected override unsafe int SubscribeParamsNative(void* proxy, uint* ids, uint count) =>
        Native.pw_port_subscribe_params((pw_port*)proxy, ids, count);

    private protected override void OnHandlerFaulted(Exception exception) => LogHandlerFaulted(exception);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnInfoCallback(void* data, pw_port_info* info)
    {
        // An exception escaping a reverse P/Invoke aborts the process, so nothing here may throw.
        try
        {
            if (info is not null)
                FromUserData<PipeWirePortControl>(data)?.OnInfo(info->@params, info->n_params);
        }
        catch
        {
            // Deliberately not logged: the instance the logger belongs to is what failed to resolve.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnParamCallback(
        void* data, int seq, uint id, uint index, uint next, spa_pod* param)
    {
        try
        {
            FromUserData<PipeWirePortControl>(data)?.OnParam(seq, param);
        }
        catch
        {
            // Deliberately not logged: the instance the logger belongs to is what failed to resolve.
        }
    }

    [LoggerMessage(EventId = 34400, Level = LogLevel.Warning, Message = "a port parameter handler threw")]
    private partial void LogHandlerFaulted(Exception exception);
}
