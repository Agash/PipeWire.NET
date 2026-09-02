using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// A filter: a node of this process's own, sitting inside the graph and processing audio as it
/// passes through.
/// </summary>
/// <remarks>
/// <para>
/// The difference from a stream is where the work happens. A stream moves media between an
/// application and the graph; a filter <em>is</em> a participant in the graph, with its own input
/// and output ports that other nodes link to. An equaliser, a noise gate or a mixer is a filter.
/// </para>
/// <para>
/// <see cref="ProcessCallback"/> runs on the realtime thread, once per graph cycle, and everything
/// the usual rules say applies: no allocation, no locks, no I/O, no exceptions. Missing the deadline
/// is an xrun the whole graph hears. It is a single delegate rather than an event on purpose -
/// walking a multicast invocation list allocates, which is exactly what must not happen there.
/// </para>
/// <para>
/// Ports are added before connecting and describe themselves as mono 32-bit float, which is the one
/// format the graph's DSP links carry. Stereo is two ports.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireFilter : IAsyncDisposable
{
    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    private readonly List<PipeWireFilterPort> _ports = [];
    private PipeWireFilterHandle? _handle;
    private unsafe pw_filter_events* _events;
    private unsafe spa_hook* _hook;
    private GCHandle _self;
    private volatile bool _disposed;
    private volatile bool _connected;

    private PipeWireFilter(PipeWireContext ctx, string name, ILogger logger)
    {
        _ctx = ctx;
        Name = name;
        _logger = logger;
    }

    /// <summary>The filter's name, as it appears in the graph.</summary>
    public string Name { get; }

    /// <summary>True once the filter has been disposed; its ports are dead with it.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>The ports added so far, in the order they were added.</summary>
    public IReadOnlyList<PipeWireFilterPort> Ports => _ports;

    /// <summary>
    /// Runs once per graph cycle on the realtime thread, with the cycle's sample count.
    /// </summary>
    /// <remarks>
    /// Read each input port's samples, write each output port's, and return. Anything that
    /// allocates, blocks or throws here damages the whole graph, not just this filter - an
    /// exception is caught and logged rather than allowed to escape into native code, but by then
    /// the cycle has already been missed.
    /// </remarks>
    public Action<PipeWireFilter, uint>? ProcessCallback { get; set; }

    /// <summary>Raised when the filter changes state, on the loop thread.</summary>
    public event Action<PipeWireFilter, PipeWireFilterState, PipeWireFilterState, string?>? StateChanged;

    /// <summary>The node id the filter was given, or <see langword="null"/> before it has one.</summary>
    /// <remarks>
    /// <para>
    /// This is what links the filter to the rest of the graph: the id other nodes are linked to, and
    /// the id to look up in a <see cref="PipeWireGraphSnapshot"/>.
    /// </para>
    /// <para>
    /// Not assigned by the time <see cref="ConnectAsync"/> returns. The connect is processed by the
    /// daemon, but the id arrives afterwards with the node's own binding, so this reads
    /// <see langword="null"/> for a short while after connecting - poll it, or wait for the state to
    /// leave <see cref="PipeWireFilterState.Connecting"/>.
    /// </para>
    /// </remarks>
    public unsafe uint? NodeId
    {
        get
        {
            if (_disposed || _handle is null || !_connected) return null;

            uint id = Native.pw_filter_get_node_id(_handle.Filter);

            // Zero is the core's id and can never be a filter's, so the daemon reporting it means
            // "not yet", exactly as SPA_ID_INVALID does.
            return id is 0 or Native.SPA_ID_INVALID ? null : id;
        }
    }

    /// <summary>
    /// Waits until the filter has been given a node id, so it can be linked to.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The node id.</returns>
    /// <remarks>
    /// Connecting and being placed in the graph are two steps, and only the first is complete when
    /// <see cref="ConnectAsync"/> returns. Everything that addresses the filter as a node - linking
    /// it, finding its ports - needs the second.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The filter is not connected.</exception>
    public async Task<uint> WaitForNodeIdAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected)
            throw new InvalidOperationException("the filter is not connected.");

        while (true)
        {
            if (NodeId is { } id) return id;

            cancellationToken.ThrowIfCancellationRequested();

            // A round-trip rather than a delay: it returns as soon as the daemon has caught up,
            // which is what actually assigns the id, instead of guessing at how long that takes.
            await CoreSync.RoundTripAsync(_ctx, cancellationToken).ConfigureAwait(false);

            if (NodeId is { } assigned) return assigned;

            // No delay between round-trips: each one already waits for the daemon to finish what it
            // had queued, so sleeping on top of it only adds latency to the answer.
        }
    }

    /// <summary>The filter's current state.</summary>
    public unsafe PipeWireFilterState State
    {
        get
        {
            // Under the loop lock, like every other call into the filter: the state is read from
            // structures the loop thread mutates as the filter transitions.
            if (_disposed || _handle is null || _handle.IsInvalid) return PipeWireFilterState.Unconnected;

            if (!_ctx.TryLock(out PipeWireContext.LoopLock scope)) return PipeWireFilterState.Unconnected;

            using (scope)
            {
                return _handle.IsInvalid
                    ? PipeWireFilterState.Unconnected
                    : Native.pw_filter_get_state(_handle.Filter, null);
            }
        }
    }

    /// <summary>
    /// Creates a filter. It appears in the graph only once <see cref="ConnectAsync"/> is called.
    /// </summary>
    /// <param name="context">The context whose core the filter belongs to; must be started.</param>
    /// <param name="name">The filter's name in the graph.</param>
    /// <param name="properties">Extra node properties, such as <c>media.name</c> or a target.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The context is not connected, or PipeWire refused.</exception>
    public static unsafe PipeWireFilter Create(
        PipeWireContext context,
        string name,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var filter = new PipeWireFilter(context, name, context.LoggerFactory.CreateLogger<PipeWireFilter>());
        try
        {
            using (context.Lock())
            {
                pw_core* core = context.CoreHandle;
                if (core is null)
                {
                    throw new InvalidOperationException(
                        "the context is not connected; call StartAsync before creating a filter.");
                }

                pw_filter* native;
                ReadOnlySpan<byte> nameUtf8 = Encoding.UTF8.GetBytes(name + '\0');
                pw_properties* props = BuildProperties(properties);
                fixed (byte* n = nameUtf8)
                    native = Native.pw_filter_new(core, (sbyte*)n, props);

                if (native is null)
                    throw new InvalidOperationException("pw_filter_new failed.");

                filter._handle = new PipeWireFilterHandle(native, context.LoopOwner, context.CoreOwner);

                filter._events = (pw_filter_events*)NativeMemory.AllocZeroed((nuint)sizeof(pw_filter_events));
                filter._events->version = Native.PW_VERSION_FILTER_EVENTS;
                filter._events->process = &OnProcessCallback;
                filter._events->state_changed = &OnStateChangedCallback;

                filter._hook = (spa_hook*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook));
                // Weak: a strong self-handle roots the filter for the life of the process, so one dropped
        // without disposal leaks the native filter too.
        filter._self = GCHandle.Alloc(filter, GCHandleType.Weak);

                // Handed over before the listener is attached, so a failure below still frees it and
                // the free happens after pw_filter_destroy rather than racing it.
                filter._handle!.OwnListener(filter._events, filter._hook, filter._self);

                Native.pw_filter_add_listener(
                    native, filter._hook, filter._events, (void*)GCHandle.ToIntPtr(filter._self));
            }

            return filter;
        }
        catch
        {
            filter.Release();
            throw;
        }
    }

    /// <summary>
    /// Adds a mono DSP audio port.
    /// </summary>
    /// <param name="direction">Whether audio enters or leaves the filter here.</param>
    /// <param name="name">The port name, as it appears in the graph.</param>
    /// <returns>The port, for reading or writing its samples during processing.</returns>
    /// <remarks>
    /// Add every port before connecting. Ports declare themselves as
    /// <c>32 bit float mono audio</c>, which is the format the graph's DSP links carry - a filter
    /// that wanted anything else would not be linkable to the rest of the graph.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The filter is already connected, or PipeWire refused.</exception>
    public unsafe PipeWireFilterPort AddAudioPort(PipeWirePortDirection direction, string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_connected)
            throw new InvalidOperationException("ports must be added before the filter is connected.");

        SpaDirection spaDirection = direction switch
        {
            PipeWirePortDirection.In => SpaDirection.Input,
            PipeWirePortDirection.Out => SpaDirection.Output,
            _ => throw new ArgumentException(
                "a filter port carries audio, so it is an input or an output.", nameof(direction)),
        };

        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format.dsp"] = "32 bit float mono audio",
            ["port.name"] = name,
        };

        void* portData;
        using (_ctx.Lock())
        {
            portData = Native.pw_filter_add_port(
                _handle!.Filter, spaDirection, PipeWireFilterPortFlags.MapBuffers,
                0, BuildProperties(properties), null, 0);
        }

        if (portData is null)
            throw new InvalidOperationException($"pw_filter_add_port failed for '{name}'.");

        var port = new PipeWireFilterPort(this, portData, direction, name);
        _ports.Add(port);
        return port;
    }

    /// <summary>
    /// Puts the filter into the graph, where other nodes can link to its ports.
    /// </summary>
    /// <param name="flags">How the filter participates; the default is enough for ordinary DSP.</param>
    /// <param name="cancellationToken">Abandons the wait for the daemon to catch up.</param>
    /// <exception cref="InvalidOperationException">PipeWire refused the connection.</exception>
    public async Task ConnectAsync(
        PipeWireFilterFlags flags = PipeWireFilterFlags.RtProcess,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // The node appears in the graph asynchronously, so returning before the round-trip would
        // hand back a filter whose NodeId is not yet assigned. Connecting inside the round-trip
        // also keeps a refusal from being answered before anything is listening for it.
        // Marked connected only once the round-trip has actually succeeded: setting it first leaves
        // a filter that refused to connect claiming it did.
        await CoreSync.RoundTripAsync(_ctx, () => ConnectNative(flags), cancellationToken)
            .ConfigureAwait(false);
        _connected = true;

        LogConnected(Name, _ports.Count);
    }

    private unsafe int ConnectNative(PipeWireFilterFlags flags)
    {
        using (_ctx.Lock())
            return Native.pw_filter_connect(_handle!.Filter, flags, null, 0);
    }

    private static unsafe pw_properties* BuildProperties(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null || properties.Count == 0)
            return null;

        int bytes = 0;
        foreach ((string key, string value) in properties)
            bytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value) + 2;

        // pw_properties_new_dict copies what it is given, so these buffers only have to survive the
        // call - but they must not move during it, which is why the heap fallback is pinned.
        Span<byte> scratch = bytes <= 512
            ? stackalloc byte[bytes]
            : GC.AllocateUninitializedArray<byte>(bytes, pinned: true);
        Span<spa_dict_item> items = properties.Count <= 16
            ? stackalloc spa_dict_item[properties.Count]
            : GC.AllocateArray<spa_dict_item>(properties.Count, pinned: true);

        var builder = new SpaDictBuilder(scratch, items);
        foreach ((string key, string value) in properties)
            builder.Add(Encoding.UTF8.GetBytes(key), value);

        spa_dict dict = builder.Build();
        return Native.pw_properties_new_dict(&dict);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnProcessCallback(void* data, spa_io_position* position)
    {
        // The realtime thread. An exception escaping here aborts the process, and even catching one
        // has already cost the cycle - the contract is that the callback does not throw.
        try
        {
            var self = (PipeWireFilter?)GCHandle.FromIntPtr((nint)data).Target;
            if (self is null || self._disposed) return;

            Action<PipeWireFilter, uint>? callback = self.ProcessCallback;
            if (callback is null || position is null) return;

            // Invoked directly rather than through GetInvocationList: walking one allocates, which
            // is why this is a single delegate and not an event.
            callback(self, (uint)position->clock.duration);
        }
        catch
        {
            // Deliberately not logged: logging from the realtime thread is itself a violation, and
            // the instance whose logger it would be is what failed to resolve.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnStateChangedCallback(
        void* data, PipeWireFilterState old, PipeWireFilterState state, sbyte* error)
    {
        try
        {
            var self = (PipeWireFilter?)GCHandle.FromIntPtr((nint)data).Target;
            if (self is null || self._disposed) return;

            string? message = error is null
                ? null
                : Encoding.UTF8.GetString(
                    MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)error));

            self.RaiseStateChanged(old, state, message);
        }
        catch
        {
            // Deliberately not logged: the instance the logger belongs to is what failed to resolve.
        }
    }

    private void RaiseStateChanged(PipeWireFilterState old, PipeWireFilterState state, string? error)
    {
        LogStateChanged(Name, old, state, error);

        Action<PipeWireFilter, PipeWireFilterState, PipeWireFilterState, string?>? handlers = StateChanged;
        if (handlers is null) return;

        // Not the realtime path, so one throwing subscriber must not starve the rest.
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<PipeWireFilter, PipeWireFilterState, PipeWireFilterState, string?>)handler)(
                    this, old, state, error);
            }
            catch (Exception ex)
            {
                LogHandlerFaulted(Name, ex);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Release();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private unsafe void Release()
    {
        // The listener's memory belongs to the handle, which frees it after pw_filter_destroy has
        // actually run - disposal only destroys once nothing else holds a reference.
        _handle?.Dispose();
        _handle = null;
        _hook = null;
        _events = null;

        _ports.Clear();
    }

    [LoggerMessage(EventId = 33300, Level = LogLevel.Debug,
                   Message = "filter '{Name}' connected with {PortCount} port(s)")]
    private partial void LogConnected(string name, int portCount);

    [LoggerMessage(EventId = 33301, Level = LogLevel.Debug,
                   Message = "filter '{Name}' state {Old} -> {State} {Error}")]
    private partial void LogStateChanged(
        string name, PipeWireFilterState old, PipeWireFilterState state, string? error);

    [LoggerMessage(EventId = 33302, Level = LogLevel.Error,
                   Message = "a StateChanged handler for filter '{Name}' threw")]
    private partial void LogHandlerFaulted(string name, Exception exception);
}
