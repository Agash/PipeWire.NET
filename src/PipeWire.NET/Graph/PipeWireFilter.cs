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
/// <para>
/// <b>Dispose it; do not let it fall out of scope.</b> The callbacks the daemon holds refer back
/// here through a weak handle, so an instance the application drops is collected and simply stops
/// delivering, with no error and no final state change. That is the deliberate half of the trade:
/// a strong handle would keep every one ever made alive for the life of the process. What it costs
/// is that the garbage collector cannot be the thing that closes one, because by the time it runs
/// there is nothing left to close it from.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireFilter : IAsyncDisposable
{
    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    private readonly List<PipeWireFilterPort> _ports = [];

    // The graph's position area, as io_changed hands it over. Read on the loop thread when a video
    // port's format arrives, to size its buffers for the frames the graph publishes.
    private unsafe spa_io_position* _position;
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
    /// <para>
    /// Read each input port's samples, write each output port's, and return. This runs on the
    /// realtime thread, so the whole graph misses its deadline if the callback is late, not just
    /// this filter.
    /// </para>
    /// <para>
    /// What that rules out, none of which the type system can stop: allocation, locking, blocking
    /// or file IO, starting tasks, logging, anything that can trigger a collection, and any call
    /// back into this library's control plane. The spans handed out are valid only for the duration
    /// of the call and must not be stored.
    /// </para>
    /// <para>
    /// An exception is caught rather than allowed to escape into native code, because escaping
    /// would abort the process, but by then the cycle has already been missed.
    /// </para>
    /// </remarks>
    public ProcessHandler? ProcessCallback { get; set; }

    /// <summary>Runs once per graph cycle on the realtime thread.</summary>
    /// <param name="filter">The filter being processed.</param>
    /// <param name="sampleCount">Samples to produce or consume this cycle.</param>
    /// <param name="clock">
    /// Where the graph clock stands for this cycle. This is the supported way to get timing in a
    /// filter: <c>pw_filter_get_time</c> is marked deprecated in the header, which says to use the
    /// <c>spa_io_position</c> handed to the process callback instead - which is what this is.
    /// </param>
    /// <remarks>
    /// A struct rather than an object, and a single delegate rather than an event, for the same
    /// reason: neither may allocate on this thread.
    /// </remarks>
    public delegate void ProcessHandler(PipeWireFilter filter, uint sampleCount, in PipeWireGraphClock clock);

    private PipeWireVideoCycle _videoCycle;

    /// <summary>
    /// The frame geometry the graph is running, valid only inside the process callback.
    /// </summary>
    /// <remarks>
    /// What <see cref="PipeWireFilterPort.GetPixels"/> needs, and the reason it takes the size as
    /// arguments rather than reading it itself: a filter with several video ports reads this once
    /// and sizes every port from it. Outside the callback this reports
    /// <see cref="PipeWireVideoCycle.IsValid"/> false, because the graph is between cycles and the
    /// last geometry is not a statement about the next one.
    /// </remarks>
    public PipeWireVideoCycle VideoCycle => _videoCycle;


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

            // Under the loop lock, like the stream's equivalent: pw_filter_* is called with the
            // loop held, and without it this reads a field the loop thread is free to be writing
            // while the handle it is read through could be torn down underneath.
            uint id;
            using (_ctx.Lock())
            {
                if (_disposed || _handle is null) return null;
                id = Native.pw_filter_get_node_id(_handle.Filter);
            }

            // Zero is the core's id and can never be a filter's, so the daemon reporting it means
            // "not yet", exactly as SPA_ID_INVALID does.
            return id is 0 or NativeConstants.SPA_ID_INVALID ? null : id;
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
    public Task<uint> WaitForNodeIdAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected)
            throw new InvalidOperationException("the filter is not connected.");

        // Fast path: the id is often already there.
        if (NodeId is { } ready) return Task.FromResult(ready);
        cancellationToken.ThrowIfCancellationRequested();

        // Event-driven, not polled. The id arrives with the node's binding while state changes
        // mark the progress around it, so every transition re-reads the live id rather than
        // trusting any one event to carry it. Continuations run off the loop thread: completing
        // inline would run a stranger's continuation with the native lock held.
        var waiter = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);

        Action<PipeWireFilter, PipeWireFilterState, PipeWireFilterState, string?>? handler = null;
        handler = (_, _, _, _) =>
        {
            if (NodeId is { } id) waiter.TrySetResult(id);
        };

        CancellationTokenRegistration registration = cancellationToken.Register(
            static s => ((TaskCompletionSource<uint>)s!).TrySetCanceled(), waiter);

        StateChanged += handler;
        _ = waiter.Task.ContinueWith(
            _ =>
            {
                StateChanged -= handler;
                registration.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Re-check under subscription: the id may have arrived between the fast path and here.
        if (NodeId is { } arrived)
        {
            waiter.TrySetResult(arrived);
            return waiter.Task;
        }

        // One barrier, not a loop. Events are ordered, so anything the daemon had already done -
        // including assigning the id with no further state change to announce it - has been
        // dispatched by the time this answers. The recheck then either finishes or waits for the
        // next real transition; the caller's token is what ends a wait for an id that never comes.
        _ = CoreSync.RoundTripAsync(_ctx, cancellationToken).ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                    waiter.TrySetException(t.Exception?.InnerException
                        ?? new InvalidOperationException("the node-id wait ended with its barrier."));
                else if (NodeId is { } id)
                    waiter.TrySetResult(id);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return waiter.Task;
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

                try
                {
                    filter._handle = new PipeWireFilterHandle(native, context.LoopOwner, context.CoreOwner);
                }
                catch
                {
                    // Nothing knows about this filter yet, so a throw out of the handle
                    // constructor - the loop or core refusing a reference to a disposing context -
                    // would strand it.
                    Native.pw_filter_destroy(native);
                    throw;
                }

                filter._events = (pw_filter_events*)NativeMemory.AllocZeroed((nuint)sizeof(pw_filter_events));
                filter._events->version = NativeConstants.PW_VERSION_FILTER_EVENTS;
                filter._events->process = &OnProcessCallback;
                filter._events->state_changed = &OnStateChangedCallback;
                filter._events->io_changed = &OnIoChangedCallback;
                filter._events->param_changed = &OnParamChangedCallback;

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

    /// <summary>Whether this filter is driving the graph.</summary>
    /// <remarks>
    /// True only for a filter connected with the driver flag and chosen by the daemon as the driver.
    /// When it is, the graph does not run on its own clock: <see cref="TriggerProcess"/> is what
    /// advances it, once per buffer, and nothing happens until it is called.
    /// </remarks>
    public unsafe bool IsDriving
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle is null || !_connected) return false;

            using (_ctx.Lock())
                return Native.pw_filter_is_driving(_handle.Filter);
        }
    }

    /// <summary>
    /// Puts the filter into the error state and tells the daemon why.
    /// </summary>
    /// <param name="result">A negative errno describing the failure.</param>
    /// <param name="message">What went wrong, for logs and for the peer.</param>
    /// <remarks>
    /// The counterpart to what a stream does when it cannot satisfy a negotiation. A filter that
    /// fails and stays quiet leaves the nodes linked to it waiting on a cycle that will not come,
    /// and nothing in the graph says why. Reporting turns that into a visible error.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The filter is not connected.</exception>
    public unsafe void SetError(int result, string message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(message);
        if (_handle is null || !_connected)
            throw new InvalidOperationException("connect the filter before reporting an error on it.");

        using (_ctx.Lock())
        {
            _ = Native.pw_filter_set_error(_handle.Filter, result, message);
        }
    }

    /// <summary>Runs one iteration of the graph. Only meaningful while <see cref="IsDriving"/>.</summary>
    /// <remarks>
    /// A driving filter decides when the graph advances, so this is what produces a
    /// <c>Process</c> callback. On a filter that is not driving the daemon schedules the graph and
    /// this does nothing useful.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The filter is not connected.</exception>
    /// <exception cref="PipeWireException">The daemon refused.</exception>
    public unsafe void TriggerProcess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle is null || !_connected)
            throw new InvalidOperationException("connect the filter before triggering it.");

        using (_ctx.Lock())
        {
            // Skipped unless this filter is the one driving. Upstream sends the request on to
            // whichever node actually drives, and a node that does not implement RequestProcess
            // answers ENOTSUP and the daemon logs it - once per call, so a caller triggering at
            // frame rate fills the log and changes nothing.
            if (!Native.pw_filter_is_driving(_handle.Filter)) return;

            int rc = Native.pw_filter_trigger_process(_handle.Filter);
            if (rc < 0) throw new PipeWireInteropException("pw_filter_trigger_process", rc);
        }
    }

    /// <summary>Starts or stops the filter processing without disconnecting it.</summary>
    /// <param name="active">True to process, false to stop.</param>
    /// <remarks>
    /// The difference from disposal is that the ports, the links through them and the negotiated
    /// format all survive: an inactive filter is still in the graph and can be started again. A
    /// filter connected with the inactive flag needs this before it does anything.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The filter is not connected.</exception>
    /// <exception cref="PipeWireException">The daemon refused.</exception>
    public unsafe void SetActive(bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle is null || !_connected)
            throw new InvalidOperationException("connect the filter before activating it.");

        using (_ctx.Lock())
        {
            int rc = Native.pw_filter_set_active(_handle.Filter, active);
            if (rc < 0) throw new PipeWireInteropException("pw_filter_set_active", rc);
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
    public PipeWireFilterPort AddAudioPort(PipeWirePortDirection direction, string name) =>
        AddPort(direction, name, PipeWireDspFormat.MonoAudio);

    /// <summary>Adds a MIDI port.</summary>
    /// <param name="direction">Whether the filter reads from it or writes to it.</param>
    /// <param name="name">The port's name, as the graph shows it.</param>
    /// <returns>The port, for reading or writing during processing.</returns>
    /// <remarks>
    /// A MIDI port carries a sequence of timed controls per buffer rather than samples, so
    /// <see cref="PipeWireFilterPort.GetSamples"/> refuses it: sequences get a typed accessor of
    /// their own when the sequence transport lands. Add every port before connecting.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The filter is already connected, or PipeWire refused.</exception>
    public PipeWireFilterPort AddMidiPort(PipeWirePortDirection direction, string name) =>
        AddPort(direction, name, PipeWireDspFormat.Midi);

    /// <summary>Adds a control port.</summary>
    /// <param name="direction">Whether the filter reads from it or writes to it.</param>
    /// <param name="name">The port's name, as the graph shows it.</param>
    /// <returns>The port, for reading or writing during processing.</returns>
    /// <inheritdoc cref="AddMidiPort" path="/remarks"/>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The filter is already connected, or PipeWire refused.</exception>
    public PipeWireFilterPort AddControlPort(PipeWirePortDirection direction, string name) =>
        AddPort(direction, name, PipeWireDspFormat.Control);

    /// <summary>Adds a video port carrying 32 bit float RGBA frames.</summary>
    /// <param name="direction">Whether the filter reads from it or writes to it.</param>
    /// <param name="name">The port's name, as the graph shows it.</param>
    /// <remarks>
    /// The shape upstream's <c>video-dsp-play</c> and <c>video-dsp-src</c> use: a filter port is how
    /// video reaches a processing node without the node being a stream. The frame's geometry is not
    /// on the port - it comes from the graph's position area, so a filter reads the size from its
    /// process callback rather than negotiating it.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The filter is already connected, or PipeWire refused.</exception>
    public PipeWireFilterPort AddVideoPort(PipeWirePortDirection direction, string name) =>
        AddPort(direction, name, PipeWireDspFormat.Rgba32FloatVideo);

    /// <summary>Adds a port carrying MIDI 2.0 Universal MIDI Packets.</summary>
    /// <param name="direction">Whether the filter reads from it or writes to it.</param>
    /// <param name="name">The port's name, as the graph shows it.</param>
    /// <remarks>
    /// PipeWire rewrites this port's <c>format.dsp</c> to <c>8 bit raw midi</c> and sets
    /// <c>control.ump</c>, so the port reports itself as MIDI once it is in the graph. What differs
    /// is the control type it carries.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The filter is already connected, or PipeWire refused.</exception>
    public PipeWireFilterPort AddUmpPort(PipeWirePortDirection direction, string name) =>
        AddPort(direction, name, PipeWireDspFormat.Ump);

    /// <summary>Adds a port carrying a named DSP format.</summary>
    /// <param name="direction">Whether the filter reads from it or writes to it.</param>
    /// <param name="name">The port's name, as the graph shows it.</param>
    /// <param name="format">What the port carries.</param>
    /// <param name="properties">
    /// Extra port properties, or null. A caller's value wins over the defaults except for
    /// <c>format.dsp</c> and <c>port.name</c>, which this method owns.
    /// </param>
    /// <returns>The port, for reading or writing during processing.</returns>
    /// <remarks>
    /// The general form. <c>format.dsp</c> is what decides a port's shape, and the graph's DSP links
    /// only carry the formats <see cref="PipeWireDspFormat"/> names: a port declaring anything
    /// else is not linkable to the rest of the graph, which is why this takes an enum rather than a
    /// string. Add every port before connecting.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The filter is already connected, or PipeWire refused.</exception>
    public unsafe PipeWireFilterPort AddPort(
        PipeWirePortDirection direction,
        string name,
        PipeWireDspFormat format,
        IReadOnlyDictionary<string, string>? properties = null)
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
                "a filter port is an input or an output.", nameof(direction)),
        };

        string dsp = format switch
        {
            PipeWireDspFormat.MonoAudio => "32 bit float mono audio",
            PipeWireDspFormat.Midi => "8 bit raw midi",
            PipeWireDspFormat.Control => "8 bit raw control",
            PipeWireDspFormat.Rgba32FloatVideo => "32 bit float RGBA video",
            PipeWireDspFormat.Ump => "32 bit raw UMP",
            _ => throw new ArgumentException($"unknown DSP format {format}.", nameof(format)),
        };

        var portProperties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (properties is not null)
        {
            foreach (KeyValuePair<string, string> pair in properties)
                portProperties[pair.Key] = pair.Value;
        }

        portProperties[PipeWireKeys.PW_KEY_FORMAT_DSP] = dsp;
        portProperties[PipeWireKeys.PW_KEY_PORT_NAME] = name;

        Dictionary<string, string> properties2 = portProperties;

        void* portData;
        using (_ctx.Lock())
        {
            portData = Native.pw_filter_add_port(
                _handle!.Filter, spaDirection, PipeWireFilterPortFlags.MapBuffers,
                0, BuildProperties(properties2), null, 0);
        }

        if (portData is null)
            throw new InvalidOperationException($"pw_filter_add_port failed for '{name}'.");

        var port = new PipeWireFilterPort(this, portData, direction, name, format);
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
            builder.Add(key, value);

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

            ProcessHandler? callback = self.ProcessCallback;
            if (callback is null || position is null) return;

            // The whole clock, not just the duration. The position area is the only timing a filter
            // is supposed to read - pw_filter_get_time is deprecated in favour of exactly this - so
            // dropping everything but the sample count left a filter unable to timestamp its own
            // work or line it up against anything else on the graph.
            spa_io_clock* c = &position->clock;
            PipeWireGraphClock clock = new(
                c->nsec, c->position, c->duration,
                c->rate.num, c->rate.denom, c->delay, c->rate_diff, c->next_nsec);

            // The graph's frame geometry for this cycle. A video port has no size of its own, so
            // this is the only place a filter can learn how many pixels its buffer holds - and it
            // is per-cycle, because the graph may change it.
            spa_io_video_size* v = &position->video;
            self._videoCycle = new PipeWireVideoCycle(
                (v->flags & (uint)SpaIoVideoSizeFlags.Valid) != 0,
                v->size.width, v->size.height, v->stride,
                v->framerate.num, v->framerate.denom);

            // Invoked directly rather than through GetInvocationList: walking one allocates, which
            // is why this is a single delegate and not an event.
            try
            {
                callback(self, (uint)position->clock.duration, in clock);
            }
            finally
            {
                // Outside the cycle the geometry is not a fact about anything, and a filter that
                // read it later would size its next access from a frame the graph has moved on from.
                self._videoCycle = default;
            }
        }
        catch
        {
            // Deliberately not logged: logging from the realtime thread is itself a violation, and
            // the instance whose logger it would be is what failed to resolve.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnIoChangedCallback(void* data, void* portData, uint id, void* area, uint size)
    {
        try
        {
            var self = (PipeWireFilter?)GCHandle.FromIntPtr((nint)data).Target;
            if (self is null || self._disposed) return;

            // The node's position area; port io areas are pw_filter's own business.
            if (portData is null && id == (uint)SpaIoType.Position)
                self._position = area is not null && size >= (uint)sizeof(spa_io_position) ? (spa_io_position*)area : null;
        }
        catch
        {
            // Deliberately not logged: the instance the logger belongs to is what failed to resolve.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnParamChangedCallback(void* data, void* portData, uint id, spa_pod* param)
    {
        try
        {
            var self = (PipeWireFilter?)GCHandle.FromIntPtr((nint)data).Target;
            if (self is null || self._disposed || portData is null || param is null) return;
            if (id != (uint)SpaParamType.Format) return;

            PipeWireFilterPort? port = self._ports.Find(p => p.PortData == portData);
            if (port is { Format: PipeWireDspFormat.Rgba32FloatVideo })
                self.DeclareVideoBuffers(port);
        }
        catch (Exception ex)
        {
            // A native frame on the loop thread: contained, and the port keeps whatever pool the
            // graph gives it, which the pixel accessor refuses rather than overruns.
            if (GCHandle.FromIntPtr((nint)data).Target is PipeWireFilter self) self.LogVideoBuffersFailed(ex);
        }
    }

    /// <summary>
    /// Tells the graph how large a video port's buffers must be, once its format is set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream's filter declares no Buffers parameter for a video DSP port
    /// (<c>filter.c, add_video_dsp_port_params</c>), so the pool falls back to buffers.c's default of
    /// <c>clock.quantum_limit</c> bytes - 8 KiB on a default install - while the graph publishes frames
    /// of <c>default.video.width</c> x <c>height</c> (640x480) at 16 bytes a pixel. A frame does not
    /// fit, and writing one overran the buffer every cycle. Upstream's own video-dsp-src answers its
    /// format exactly this way: stride from the position area's width, size stride times height.
    /// </para>
    /// <para>
    /// Before the position area arrives, upstream's defaults stand in; a graph that later publishes a
    /// larger frame gets buffers too small for it, which the pixel accessor refuses rather than
    /// overruns.
    /// </para>
    /// </remarks>
    private unsafe void DeclareVideoBuffers(PipeWireFilterPort port)
    {
        uint width = DefaultVideoWidth, height = DefaultVideoHeight;
        spa_io_position* position = _position;
        if (position is not null
            && (position->video.flags & (uint)SpaIoVideoSizeFlags.Valid) != 0
            && position->video.size.width > 0 && position->video.size.height > 0)
        {
            width = position->video.size.width;
            height = position->video.size.height;
        }

        // RGBA, four floats: the stride upstream's impl-node.c publishes (width * 16), rounded as
        // video-dsp-src rounds it.
        int stride = checked((int)((width * 16 + 3) & ~3u));
        int size = checked(stride * (int)height);

        Span<byte> pod = stackalloc byte[256];
        var b = new SpaPodBuilder(pod);
        b.PushObject(SpaType.ObjectParamBuffers, SpaParamType.Buffers);
        b.AddChoiceRangeInt(SpaParamBuffers.Buffers, 8, 2, 64);
        b.AddInt(SpaParamBuffers.Blocks, 1);
        b.AddInt(SpaParamBuffers.Size, size);
        b.AddInt(SpaParamBuffers.Stride, stride);
        b.Pop();

        int rc;
        fixed (byte* p = b.GetPod())
        {
            var param = (spa_pod*)p;
            rc = Native.pw_filter_update_params(_handle!.Filter, port.PortData, &param, 1);
        }

        if (rc < 0) LogVideoBuffersRefused(port.Name, rc);
        else LogVideoBuffersDeclared(port.Name, width, height, size);
    }

    // Upstream's default.video.width and height (settings.c), what the graph publishes when nothing
    // configures them.
    private const uint DefaultVideoWidth = 640;
    private const uint DefaultVideoHeight = 480;

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
                    DaemonText.Bytes((sbyte*)error));

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

        // Not the realtime path, so one throwing subscriber must not starve the rest.
        SafeCallback.Raise(StateChanged, h => h(this, old, state, error), ex => LogHandlerFaulted(Name, ex));
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
        // The listener's memory belongs to the handle once OwnListener has run, and the handle
        // frees it after pw_filter_destroy rather than racing it.
        bool ownedByHandle = _handle?.OwnsListener ?? false;

        _handle?.Dispose();
        _handle = null;

        // Only when the handover never happened. Creation allocates these before it hands them
        // over, so a throw in between leaves blocks and a GCHandle that nothing else will free.
        if (!ownedByHandle)
        {
            if (_hook is not null) NativeMemory.Free(_hook);
            if (_events is not null) NativeMemory.Free(_events);
            if (_self.IsAllocated) _self.Free();
        }

        _hook = null;
        _events = null;

        _ports.Clear();
    }

    [LoggerMessage(EventId = 33310, Level = LogLevel.Debug,
        Message = "video port '{Port}' asks for {Width}x{Height} frames: buffers of {Size} bytes")]
    private partial void LogVideoBuffersDeclared(string port, uint width, uint height, int size);

    [LoggerMessage(EventId = 33311, Level = LogLevel.Warning,
        Message = "the graph refused video port '{Port}''s buffer size ({Result}); frames larger than its default pool will not be handed out")]
    private partial void LogVideoBuffersRefused(string port, int result);

    [LoggerMessage(EventId = 33312, Level = LogLevel.Error,
        Message = "declaring a video port's buffers threw")]
    private partial void LogVideoBuffersFailed(Exception exception);

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
