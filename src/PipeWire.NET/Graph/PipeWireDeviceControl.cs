using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// A device bound for reading and writing its parameters: profiles, routes and port configuration.
/// </summary>
/// <remarks>
/// <para>
/// A device is the thing nodes are made from, so this is where card-level decisions live. Choosing a
/// profile is what makes a card's sinks and sources appear in the graph - switching from "Analog
/// Stereo" to "Pro Audio" replaces every node the card had.
/// </para>
/// <para>
/// Routes are the jacks and speakers within the chosen profile, and carry their own volume and mute.
/// A route's volume is the hardware one; a node's is applied in software on top.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireDeviceControl : PipeWireParameterObject
{
    private readonly ILogger _logger;

    private PipeWireDeviceControl(PipeWireContext ctx, uint id, ILogger logger)
        : base(ctx, id) => _logger = logger;

    internal static unsafe PipeWireDeviceControl Bind(
        PipeWireContext ctx, pw_registry* registry, uint id, uint version, ILogger logger)
    {
        var control = new PipeWireDeviceControl(ctx, id, logger);
        control.Attach(BoundProxy.Bind(
            ctx, registry, id, Native.PW_TYPE_INTERFACE_DEVICE, version,
            sizeof(pw_device_events),
            events =>
            {
                var table = (pw_device_events*)events;
                table->version = Native.PW_VERSION_DEVICE_EVENTS;
                table->info = &OnInfoCallback;
                table->param = &OnParamCallback;
            },
            static (proxy, hook, events, data) => Native.pw_device_add_listener(
                (pw_device*)proxy, (spa_hook*)hook, (pw_device_events*)events, (void*)data),
            control));

        return control;
    }

    /// <summary>Every profile this device offers, such as the configurations of a sound card.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public Task<ImmutableArray<SpaObject>> EnumerateProfilesAsync(
        CancellationToken cancellationToken = default) =>
        EnumerateParametersAsync(SpaParamType.EnumProfile, cancellationToken);

    /// <summary>The profile the device is currently using, or <see langword="null"/> if it has none.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public Task<SpaObject?> GetProfileAsync(CancellationToken cancellationToken = default) =>
        GetParameterAsync(SpaParamType.Profile, cancellationToken);

    /// <summary>
    /// Switches the device to a profile, by the index reported in
    /// <see cref="EnumerateProfilesAsync"/>.
    /// </summary>
    /// <param name="index">The profile index.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// Destructive to the graph: the nodes the old profile provided are removed and the new
    /// profile's appear, so anything holding node ids across this call must re-resolve them.
    /// </remarks>
    public Task SetProfileAsync(int index, CancellationToken cancellationToken = default) =>
        SetParameterAsync(
            SpaParamType.Profile,
            new SpaObject(SpaType.ObjectParamProfile, SpaParamType.Profile,
                [new SpaProperty(SpaParamProfile.Index, 0, new SpaInt(index))]),
            cancellationToken);

    /// <summary>Every route the device offers: its jacks, speakers and microphones.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public Task<ImmutableArray<SpaObject>> EnumerateRoutesAsync(
        CancellationToken cancellationToken = default) =>
        EnumerateParametersAsync(SpaParamType.EnumRoute, cancellationToken);

    /// <summary>The routes currently in use, one per direction the profile provides.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public Task<ImmutableArray<SpaObject>> GetActiveRoutesAsync(
        CancellationToken cancellationToken = default) =>
        EnumerateParametersAsync(SpaParamType.Route, cancellationToken);

    /// <summary>
    /// Selects a route for one of the device's ports.
    /// </summary>
    /// <param name="routeIndex">The route index, from <see cref="EnumerateRoutesAsync"/>.</param>
    /// <param name="devicePort">
    /// Which of the device's ports to apply it to, from the route's <c>devices</c> property.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public Task SetRouteAsync(
        int routeIndex, int devicePort, CancellationToken cancellationToken = default) =>
        SetParameterAsync(
            SpaParamType.Route,
            new SpaObject(SpaType.ObjectParamRoute, SpaParamType.Route,
            [
                new SpaProperty(SpaParamRoute.Index, 0, new SpaInt(routeIndex)),
                new SpaProperty(SpaParamRoute.Device, 0, new SpaInt(devicePort)),
            ]),
            cancellationToken);

    /// <summary>
    /// Sets the hardware volume and mute of a route.
    /// </summary>
    /// <param name="routeIndex">The route index, from <see cref="EnumerateRoutesAsync"/>.</param>
    /// <param name="devicePort">Which of the device's ports the route applies to.</param>
    /// <param name="channelVolumes">One linear amplitude per channel.</param>
    /// <param name="muted">Whether to mute it.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// This is the mixer control on the card itself, which is why it survives the application that
    /// set it. A node's volume does not.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="channelVolumes"/> is empty or holds a negative value.</exception>
    public Task SetRouteVolumeAsync(
        int routeIndex,
        int devicePort,
        ReadOnlySpan<float> channelVolumes,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        if (channelVolumes.IsEmpty)
            throw new ArgumentException("at least one channel volume is required.", nameof(channelVolumes));

        var values = ImmutableArray.CreateBuilder<SpaValue>(channelVolumes.Length);
        foreach (float volume in channelVolumes)
        {
            if (float.IsNegative(volume) || float.IsNaN(volume))
                throw new ArgumentException("a channel volume must be a non-negative number.", nameof(channelVolumes));
            values.Add(new SpaFloat(volume));
        }

        // The volume lives in a Props object nested inside the Route, because a route describes both
        // which jack is selected and what its mixer is set to.
        var props = new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
        [
            new SpaProperty(SpaProp.Mute, 0, new SpaBool(muted)),
            new SpaProperty(SpaProp.ChannelVolumes, 0,
                new SpaArray(SpaType.Float, values.MoveToImmutable())),
        ]);

        return SetParameterAsync(
            SpaParamType.Route,
            new SpaObject(SpaType.ObjectParamRoute, SpaParamType.Route,
            [
                new SpaProperty(SpaParamRoute.Index, 0, new SpaInt(routeIndex)),
                new SpaProperty(SpaParamRoute.Device, 0, new SpaInt(devicePort)),
                new SpaProperty(SpaParamRoute.Props, 0, props),
                new SpaProperty(SpaParamRoute.Save, 0, new SpaBool(true)),
            ]),
            cancellationToken);
    }

    private protected override unsafe int EnumParamsNative(void* proxy, int seq, uint id, uint start, uint num) =>
        Native.pw_device_enum_params((pw_device*)proxy, seq, id, start, num, null);

    private protected override unsafe int SetParamNative(void* proxy, uint id, uint flags, spa_pod* param) =>
        Native.pw_device_set_param((pw_device*)proxy, id, flags, param);

    private protected override unsafe int SubscribeParamsNative(void* proxy, uint* ids, uint count) =>
        Native.pw_device_subscribe_params((pw_device*)proxy, ids, count);

    private protected override void OnHandlerFaulted(Exception exception) =>
        LogHandlerFaulted(Id, exception);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnInfoCallback(void* data, pw_device_info* info)
    {
        // An exception escaping a reverse P/Invoke aborts the process, so nothing here may throw.
        try
        {
            if (info is not null)
                FromUserData<PipeWireDeviceControl>(data)?.OnInfo(info->@params, info->n_params);
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
            FromUserData<PipeWireDeviceControl>(data)?.OnParam(seq, param);
        }
        catch
        {
            // Deliberately not logged: the instance the logger belongs to is what failed to resolve.
        }
    }

    [LoggerMessage(EventId = 33100, Level = LogLevel.Error,
                   Message = "a ParameterChanged handler for device {DeviceId} threw")]
    private partial void LogHandlerFaulted(uint deviceId, Exception exception);
}
