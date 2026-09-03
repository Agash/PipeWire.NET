using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// A node bound for reading and writing its parameters: volume, mute, latency, formats.
/// </summary>
/// <remarks>
/// <para>
/// Most of a mixer is here. <see cref="SpaParamType.PropInfo"/> says which controls a node has and
/// what they accept, <see cref="SpaParamType.Props"/> carries their current values, and writing
/// <c>Props</c> is what changes them.
/// </para>
/// <para>
/// Bound proxies are not free - each is a native object and a listener - so bind one when a node is
/// being worked with and dispose it when it is not. The graph snapshot is the right thing to hold
/// for merely watching.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireNodeControl : PipeWireParameterObject
{
    private readonly ILogger _logger;

    private PipeWireNodeControl(PipeWireContext ctx, uint id, ILogger logger)
        : base(ctx, id) => _logger = logger;

    internal static unsafe PipeWireNodeControl Bind(
        PipeWireContext ctx, pw_registry* registry, uint id, uint version, ILogger logger)
    {
        var control = new PipeWireNodeControl(ctx, id, logger);
        control.Attach(BoundProxy.Bind(
            ctx, registry, id, Native.PW_TYPE_INTERFACE_NODE, version, Native.PW_VERSION_NODE,
            sizeof(pw_node_events),
            events =>
            {
                var table = (pw_node_events*)events;
                table->version = Native.PW_VERSION_NODE_EVENTS;
                table->info = &OnInfoCallback;
                table->param = &OnParamCallback;
            },
            static (proxy, hook, events, data) => Native.pw_node_add_listener(
                (pw_node*)proxy, (spa_hook*)hook, (pw_node_events*)events, (void*)data),
            control));

        return control;
    }

    /// <summary>
    /// The node's overall volume, as a linear amplitude, or <see langword="null"/> if it has none.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// Linear, not decibels, and <c>1.0</c> is unity rather than a maximum - a node may accept more.
    /// Read <see cref="SpaParamType.PropInfo"/> for the range it will actually take.
    /// </remarks>
    public async Task<float?> GetVolumeAsync(CancellationToken cancellationToken = default)
    {
        SpaObject? props = await GetParameterAsync(SpaParamType.Props, cancellationToken)
            .ConfigureAwait(false);
        return props?[(uint)SpaProp.Volume] is SpaFloat volume ? volume.Value : null;
    }

    /// <summary>
    /// The node's per-channel volumes, in its channel order, or empty if it has none.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// This is the one a mixer should write. <see cref="GetVolumeAsync"/> is a single scalar applied
    /// on top, and a device with a channel map reports per-channel values whatever that scalar says.
    /// </remarks>
    public async Task<ImmutableArray<float>> GetChannelVolumesAsync(
        CancellationToken cancellationToken = default)
    {
        SpaObject? props = await GetParameterAsync(SpaParamType.Props, cancellationToken)
            .ConfigureAwait(false);
        return ReadFloatArray(props, SpaProp.ChannelVolumes);
    }

    /// <summary>Whether the node is muted, or <see langword="null"/> if it has no mute control.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public async Task<bool?> GetMutedAsync(CancellationToken cancellationToken = default)
    {
        SpaObject? props = await GetParameterAsync(SpaParamType.Props, cancellationToken)
            .ConfigureAwait(false);
        return props?[(uint)SpaProp.Mute] is SpaBool muted ? muted.Value : null;
    }

    /// <summary>Sets the node's overall volume.</summary>
    /// <param name="volume">Linear amplitude; negative values are refused.</param>
    /// <param name="cancellationToken">
    /// Abandons the wait. The request is already on its way, so cancelling does not recall
    /// it: the daemon can still apply the change after this throws.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="volume"/> is negative.</exception>
    /// <remarks>
    /// This is the node's master <c>volume</c> property. Session managers and desktop mixers read
    /// <c>channelVolumes</c> instead, so a change made here is correct but will not show up in
    /// wpctl or pavucontrol. Use <see cref="SetChannelVolumesAsync(ReadOnlySpan{float}, CancellationToken)"/> for the volume a user sees.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="volume"/> is negative or not a number.
    /// </exception>
    public Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        // NaN as well as negative. ThrowIfNegative asks IsNegative, which is false for NaN, so a
        // NaN went into a float pod and reached the daemon as a volume.
        ArgumentOutOfRangeException.ThrowIfNegative(volume);
        if (float.IsNaN(volume))
            throw new ArgumentOutOfRangeException(nameof(volume), volume, "a volume must be a number.");
        return SetParameterAsync(
            SpaParamType.Props,
            new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
                [new SpaProperty(SpaProp.Volume, 0, new SpaFloat(volume))]),
            cancellationToken);
    }

    /// <summary>Sets the node's per-channel volumes.</summary>
    /// <param name="volumes">One linear amplitude per channel, in the node's channel order.</param>
    /// <param name="cancellationToken">
    /// Abandons the wait. The request is already on its way, so cancelling does not recall
    /// it: the daemon can still apply the change after this throws.
    /// </param>
    /// <remarks>
    /// <para>
    /// The count must match the node's channel map, and nothing enforces that. PipeWire stores the
    /// array verbatim at whatever length it is given: send eight volumes to a stereo node and it
    /// reports eight from then on, while its channel map still says two. Nothing errors, and the
    /// volumes are silently out of step with the channels they are supposed to control.
    /// </para>
    /// <para>
    /// <see cref="GetChannelMapAsync"/> is the authority on how many there should be - the map is
    /// the node's own description of its channels, and it does not follow a bad write.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="volumes"/> is empty or holds a negative value.</exception>
    public Task SetChannelVolumesAsync(
        ReadOnlySpan<float> volumes, CancellationToken cancellationToken = default)
    {
        if (volumes.IsEmpty)
            throw new ArgumentException("at least one channel volume is required.", nameof(volumes));

        var values = ImmutableArray.CreateBuilder<SpaValue>(volumes.Length);
        foreach (float volume in volumes)
        {
            if (float.IsNegative(volume) || float.IsNaN(volume))
                throw new ArgumentException("a channel volume must be a non-negative number.", nameof(volumes));
            values.Add(new SpaFloat(volume));
        }

        return SetParameterAsync(
            SpaParamType.Props,
            new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
            [
                new SpaProperty(SpaProp.ChannelVolumes, 0,
                    new SpaArray(SpaType.Float, values.MoveToImmutable())),
            ]),
            cancellationToken);
    }

    /// <summary>Sets the per-channel volumes, checking the count against the node first.</summary>
    /// <param name="volumes">One linear amplitude per channel, in the node's channel order.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="volumes"/> is empty, holds a negative value, or its count matches neither
    /// the node's channel map nor the volumes it currently holds.
    /// </exception>
    /// <param name="matchChannelMap">
    /// Reads the node's channel map first and refuses a count that matches neither it nor the
    /// volumes the node currently holds.
    /// <para>
    /// Deliberately not a hard equality against the map: a pro-audio node legitimately holds fewer
    /// volumes than its map has entries, so refusing on that alone would reject writes the daemon
    /// accepts. Without it a mismatched count is stored verbatim and sticks, which is the daemon's
    /// own behaviour. With it, one extra round trip.
    /// </para>
    /// </param>
    public Task SetChannelVolumesAsync(
        ReadOnlySpan<float> volumes,
        bool matchChannelMap,
        CancellationToken cancellationToken = default)
    {
        float[] copy = volumes.ToArray();
        return matchChannelMap
            ? SetCheckedAsync(copy, cancellationToken)
            : SetChannelVolumesAsync(copy, cancellationToken);
    }

    private async Task SetCheckedAsync(float[] volumes, CancellationToken cancellationToken)
    {
        ImmutableArray<SpaAudioChannel> map =
            await GetChannelMapAsync(cancellationToken).ConfigureAwait(false);
        ImmutableArray<float> current =
            await GetChannelVolumesAsync(cancellationToken).ConfigureAwait(false);

        if (volumes.Length != map.Length && volumes.Length != current.Length)
        {
            throw new ArgumentException(
                $"{volumes.Length} volumes match neither the node's channel map ({map.Length}) "
                + $"nor the {current.Length} it currently holds.",
                nameof(volumes));
        }

        await SetChannelVolumesAsync(volumes, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// The channels this node actually has, in order, or empty if it does not say.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// The authority on how many entries <see cref="SetChannelVolumesAsync(ReadOnlySpan{float}, CancellationToken)"/> should be given.
    /// Unlike the volume array, this reflects the node rather than the last thing written to it.
    /// </remarks>
    public async Task<ImmutableArray<SpaAudioChannel>> GetChannelMapAsync(
        CancellationToken cancellationToken = default)
    {
        SpaObject? props = await GetParameterAsync(SpaParamType.Props, cancellationToken)
            .ConfigureAwait(false);

        if (props?[SpaProp.ChannelMap] is not SpaArray map)
            return [];

        var channels = ImmutableArray.CreateBuilder<SpaAudioChannel>(map.Items.Length);
        foreach (SpaValue item in map.Items)
        {
            if (item is SpaId id) channels.Add((SpaAudioChannel)id.Value);
        }

        return channels.ToImmutable();
    }

    /// <summary>Mutes or unmutes the node.</summary>
    /// <param name="muted">Whether to mute it.</param>
    /// <param name="cancellationToken">
    /// Abandons the wait. The request is already on its way, so cancelling does not recall
    /// it: the daemon can still apply the change after this throws.
    /// </param>
    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        SetParameterAsync(
            SpaParamType.Props,
            new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
                [new SpaProperty(SpaProp.Mute, 0, new SpaBool(muted))]),
            cancellationToken);

    /// <summary>
    /// The extra latency applied to this node, in nanoseconds, or <see langword="null"/> if unset.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public async Task<long?> GetLatencyOffsetAsync(CancellationToken cancellationToken = default)
    {
        SpaObject? props = await GetParameterAsync(SpaParamType.Props, cancellationToken)
            .ConfigureAwait(false);
        return props?[(uint)SpaProp.LatencyOffsetNsec] is SpaLong offset ? offset.Value : null;
    }

    /// <summary>Sets the extra latency applied to this node.</summary>
    /// <param name="nanoseconds">The offset. Negative values pull the node earlier.</param>
    /// <param name="cancellationToken">
    /// Abandons the wait. The request is already on its way, so cancelling does not recall
    /// it: the daemon can still apply the change after this throws.
    /// </param>
    public Task SetLatencyOffsetAsync(long nanoseconds, CancellationToken cancellationToken = default) =>
        SetParameterAsync(
            SpaParamType.Props,
            new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
                [new SpaProperty(SpaProp.LatencyOffsetNsec, 0, new SpaLong(nanoseconds))]),
            cancellationToken);

    /// <summary>The formats this node will accept, as offered for negotiation.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public Task<ImmutableArray<SpaObject>> EnumerateFormatsAsync(
        CancellationToken cancellationToken = default) =>
        EnumerateParametersAsync(SpaParamType.EnumFormat, cancellationToken);

    /// <summary>
    /// Describes every property this node supports: its key, its type and the values it accepts.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// Writing a property a node does not have is silently dropped,
    /// so this says whether a volume slider should be shown.
    /// </remarks>
    public Task<ImmutableArray<SpaObject>> EnumeratePropertyInfoAsync(
        CancellationToken cancellationToken = default) =>
        EnumerateParametersAsync(SpaParamType.PropInfo, cancellationToken);

    internal static ImmutableArray<float> ReadFloatArray(SpaObject? props, SpaProp key)
    {
        if (props?[(uint)key] is not SpaArray array)
            return [];

        var values = ImmutableArray.CreateBuilder<float>(array.Items.Length);
        foreach (SpaValue item in array.Items)
        {
            if (item is SpaFloat value) values.Add(value.Value);
        }

        return values.ToImmutable();
    }

    private protected override unsafe int EnumParamsNative(
        void* proxy, int seq, uint id, uint start, uint num, spa_pod* filter) =>
        Native.pw_node_enum_params((pw_node*)proxy, seq, id, start, num, filter);

    private protected override unsafe int SetParamNative(void* proxy, uint id, uint flags, spa_pod* param) =>
        Native.pw_node_set_param((pw_node*)proxy, id, flags, param);

    private protected override unsafe int SubscribeParamsNative(void* proxy, uint* ids, uint count) =>
        Native.pw_node_subscribe_params((pw_node*)proxy, ids, count);

    private protected override void OnHandlerFaulted(Exception exception) =>
        LogHandlerFaulted(Id, exception);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnInfoCallback(void* data, pw_node_info* info)
    {
        // An exception escaping a reverse P/Invoke aborts the process, so nothing here may throw.
        try
        {
            if (info is not null)
                FromUserData<PipeWireNodeControl>(data)?.OnInfo(info->@params, info->n_params);
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
        // An exception escaping a reverse P/Invoke aborts the process, so nothing here may throw.
        try
        {
            FromUserData<PipeWireNodeControl>(data)?.OnParam(seq, param);
        }
        catch
        {
            // Deliberately not logged: the instance the logger belongs to is what failed to resolve,
            // and there is nothing else to report it through from inside a native callback.
        }
    }

    // ------------------------------------------------------------------ port configuration

    /// <summary>The port layouts this node offers, as opposed to the one it is using.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// Empty on a node that is not an adapter. Only the adapter implements this parameter, so a
    /// plain node reports nothing rather than failing.
    /// </remarks>
    public async Task<ImmutableArray<PipeWirePortConfig>> EnumeratePortConfigsAsync(
        CancellationToken cancellationToken = default)
    {
        ImmutableArray<SpaObject> raw = await EnumerateParametersAsync(
            SpaParamType.EnumPortConfig, cancellationToken).ConfigureAwait(false);

        var configs = ImmutableArray.CreateBuilder<PipeWirePortConfig>(raw.Length);
        foreach (SpaObject param in raw)
        {
            if (PipeWirePortConfig.From(param) is { } config) configs.Add(config);
        }

        return configs.ToImmutable();
    }

    /// <summary>The port layout this node is currently using.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public async Task<PipeWirePortConfig?> GetPortConfigAsync(
        CancellationToken cancellationToken = default) =>
        PipeWirePortConfig.From(
            await GetParameterAsync(SpaParamType.PortConfig, cancellationToken).ConfigureAwait(false));

    /// <summary>Reconfigures the node's ports.</summary>
    /// <param name="config">The layout to apply.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// <b>This destroys the node's ports and creates new ones.</b> Every port id the caller holds is
    /// stale afterwards, and every link through those ports is gone; the graph reports the removals
    /// and additions as it would for any other change. Re-read the ports rather than assuming the
    /// ids survived.
    /// <para>
    /// Only the adapter implements it. A node that is not one refuses with ENOTSUP, which arrives as
    /// a <see cref="PipeWireException"/> carrying -95.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    public Task SetPortConfigAsync(
        PipeWirePortConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return SetParameterAsync(SpaParamType.PortConfig, config.ToParameter(), cancellationToken);
    }

    // ------------------------------------------------------------------ latency and tags

    /// <summary>What this node declares it costs the graph to process.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The declared latency, or null when the node declares none.</returns>
    public async Task<PipeWireProcessLatency?> GetProcessLatencyAsync(
        CancellationToken cancellationToken = default) =>
        PipeWireProcessLatency.From(
            await GetParameterAsync(SpaParamType.ProcessLatency, cancellationToken).ConfigureAwait(false));

    /// <summary>Declares what this node costs the graph to process.</summary>
    /// <param name="latency">The delay this node adds. See the type for which unit to use.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// Only meaningful on a node this client owns. The daemon recomputes the latency of every path
    /// through it, so a wrong figure here is not a local error: it moves the numbers every consumer
    /// downstream uses to align audio against video.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="latency"/> is null.</exception>
    public Task SetProcessLatencyAsync(
        PipeWireProcessLatency latency, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(latency);
        return SetParameterAsync(SpaParamType.ProcessLatency, latency.ToParameter(), cancellationToken);
    }

    /// <summary>The metadata travelling with this node's stream, per direction.</summary>
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

    /// <summary>Sets the metadata travelling with this node's stream.</summary>
    /// <param name="tag">The direction and the key and value pairs.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tag"/> is null.</exception>
    public Task SetTagAsync(PipeWireTag tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return SetParameterAsync(SpaParamType.Tag, tag.ToParameter(), cancellationToken);
    }

    [LoggerMessage(EventId = 33000, Level = LogLevel.Error,
                   Message = "a ParameterChanged handler for node {NodeId} threw")]
    private partial void LogHandlerFaulted(uint nodeId, Exception exception);
}
