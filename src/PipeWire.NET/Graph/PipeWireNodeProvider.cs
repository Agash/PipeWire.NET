using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// A node this process implements itself, published into the graph.
/// </summary>
/// <remarks>
/// <para>
/// The layer below <c>pw_stream</c>. A stream is a node libpipewire implements on the application's
/// behalf, with libpipewire's policy about formats, buffers and scheduling baked in; exporting means
/// being the node, and answering the graph's questions directly. That is what upstream's
/// <c>export-source</c> and <c>export-sink</c> examples do, and what a virtual device needs.
/// </para>
/// <para>
/// The technique is not new here even though the interface is: every <c>pw_*_events</c> struct this
/// library already fills is a table of function pointers, and <c>pw_stream</c> is itself an
/// implementation of this same <c>spa_node</c> interface (upstream <c>stream.c</c>). What differs is
/// that the graph calls in rather than out, several of the calls happen on the realtime thread, and
/// the buffer-negotiation state machine is the implementer's to run.
/// </para>
/// <para>
/// Of the fifteen methods on <c>spa_node_methods</c>, the eight upstream's export examples implement
/// are the ones a source or sink needs; the rest stay null, which the graph reads as unsupported.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class PipeWireNodeProvider : IDisposable, IAsyncDisposable
{
    /// <summary>Fills or consumes one buffer for a cycle.</summary>
    /// <param name="node">The node being processed.</param>
    /// <param name="data">The buffer's bytes, sized to what the peer allocated.</param>
    /// <returns>How many bytes were written, or 0 to publish nothing this cycle.</returns>
    /// <remarks>
    /// Runs on the realtime thread. Nothing here may allocate, lock or block: the graph is waiting,
    /// and a late return is an xrun for every node in the driver group.
    /// </remarks>
    public delegate int ProcessHandler(PipeWireNodeProvider node, Span<byte> data);

    // A single port, index 0 - output for a source, input for a sink - which is the shape both
    // export examples have.
    private const uint PortId = 0;

    private readonly PipeWireContext _ctx;
    private readonly SpaDirection _direction;
    private readonly string _name;
    private readonly ILogger _logger;

    private GCHandle _self;
    private spa_node* _node;
    private spa_node_methods* _methods;

    // What the graph installed for this node to call back into. `ready` and `reuse_buffer` stay
    // unused deliberately - a null `ready` is how a node asks for synchronous operation, and the
    // buffers to reuse go through the input port's io area - but `xrun` is how a node reports that
    // it missed a cycle, and nothing else can report that on its behalf.
    private spa_node_callbacks* _callbacks;
    private void* _callbacksData;

    // Every listener on this node, not just the one that exported it: the audio adapter that wraps
    // it keeps one for good, and synchronous queries add and remove their own (see SpaHookList).
    private spa_hook_list* _hooks;
    private spa_io_buffers* _io;
    private spa_io_position* _position;

    // The realtime loop this node's process() runs on, used to serialise io changes with it.
    private pw_loop* _dataLoop;
    private pw_proxy* _proxy;
    private spa_handle* _spaHandle;

    // The adapter node this node is the follower of, as a pw_stream's node is (stream.c). Null for a
    // node exported from a SPA factory, which is published as it is.
    private pw_impl_node* _adapter;

    // What this node's info reports as its properties. A follower's node info is where the adapter
    // learns them (module-adapter's info_event runs pw_properties_update on info->props with no null
    // check), so they are kept for every emission rather than only handed to the export.
    private KeyValuePair<string, string>[] _nodeProperties = [];

    private spa_buffer** _buffers;
    private uint _bufferCount;
    private uint _nextBuffer;
    private bool[] _free = [];

    private bool _disposed;

    private PipeWireNodeProvider(PipeWireContext ctx, string name, SpaDirection direction)
    {
        _ctx = ctx;
        _name = name;
        _direction = direction;
        _logger = ctx.LoggerFactory.CreateLogger<PipeWireNodeProvider>();
    }

    /// <summary>Tells the graph this node missed a cycle.</summary>
    /// <param name="triggerMicroseconds">When the xrun happened, on the graph's clock.</param>
    /// <param name="delayMicroseconds">How long the node was late by.</param>
    /// <returns>
    /// <see langword="true"/> if the graph took the report. <see langword="false"/> when it
    /// installed no xrun callback, which is legal and means it does not want them.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A node that overruns or underruns and says nothing leaves the graph's own accounting wrong:
    /// the cycle is simply missing, and tools that read the daemon's xrun counters - `pw-top` among
    /// them - attribute nothing to this node. This is the only way a node reports one; nothing can
    /// do it on the node's behalf.
    /// </para>
    /// <para>
    /// Call it from inside the process callback, on the realtime thread, which is where the graph
    /// expects its callbacks to come from. The library does not report automatically when a process
    /// handler throws: a dropped cycle is not always an xrun, and only the caller knows the trigger
    /// and delay to report.
    /// </para>
    /// </remarks>
    public unsafe bool ReportXrun(ulong triggerMicroseconds, ulong delayMicroseconds)
    {
        spa_node_callbacks* callbacks = _callbacks;
        if (_disposed || callbacks is null || callbacks->xrun is null)
            return false;

        return callbacks->xrun(_callbacksData, triggerMicroseconds, delayMicroseconds, null) >= 0;
    }

    /// <summary>Raised to fill (a source) or consume (a sink) one cycle's buffer.</summary>
    public ProcessHandler? ProcessCallback { get; set; }

    /// <summary>The format the peer settled on, or null before negotiation.</summary>
    public PipeWireExportedFormat? NegotiatedFormat { get; private set; }

    /// <summary>How many buffers the peer allocated, or 0 before it did.</summary>
    public int BufferCount => (int)_bufferCount;

    /// <summary>Whether the graph has driven at least one cycle through this node.</summary>
    /// <remarks>
    /// True from the first cycle the graph runs, whether or not anything was produced: a node with
    /// no <see cref="ProcessCallback"/>, or one whose handler returns zero, is still being driven.
    /// Together with <see cref="HasProcessed"/> this separates the two reasons an exported node goes
    /// quiet - never scheduled, or scheduled and producing nothing - which are otherwise the same
    /// silence from outside.
    /// </remarks>
    public bool HasBeenScheduled { get; private set; }

    /// <summary>Whether a process handler has produced data at least once.</summary>
    /// <remarks>
    /// Set where a handler returned bytes, so it stays false for a node the graph is driving that
    /// has no handler or whose handler produces nothing. See <see cref="HasBeenScheduled"/> for
    /// whether the graph is running cycles at all.
    /// </remarks>
    public bool HasProcessed { get; private set; }

    /// <summary>The last exception a process cycle threw, or null if none has.</summary>
    /// <remarks>
    /// A process callback cannot let an exception reach its native caller, so one is recorded here
    /// rather than thrown. Tests and callers read it to turn a dropped cycle into a diagnosable
    /// failure instead of a stream that merely goes quiet.
    /// </remarks>
    public Exception? LastProcessError { get; private set; }

    /// <summary>The last exception one of this node's own graph callbacks threw, or null if none has.</summary>
    /// <remarks>
    /// Kept apart from <see cref="LastProcessError"/> because the two mean different things: that
    /// one is the caller's handler failing, this one is this library failing to answer the graph.
    /// A node that negotiates oddly or never starts is usually explained here.
    /// </remarks>
    public Exception? LastCallbackError { get; private set; }

    /// <summary>
    /// Records an exception that must not cross back into C and turns it into an errno.
    /// </summary>
    /// <remarks>
    /// Every method on this node's <c>spa_node</c> vtable is called from C, several of them from the
    /// data loop. A managed exception cannot unwind through those frames, so one that escapes takes
    /// the process down with it rather than failing the operation. Answering <c>-EIO</c> is what the
    /// graph is prepared to handle.
    /// </remarks>
    private static int Fault(void* obj, Exception ex, [CallerMemberName] string callback = "")
    {
        if (From(obj) is { } self)
        {
            self.LastCallbackError = ex;
            self.LogCallbackFaulted(self._name, callback, ex);
        }
        else
        {
            // Deliberately not logged: with no node behind the pointer there is no logger to log to,
            // and the -EIO is all the graph needs.
        }

        return -NativeLibc.EIO;
    }

    /// <summary>
    /// Publishes a node this process implements into the graph.
    /// </summary>
    /// <param name="ctx">A started context.</param>
    /// <param name="name">The node's name, as the graph shows it.</param>
    /// <param name="format">The one format this node offers.</param>
    /// <param name="direction">
    /// <see cref="SpaDirection.Output"/> for a source the graph reads from,
    /// <see cref="SpaDirection.Input"/> for a sink it writes to.
    /// </param>
    /// <param name="properties">Extra node properties, e.g. <c>media.class</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ctx"/> or <paramref name="format"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The context is not connected, or the export was refused.</exception>
    public static PipeWireNodeProvider Create(
        PipeWireContext ctx,
        string name,
        PipeWireExportedFormat format,
        SpaDirection direction = SpaDirection.Output,
        IReadOnlyDictionary<string, string>? properties = null
    )
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var node = new PipeWireNodeProvider(ctx, name, direction) { NegotiatedFormat = null };
        node.Initialize(format, properties);
        return node;
    }

    /// <summary>
    /// Loads a SPA factory and publishes the node it provides, rather than implementing one here.
    /// </summary>
    /// <param name="ctx">A started context.</param>
    /// <param name="factoryName">The SPA factory, e.g. <c>audiotestsrc</c> or <c>api.v4l2.source</c>.</param>
    /// <param name="properties">Properties for both the factory and the exported node.</param>
    /// <param name="libraryName">
    /// The SPA library to load the factory from, e.g. <c>audiotestsrc/libspa-audiotestsrc</c>.
    /// Optional: when null the factory is resolved through the context's <c>context.spa-libs</c>
    /// map, which on a client covers only a handful of prefixes and will not find most factories.
    /// </param>
    /// <returns>A handle whose disposal removes the node from the graph.</returns>
    /// <remarks>
    /// <para>
    /// What upstream's <c>export-spa</c> does, and the mechanism behind <c>export-spa-device</c> and
    /// <c>bluez-session</c>: the node already exists inside a SPA plugin, so instead of answering
    /// the graph's questions this process just hands the plugin's interface over.
    /// </para>
    /// <para>
    /// Nothing about the media is described here because nothing about it is ours - the factory's
    /// node answers for its own formats and buffers, which is why there is no format argument.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="ctx"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="factoryName"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// The factory is not installed, provides no node interface, or the export was refused.
    /// </exception>
    public static PipeWireNodeProvider FromSpaFactory(
        PipeWireContext ctx,
        string factoryName,
        IReadOnlyDictionary<string, string>? properties = null,
        string? libraryName = null
    )
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(factoryName);

        var node = new PipeWireNodeProvider(ctx, factoryName, SpaDirection.Output);
        node._proxy = SpaFactoryExport.Load(
            ctx,
            factoryName,
            NativeConstants.SPA_TYPE_INTERFACE_Node,
            "node",
            properties,
            libraryName,
            out node._spaHandle
        );
        return node;
    }

    private void Initialize(
        PipeWireExportedFormat format,
        IReadOnlyDictionary<string, string>? properties
    )
    {
        OfferedFormat = format;

        // The data loop is the one pw_context runs exported nodes' process() on. Held so io updates
        // can take its lock rather than race a cycle in flight.
        pw_data_loop* dataLoop = Native.pw_context_get_data_loop(_ctx.ContextHandle);
        _dataLoop = dataLoop is null ? null : Native.pw_data_loop_get_loop(dataLoop);

        // Everything the graph may call into has to outlive this call and be reachable from a raw
        // pointer, so the managed instance is pinned by handle and the native structs live in
        // unmanaged memory rather than on the GC heap.
        _self = GCHandle.Alloc(this);

        _methods = (spa_node_methods*)NativeMemory.AllocZeroed((nuint)sizeof(spa_node_methods));
        _methods->version = NativeConstants.SPA_VERSION_NODE_METHODS;
        _methods->add_listener = &OnAddListener;
        _methods->set_callbacks = &OnSetCallbacks;
        _methods->enum_params = &OnEnumParams;
        _methods->set_param = &OnSetParam;
        _methods->set_io = &OnSetIo;
        _methods->send_command = &OnSendCommand;
        _methods->port_enum_params = &OnPortEnumParams;
        _methods->port_set_param = &OnPortSetParam;
        _methods->port_use_buffers = &OnPortUseBuffers;
        _methods->port_reuse_buffer = &OnPortReuseBuffer;
        _methods->port_set_io = &OnPortSetIo;
        _methods->process = &OnProcess;

        _node = (spa_node*)NativeMemory.AllocZeroed((nuint)sizeof(spa_node));

        _hooks = (spa_hook_list*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook_list));
        SpaHookList.Init(_hooks);

        // The pointer is kept past the fixed block deliberately: a u8 literal is a span over
        // static data in the assembly image, which never moves, so pinning it is a formality
        // rather than something the stored pointer depends on.
        fixed (byte* type = NativeConstants.SPA_TYPE_INTERFACE_Node)
        {
            _node->iface.type = (sbyte*)type;
            _node->iface.version = NativeConstants.SPA_VERSION_NODE;
            _node->iface.cb.funcs = _methods;
            _node->iface.cb.data = (void*)GCHandle.ToIntPtr(_self);
        }

        var props = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipeWireKeys.PW_KEY_NODE_NAME] = _name,
        };

        if (properties is not null)
        {
            foreach (KeyValuePair<string, string> pair in properties)
                props[pair.Key] = pair.Value;
        }

        // Wrapped in the adapter, the way pw_stream wraps its own node for audio and video
        // (stream.c: find the "adapter" factory, point adapt.follower.spa-node at the spa_node, do not
        // register it locally, export the resulting pw_impl_node). Exported bare, the node is a raw
        // interleaved port with no node-level EnumFormat and no PortConfig, and WirePlumber's audio
        // session item refuses it outright - "no usable format found for node" - so nothing is ever
        // routed to it. The adapter puts audioconvert in front of this node: it answers the node-level
        // parameters, takes PortConfig, offers the DSP ports every other audio node offers, and drives
        // this node as its follower, the same role alsa-pcm plays inside a sound card's node.
        _nodeProperties = [.. props];

        props["adapt.follower.spa-node"] = $"pointer:0x{(nuint)_node:x}";
        props[PipeWireKeys.PW_KEY_OBJECT_REGISTER] = "false";

        using (_ctx.Lock())
        {
            pw_core* core = _ctx.CoreHandle;
            if (core is null)
                throw new InvalidOperationException("the context is not connected.");

            pw_impl_factory* factory;
            fixed (byte* adapter = "adapter"u8)
                factory = Native.pw_context_find_factory(_ctx.ContextHandle, (sbyte*)adapter);

            if (factory is null)
            {
                Cleanup();
                throw new InvalidOperationException(
                    "the context has no adapter factory (libpipewire-module-adapter), which every "
                        + "stream needs too; the client configuration does not load it."
                );
            }

            Span<byte> scratch = stackalloc byte[2048];
            Span<spa_dict_item> items = stackalloc spa_dict_item[24];
            var dict = new SpaDictBuilder(scratch, items);
            foreach (KeyValuePair<string, string> pair in props)
                dict.Add(pair.Key, pair.Value);
            spa_dict built = dict.Build();

            // The factory takes ownership of the properties, as stream.c's does.
            pw_properties* owned = Native.pw_properties_new_dict(&built);

            fixed (byte* type = NativeConstants.PW_TYPE_INTERFACE_Node)
            {
                _adapter = (pw_impl_node*)
                    Native.pw_impl_factory_create_object(
                        factory,
                        null,
                        (sbyte*)type,
                        NativeConstants.PW_VERSION_NODE,
                        owned,
                        0
                    );
            }

            if (_adapter is null)
            {
                int errno = Marshal.GetLastSystemError();
                Cleanup();
                throw new InvalidOperationException(
                    $"the adapter factory refused the node '{_name}' (errno {errno})."
                );
            }

            _ = Native.pw_impl_node_set_active(_adapter, true);

            fixed (byte* type = NativeConstants.PW_TYPE_INTERFACE_Node)
            {
                _proxy = Native.pw_core_export(core, (sbyte*)type, null, _adapter, 0);
            }
        }

        if (_proxy is null)
        {
            Cleanup();
            throw new InvalidOperationException($"pw_core_export refused the node '{_name}'.");
        }
    }

    /// <summary>The format this node offers, as given to <see cref="Create"/>.</summary>
    public PipeWireExportedFormat? OfferedFormat { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Disposal here does no awaiting, so this and <see cref="DisposeAsync"/> do the same work.
    /// Both exist so that a caller is not forced into one idiom by which type they happen to hold.
    /// </remarks>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        using (_ctx.Lock())
        {
            // Shut the data path before the proxy goes. Destroying it is what lets PipeWire free the
            // buffer pool and the io areas, and a cycle already in flight on the data-loop thread
            // would otherwise read them afterwards - a use-after-free that surfaces as a null
            // dereference inside the process callback and takes the process down with it.
            //
            // The io goes first and under the data loop's lock. When SetBuffersIo returns, any cycle
            // that was in flight has finished, and every later one sees no io and returns before it
            // reaches the pool - so the plain stores after it cannot race a reader.
            SetBuffersIo(null);
            _bufferCount = 0;
            _buffers = null;
            _free = [];
            _position = null;

            if (_proxy is not null)
            {
                Native.pw_proxy_destroy(_proxy);
            }

            _proxy = null;

            DestroyAdapter();
        }

        Cleanup();
        return ValueTask.CompletedTask;
    }

    /// <summary>Destroys the adapter this node follows, which unhooks it from this node.</summary>
    /// <remarks>
    /// Before this node's own memory goes, and under the loop lock: the adapter's follower listener is
    /// linked into <see cref="_hooks"/>, and destroying the adapter is what unlinks it - so the list,
    /// and the node the adapter still points at, must both still be there. stream.c destroys its node
    /// the same way, after the proxy.
    /// </remarks>
    private void DestroyAdapter()
    {
        if (_adapter is null)
            return;

        Native.pw_impl_node_destroy(_adapter);
        _adapter = null;
    }

    private void Cleanup()
    {
        // Only reached with the loop lock not held on the failure paths of Initialize, where the
        // adapter was never created or has just been; taking the lock again is harmless (recursive).
        if (_adapter is not null)
        {
            using (_ctx.Lock())
                DestroyAdapter();
        }

        // The factory's node lives in the handle, so clearing it is what actually releases the
        // plugin's resources; the proxy only removed it from the graph.
        if (_spaHandle is not null)
        {
            if (_spaHandle->clear is not null)
                _ = _spaHandle->clear(_spaHandle);
            _spaHandle = null;
        }

        if (_node is not null)
        {
            NativeMemory.Free(_node);
            _node = null;
        }
        if (_hooks is not null)
        {
            NativeMemory.Free(_hooks);
            _hooks = null;
        }
        if (_methods is not null)
        {
            NativeMemory.Free(_methods);
            _methods = null;
        }
        if (_self.IsAllocated)
            _self.Free();
    }

    private static PipeWireNodeProvider? From(void* data) =>
        data is null ? null : GCHandle.FromIntPtr((nint)data).Target as PipeWireNodeProvider;

    // - The vtable -
    //
    // Static, because a function pointer into managed code cannot close over an instance; the
    // instance arrives as the `object` argument, which is the cb.data set at export time.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnAddListener(void* obj, spa_hook* hook, spa_node_events* events, void* data)
    {
        try
        {
            PipeWireNodeProvider? self = From(obj);
            if (self is null)
                return -NativeLibc.EINVAL;

            if (self._hooks is null || hook is null)
            {
                self.LogListenerRefused(self._name);
                return -NativeLibc.EINVAL;
            }

            // export-source.c's impl_add_listener: the new listener is isolated so the full info goes
            // to it alone, then the others are joined back. Emitting to everyone instead would repeat
            // the whole description to listeners that already have it.
            //
            // And the description is the part that is easy to leave out and impossible to diagnose from
            // the outside: a node that never emits port info is a node the graph believes has no ports,
            // so it never asks for a format, never allocates buffers and never schedules the node.
            spa_hook_list save;
            SpaHookList.Isolate(self._hooks, &save, hook, events, data);
            try
            {
                self.EmitInfo();
            }
            finally
            {
                SpaHookList.Join(self._hooks, &save);
            }

            return 0;
        }
        catch (Exception ex)
        {
            return Fault(obj, ex);
        }
    }

    /// <summary>Tells the graph what this node is and what port it has.</summary>
    private void EmitInfo()
    {
        EmitNodeInfo();
        EmitPortInfo();
    }

    /// <summary>The node half: one port in one direction, processed in realtime, and its properties.</summary>
    /// <remarks>
    /// <para>
    /// Flags and properties, no node-level parameters: setting the params-changed bit with an empty
    /// list tells the graph its parameter list changed to nothing. The flags say the port count and
    /// that process() is realtime-safe, which is what <see cref="SpaNodeFlags.Rt"/> means.
    /// </para>
    /// <para>
    /// The properties are not optional for a follower. The adapter's first act is to read them -
    /// module-adapter's <c>info_event</c> calls <c>pw_properties_update(d->props, info->props)</c>
    /// without checking for null - and a node info without them crashes the process inside the
    /// adapter factory. <c>pw_stream</c> reports its stream properties here for the same reason.
    /// </para>
    /// </remarks>
    private void EmitNodeInfo()
    {
        if (_hooks is null)
            return;

        Span<byte> scratch = stackalloc byte[4096];
        Span<spa_dict_item> items = stackalloc spa_dict_item[32];
        var dict = new SpaDictBuilder(scratch, items);
        foreach (KeyValuePair<string, string> pair in _nodeProperties)
        {
            if (dict.Count == items.Length)
                break;
            dict.Add(pair.Key, pair.Value);
        }

        spa_dict props = dict.Build();

        bool output = _direction == SpaDirection.Output;
        var info = new spa_node_info
        {
            max_input_ports = output ? 0u : 1u,
            max_output_ports = output ? 1u : 0u,
            change_mask = (ulong)(SpaNodeChangeMask.Flags | SpaNodeChangeMask.Props),
            flags = (ulong)SpaNodeFlags.Rt,
            props = &props,
        };

        // spa_node_emit_info: every hook, safe against one removing itself.
        for (spa_list* l = _hooks->list.next, next; l != &_hooks->list; l = next)
        {
            next = l->next;
            var h = (spa_hook*)l;
            var ev = (spa_node_events*)h->cb.funcs;
            if (ev is not null && ev->info is not null)
                ev->info(h->cb.data, &info);
        }
    }

    /// <summary>The port half: which parameters exist and which may be read or written now.</summary>
    /// <remarks>
    /// The same five parameters, in the same order and with the same access, as upstream's
    /// export-source and export-sink. <c>Format</c> is write-only and <c>Buffers</c> unreadable until a
    /// format is set; setting one makes both readable, and clearing it takes that back. Upstream
    /// re-emits the port info on each change so the graph sees the new access, and so does this - it
    /// is called again from <see cref="OnPortSetParam"/>.
    /// </remarks>
    private void EmitPortInfo()
    {
        if (_hooks is null)
            return;

        bool formatSet = NegotiatedFormat is not null;

        spa_param_info* paramInfo = stackalloc spa_param_info[5];
        paramInfo[0] = new spa_param_info
        {
            id = (uint)SpaParamType.EnumFormat,
            flags = (uint)SpaParamInfoFlags.Read,
        };
        paramInfo[1] = new spa_param_info
        {
            id = (uint)SpaParamType.Meta,
            flags = (uint)SpaParamInfoFlags.Read,
        };
        paramInfo[2] = new spa_param_info
        {
            id = (uint)SpaParamType.Io,
            flags = (uint)SpaParamInfoFlags.Read,
        };
        paramInfo[3] = new spa_param_info
        {
            id = (uint)SpaParamType.Format,
            flags = (uint)(formatSet ? SpaParamInfoFlags.ReadWrite : SpaParamInfoFlags.Write),
        };
        paramInfo[4] = new spa_param_info
        {
            id = (uint)SpaParamType.Buffers,
            flags = (uint)(formatSet ? SpaParamInfoFlags.Read : SpaParamInfoFlags.None),
        };

        var port = new spa_port_info
        {
            change_mask = (ulong)(SpaPortChangeMask.Flags | SpaPortChangeMask.Params),
            flags = (ulong)SpaPortFlags.None,
            @params = paramInfo,
            n_params = 5,
        };

        // spa_node_emit_port_info.
        for (spa_list* l = _hooks->list.next, next; l != &_hooks->list; l = next)
        {
            next = l->next;
            var h = (spa_hook*)l;
            var ev = (spa_node_events*)h->cb.funcs;
            if (ev is not null && ev->port_info is not null)
                ev->port_info(h->cb.data, _direction, PortId, &port);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSetCallbacks(void* obj, spa_node_callbacks* callbacks, void* data)
    {
        try
        {
            PipeWireNodeProvider? self = From(obj);
            if (self is null)
                return -NativeLibc.EINVAL;

            self._callbacks = callbacks;
            self._callbacksData = data;
            return 0;
        }
        catch (Exception ex)
        {
            return Fault(obj, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnEnumParams(
        void* obj,
        int seq,
        uint id,
        uint start,
        uint num,
        spa_pod* filter
    )
    {
        try
        {
            _ = obj;
            _ = seq;
            _ = id;
            _ = start;
            _ = num;
            _ = filter;
            return 0;
        }
        catch (Exception ex)
        {
            return Fault(obj, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSetParam(void* obj, uint id, uint flags, spa_pod* param)
    {
        try
        {
            _ = obj;
            _ = id;
            _ = flags;
            _ = param;
            return 0;
        }
        catch (Exception ex)
        {
            return Fault(obj, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSetIo(void* obj, uint id, void* area, nuint size)
    {
        try
        {
            PipeWireNodeProvider? self = From(obj);
            if (self is null)
                return -NativeLibc.EINVAL;

            // Return codes are spa/node/node.h's contract for set_io, and audioconvert's behaviour:
            // -ENOSPC for an area too small to be the struct, -ENOENT for an id this node does not use.
            switch ((SpaIoType)id)
            {
                case SpaIoType.Position:
                    // Where the graph publishes the quantum. A node that ignores it and fills whole
                    // buffers produces far more than a cycle asked for, and what the consumer reads
                    // back looks like reordering rather than like overproduction. A plain store, as
                    // in audioconvert: the pointer is swapped whole and read once per cycle.
                    if (area is not null && size < (nuint)sizeof(spa_io_position))
                    {
                        self.LogIoAreaTooSmall(self._name, (SpaIoType)id, size);
                        return -NativeLibc.ENOSPC;
                    }

                    self._position = (spa_io_position*)area;
                    return 0;

                case SpaIoType.Clock:
                    // Offered to every node; only a driver writes it, and this node never drives.
                    return 0;

                default:
                    self.LogIoRefused(self._name, (SpaIoType)id);
                    return -NativeLibc.ENOENT;
            }
        }
        catch (Exception ex)
        {
            return Fault(obj, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSendCommand(void* obj, spa_command* command)
    {
        try
        {
            _ = obj;
            _ = command;
            return 0;
        }
        catch (Exception ex)
        {
            return Fault(obj, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPortSetIo(
        void* obj,
        SpaDirection direction,
        uint port,
        uint id,
        void* area,
        nuint size
    )
    {
        try
        {
            PipeWireNodeProvider? self = From(obj);
            if (self is null)
                return -NativeLibc.EINVAL;

            // SPA_IO_Buffers: where the graph says which buffer is current and reads back what this
            // node produced. Without it a source has nowhere to publish and simply never emits.
            if ((SpaIoType)id != SpaIoType.Buffers)
            {
                self.LogIoRefused(self._name, (SpaIoType)id);
                return -NativeLibc.ENOENT;
            }

            if (area is not null && size < (nuint)sizeof(spa_io_buffers))
            {
                self.LogIoAreaTooSmall(self._name, (SpaIoType)id, size);
                return -NativeLibc.ENOSPC;
            }

            self.SetBuffersIo((spa_io_buffers*)area);
            return 0;
        }
        catch (Exception ex)
        {
            return Fault(obj, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPortEnumParams(
        void* obj,
        int seq,
        SpaDirection direction,
        uint port,
        uint id,
        uint start,
        uint num,
        spa_pod* filter
    )
    {
        try
        {
            PipeWireNodeProvider? self = From(obj);
            if (self is null)
                return -NativeLibc.EINVAL;

            return self.EnumeratePortParams(seq, id, start, filter);
        }
        catch (Exception ex)
        {
            return Fault(obj, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPortSetParam(
        void* obj,
        SpaDirection direction,
        uint port,
        uint id,
        uint flags,
        spa_pod* param
    )
    {
        try
        {
            PipeWireNodeProvider? self = From(obj);
            if (self is null)
                return -NativeLibc.EINVAL;

            // A null param clears the format, which is how the peer disconnects. The id comes from the
            // generated enum rather than a literal: SPA_PARAM_ starts at Invalid = 0, so the values are
            // one higher than they look, and hardcoding them here had this branch testing for
            // EnumFormat - which meant the settled format was never recorded and negotiation silently
            // never completed.
            // spa/node/node.h: -ENOENT for a parameter id this port does not take. Format is the only
            // one a peer sets here, as in upstream's export-source.
            if (id != (uint)SpaParamType.Format)
            {
                self.LogParamRefused(self._name, (SpaParamType)id);
                return -NativeLibc.ENOENT;
            }

            if (param is null)
            {
                self.NegotiatedFormat = null;
                self.LogFormatCleared(self._name);
                self.EmitPortInfo();
                return 0;
            }

            // What the peer settled on, read back from the pod rather than assumed to be what was
            // offered. Since EnumFormat advertises choices, the agreed rate, channel count and
            // sample format can all differ from this node's preferred ones, and BytesPerFrame is
            // derived from them - a cycle sized off the offered format would then write the wrong
            // number of bytes into a buffer the graph owns.
            var settled = new ReadOnlySpan<byte>(
                param,
                checked((int)(param->size + (uint)sizeof(spa_pod)))
            );

            PipeWireExportedFormat? agreed = PipeWireExportedFormat.FromPod(settled);
            if (agreed is null)
            {
                // Not a format this node can carry.
                self.LogFormatRefused(self._name);
                return -NativeLibc.EINVAL;
            }

            self.NegotiatedFormat = agreed;
            self.LogFormatSettled(self._name, agreed.SampleFormat, agreed.Rate, agreed.Channels);
            self.EmitPortInfo();
            return 0;
        }
        catch (Exception ex)
        {
            return Fault(obj, ex);
        }
    }

    /// <summary>Replaces the buffers io area without racing a cycle that is reading it.</summary>
    /// <remarks>
    /// spa/node/node.h allows port_set_io while the node is running and says the node "must be
    /// prepared to handle changes in io areas while running ... normally done by synchronizing the
    /// port io updates with the data processing loop". This is audioconvert's way of doing it:
    /// <c>spa_loop_locked(this->data_loop, do_set_port_io, ...)</c>. Holding the loop's lock means
    /// the swap lands between cycles, never inside one that has already read the old pointer.
    /// </remarks>
    private void SetBuffersIo(spa_io_buffers* area)
    {
        if (_dataLoop is null)
        {
            _io = area;
            return;
        }

        var update = new IoUpdate { Node = (void*)GCHandle.ToIntPtr(_self), Area = area };
        _ = Native.pw_loop_locked(_dataLoop, &DoSetBuffersIo, &update);
    }

    private struct IoUpdate
    {
        public void* Node;
        public spa_io_buffers* Area;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DoSetBuffersIo(
        spa_loop* loop,
        bool async,
        uint seq,
        void* data,
        nuint size,
        void* userData
    )
    {
        // Runs under the data loop's lock, called from C: nothing may escape.
        try
        {
            var update = (IoUpdate*)userData;
            if (From(update->Node) is { } self)
                self._io = update->Area;
            return 0;
        }
        catch (Exception ex)
        {
            return Fault(((IoUpdate*)userData)->Node, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPortUseBuffers(
        void* obj,
        SpaDirection direction,
        uint port,
        uint flags,
        spa_buffer** buffers,
        uint count
    )
    {
        try
        {
            PipeWireNodeProvider? self = From(obj);
            if (self is null)
                return -NativeLibc.EINVAL;

            // The count is what the data loop gates on, so it is published last when a pool arrives and
            // first when one goes away. Assigning it before the array it describes leaves a window in
            // which a cycle running on the other thread indexes a pool that is smaller than the count
            // says, or one the graph has already freed.
            if (buffers is null || count == 0)
            {
                self._bufferCount = 0;
                self._buffers = null;
                self._free = [];
                self._nextBuffer = 0;
                return 0;
            }

            // Every buffer starts free; the graph has not taken any yet.
            var free = new bool[count];
            Array.Fill(free, true);

            self._free = free;
            self._nextBuffer = 0;
            self._buffers = buffers;
            self._bufferCount = count;
            self.LogBuffersArrived(self._name, count);
            return 0;
        }
        catch (Exception ex)
        {
            return Fault(obj, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPortReuseBuffer(void* obj, uint port, uint bufferId)
    {
        // Called on the data loop, from C: nothing may escape, and nothing here allocates.
        try
        {
            PipeWireNodeProvider? self = From(obj);
            if (self is null)
                return -NativeLibc.EINVAL;

            // Upstream's export-source: reuse_buffer puts the buffer back on its free list. Same
            // snapshot as RunCycle, and the same gate: a count that is set describes an array that
            // is already in place.
            uint bufferCount = self._bufferCount;
            bool[] free = self._free;
            if (bufferId >= bufferCount || bufferId >= (uint)free.Length)
            {
                // A peer returning a buffer this node never handed out. Rare, and a defect on one side
                // or the other, so worth the log even on the data loop.
                self.LogReuseRefused(self._name, bufferId, bufferCount);
                return -NativeLibc.EINVAL;
            }

            free[bufferId] = true;
            return 0;
        }
        catch (Exception ex)
        {
            return Fault(obj, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnProcess(void* obj)
    {
        PipeWireNodeProvider? self = From(obj);
        if (self is null)
            return -NativeLibc.EINVAL;

        // This runs on the realtime data-loop thread, called from C. A managed exception cannot
        // unwind through the native frames above it, so the runtime aborts the whole process
        // instead - the failure arrives as SIGABRT on `data-loop.0` with no test result attached,
        // which says nothing about which node or which cycle was at fault. Answering -EIO lets the
        // graph drop the cycle and carry on, and the node reports the fault where it can be read.
        try
        {
            return self.RunCycle();
        }
        catch (Exception ex)
        {
            // Logged once per kind of failure, not per cycle: this is the realtime thread, and a handler
            // that throws on every cycle would otherwise log at the graph's rate. Every occurrence is
            // still in LastProcessError.
            if (self.LastProcessError?.GetType() != ex.GetType())
                self.LogProcessFaulted(self._name, ex);
            self.LastProcessError = ex;
            return -NativeLibc.EIO;
        }
    }

    private int RunCycle()
    {
        // Before the pool check rather than after: being called with no buffers yet is still the
        // graph driving this node, and that is exactly the case a caller needs to tell apart from
        // never having been scheduled at all.
        HasBeenScheduled = true;

        // Cached for the cycle, as upstream's impl_node_process caches its own io pointer. The
        // count is the gate: OnPortUseBuffers publishes it last when a pool arrives and clears it
        // first when one goes away, so a count that is non-zero here describes an array that is
        // already in place. That ordering is what lets these be ordinary reads.
        uint bufferCount = _bufferCount;
        spa_buffer** buffers = _buffers;
        bool[] free = _free;
        spa_io_buffers* io = _io;

        bool consuming = _direction == SpaDirection.Input;

        if (io is null || buffers is null || bufferCount == 0)
            return consuming ? (int)SpaStatus.NeedData : (int)SpaStatus.Ok;

        if (consuming)
            return ConsumeCycle(io, buffers, bufferCount);
        if (ProduceTrace is not { } trace)
            return ProduceCycle(io, buffers, free, bufferCount);

        int entryStatus = io->status;
        uint entryBuffer = io->buffer_id;
        int result = ProduceCycle(io, buffers, free, bufferCount);
        uint freeCount = 0;
        foreach (bool f in free)
            if (f)
                freeCount++;
        trace[ProduceTraceCount++ % trace.Length] = new ProduceCycleRecord(
            entryStatus,
            entryBuffer,
            result == (int)SpaStatus.HaveData ? io->buffer_id : uint.MaxValue,
            result,
            freeCount,
            bufferCount
        );
        return result;
    }

    /// <summary>One source cycle as the io area saw it, for a test diagnosing lost or reused buffers.</summary>
    internal readonly record struct ProduceCycleRecord(
        int EntryStatus,
        uint EntryBuffer,
        uint Published,
        int Result,
        uint FreeAfter,
        uint Pool
    );

    /// <summary>
    /// Set by a test before the node runs to record every source cycle, round-robin. Written only by
    /// the data loop and read once the node has stopped, so it needs no synchronisation, and it costs
    /// nothing when null.
    /// </summary>
    internal ProduceCycleRecord[]? ProduceTrace { get; set; }

    internal int ProduceTraceCount;

    /// <summary>One cycle of a sink: read the buffer the graph handed over, then ask for the next.</summary>
    /// <remarks>
    /// Upstream's <c>export-sink.c</c> <c>impl_node_process</c>, step for step: nothing to read
    /// unless the status is <c>SPA_STATUS_HAVE_DATA</c> and the id is in the pool; otherwise, and
    /// after reading, the answer is <c>SPA_STATUS_NEED_DATA</c>, written to the io area as well as
    /// returned. The buffer goes back even if the handler throws: left at <c>HAVE_DATA</c>, the
    /// same buffer would be read a second time next cycle.
    /// </remarks>
    private int ConsumeCycle(spa_io_buffers* io, spa_buffer** buffers, uint bufferCount)
    {
        if (io->status != (int)SpaStatus.HaveData)
            return (int)SpaStatus.NeedData;

        uint index = io->buffer_id;
        if (index >= bufferCount)
            return (int)SpaStatus.NeedData;

        try
        {
            HasProcessed = true;

            spa_buffer* buffer = buffers[index];
            if (buffer is null || buffer->n_datas == 0 || buffer->datas is null)
                return (int)SpaStatus.NeedData;

            spa_data* d = &buffer->datas[0];
            if (d->data is null || d->chunk is null || ProcessCallback is not { } handler)
                return (int)SpaStatus.NeedData;

            // Only what the producer wrote, bounded by the mapping: the offset is clamped to the
            // block and the size to what remains after it, as every upstream reader of a chunk
            // does. Clamping the size alone reads past the block whenever the offset is non-zero.
            uint offset = Math.Min(d->chunk->offset, d->maxsize);
            uint size = Math.Min(d->chunk->size, d->maxsize - offset);
            _ = handler(this, new Span<byte>((byte*)d->data + offset, checked((int)size)));

            return (int)SpaStatus.NeedData;
        }
        finally
        {
            io->status = (int)SpaStatus.NeedData;
        }
    }

    /// <summary>One cycle of a source: recycle what the graph returned, fill a free buffer, hand it over.</summary>
    /// <remarks>
    /// <para>
    /// A buffer still published (<c>SPA_STATUS_HAVE_DATA</c>) is left where it is and the cycle
    /// produces nothing: the consumer has not taken it yet, so it is the next thing it must read.
    /// That is what spa's own sources do (<c>audiotestsrc</c>, <c>videotestsrc</c>) and what
    /// <c>pw_stream</c>'s output does (<c>stream.c</c> <c>impl_node_process_output</c>). Upstream's
    /// <c>export-source.c</c> example skips the check and recycles the unread buffer, so every cycle
    /// its consumer misses loses a whole quantum: pipewiresrc read that as a ramp jumping forward by
    /// whole buffers.
    /// </para>
    /// <para>
    /// Otherwise the buffer the graph finished with comes back in <c>buffer_id</c>, is recycled, and
    /// the field is cleared, because it is stale until the graph hands another one back; recycling
    /// it again on a cycle that runs first would hand out a buffer the consumer is still reading,
    /// which arrives as a stream intact per buffer but jumping between them. Then a free buffer is
    /// filled and published with <c>SPA_STATUS_HAVE_DATA</c>, or <c>-EPIPE</c> when every buffer is
    /// still out.
    /// </para>
    /// <para>
    /// Where this goes beyond the example is the handler, which may write nothing or throw. A
    /// buffer is marked taken only once it is published, so neither loses it from the pool; taking
    /// it first leaked one per empty cycle until every buffer looked busy and the node answered
    /// <c>-EPIPE</c> for good.
    /// </para>
    /// </remarks>
    private int ProduceCycle(
        spa_io_buffers* io,
        spa_buffer** buffers,
        bool[] free,
        uint bufferCount
    )
    {
        if (io->status == (int)SpaStatus.HaveData)
            return (int)SpaStatus.HaveData;

        if (io->buffer_id < bufferCount)
        {
            free[io->buffer_id] = true;
            io->buffer_id = NativeConstants.SPA_ID_INVALID;
        }

        uint index = uint.MaxValue;
        for (uint i = 0; i < bufferCount; i++)
        {
            uint candidate = (_nextBuffer + i) % bufferCount;
            if (!free[candidate])
                continue;

            index = candidate;
            break;
        }

        if (index == uint.MaxValue)
            return -NativeLibc.EPIPE;

        spa_buffer* buffer = buffers[index];
        if (buffer is null || buffer->n_datas == 0 || buffer->datas is null)
            return (int)SpaStatus.Ok;

        spa_data* d = &buffer->datas[0];
        if (d->data is null || d->chunk is null || ProcessCallback is not { } handler)
            return (int)SpaStatus.Ok;

        PipeWireExportedFormat? format = NegotiatedFormat ?? OfferedFormat;

        // One cycle's worth, not the whole allocation. The quantum is frames, so it is scaled by
        // the format's frame size; without a position area yet, the buffer's own size stands in.
        uint cycleBytes = d->maxsize;
        if (_position is not null && format is not null)
        {
            ulong quantum = _position->clock.duration;
            if (quantum > 0)
            {
                ulong wanted = quantum * (ulong)format.BytesPerFrame;
                if (wanted > 0 && wanted < cycleBytes)
                    cycleBytes = (uint)wanted;
            }
        }

        int written = handler(this, new Span<byte>(d->data, checked((int)cycleBytes)));
        if (written <= 0)
            return (int)SpaStatus.Ok;

        free[index] = false;
        _nextBuffer = (index + 1) % bufferCount;

        // Stride is the frame size, what spa/buffer/buffer.h means by "stride of valid data" for
        // interleaved audio and what upstream's pw_stream producers (audio-src.c) write.
        d->chunk->offset = 0;
        d->chunk->size = Math.Min((uint)written, d->maxsize);
        d->chunk->stride = format?.BytesPerFrame ?? 0;

        io->buffer_id = index;
        io->status = (int)SpaStatus.HaveData;

        HasProcessed = true;
        return (int)SpaStatus.HaveData;
    }

    /// <summary>
    /// A candidate parameter narrowed by the peer's filter, written as a pod.
    /// </summary>
    /// <returns>Bytes written, or 0 when nothing survives the filter.</returns>
    /// <remarks>
    /// Both sides become <see cref="SpaObject"/>s and are intersected with the same projection the
    /// rest of the library uses, so an exported node and a served parameter agree on what narrowing
    /// means rather than having two notions of it. An unreadable filter is treated as no constraint,
    /// as upstream does: refusing would drop the parameter on the strength of a pod we failed to parse.
    /// </remarks>
    private static int ProjectParam(
        ReadOnlySpan<byte> candidate,
        spa_pod* filter,
        Span<byte> destination
    )
    {
        if (
            !SpaPod.TryParse(candidate, out SpaValue? candidateValue)
            || candidateValue is not SpaObject offered
        )
            return 0;

        var filterBytes = new ReadOnlySpan<byte>(
            filter,
            checked((int)(filter->size + (uint)sizeof(spa_pod)))
        );

        if (
            !SpaPod.TryParse(filterBytes, out SpaValue? filterValue)
            || filterValue is not SpaObject wanted
        )
        {
            if (candidate.Length > destination.Length)
                return 0;
            candidate.CopyTo(destination);
            return candidate.Length;
        }

        SpaObject? narrowed = SpaPodProjection.Project(offered, wanted);
        if (narrowed is null)
            return 0;

        return SpaPod.TryWrite(narrowed, destination, out int projectedLength)
            ? projectedLength
            : 0;
    }

    /// <summary>Delivers one enumerated parameter to every listener, as spa_node_emit_result does.</summary>
    private int EmitParam(int seq, uint id, uint start, ReadOnlySpan<byte> pod)
    {
        if (_hooks is null)
            return 0;

        fixed (byte* p = pod)
        {
            var result = new spa_result_node_params
            {
                id = id,
                index = start,
                next = start + 1,
                param = (spa_pod*)p,
            };

            for (spa_list* l = _hooks->list.next, next; l != &_hooks->list; l = next)
            {
                next = l->next;
                var h = (spa_hook*)l;
                var ev = (spa_node_events*)h->cb.funcs;
                if (ev is not null && ev->result is not null)
                    ev->result(
                        h->cb.data,
                        seq,
                        0,
                        (uint)NativeConstants.SPA_RESULT_TYPE_NODE_PARAMS,
                        &result
                    );
            }
        }

        return 0;
    }

    /// <summary>Answers the graph's port parameter enumeration.</summary>
    /// <remarks>
    /// <para>
    /// Shaped on upstream's export-source <c>impl_port_enum_params</c>: one candidate per parameter
    /// id, so index 0 is the whole answer and any later start is an empty enumeration rather than an
    /// error; <c>Format</c> answers nothing until one is set; and an id the port does not have is
    /// <c>-ENOENT</c>.
    /// </para>
    /// <para>
    /// The peer's filter narrows every candidate, not only the formats, as upstream runs
    /// <c>spa_pod_filter</c> over whatever it built. A candidate the filter rules out is not offered,
    /// which is what upstream's skip to the next index amounts to when there is no next index.
    /// </para>
    /// </remarks>
    private int EnumeratePortParams(int seq, uint id, uint start, spa_pod* filter)
    {
        if (start > 0)
            return 0;

        Span<byte> candidate = stackalloc byte[2048];
        int length = (SpaParamType)id switch
        {
            SpaParamType.EnumFormat => OfferedFormat!.WriteFormat(candidate, asEnum: true),
            SpaParamType.Format => NegotiatedFormat is { } settled
                ? settled.WriteFormat(candidate, asEnum: false)
                : 0,
            SpaParamType.Buffers => (NegotiatedFormat ?? OfferedFormat)!.WriteBuffers(candidate),
            SpaParamType.Meta => PipeWireExportedFormat.WriteMetaHeader(candidate),
            SpaParamType.Io => PipeWireExportedFormat.WriteIoBuffers(candidate),
            _ => -1,
        };

        if (length < 0)
            return -NativeLibc.ENOENT;
        if (length == 0)
            return 0;

        if (filter is null)
            return EmitParam(seq, id, start, candidate[..length]);

        Span<byte> narrowed = stackalloc byte[2048];
        int projected = ProjectParam(candidate[..length], filter, narrowed);
        return projected > 0 ? EmitParam(seq, id, start, narrowed[..projected]) : 0;
    }

    [LoggerMessage(
        EventId = 34600,
        Level = LogLevel.Error,
        Message = "exported node '{Name}': its {Callback} callback threw; the graph was answered -EIO"
    )]
    private partial void LogCallbackFaulted(string name, string callback, Exception exception);

    [LoggerMessage(
        EventId = 34601,
        Level = LogLevel.Error,
        Message = "exported node '{Name}': the process handler threw; the cycle was dropped (later failures of the same kind are only recorded in LastProcessError)"
    )]
    private partial void LogProcessFaulted(string name, Exception exception);

    [LoggerMessage(
        EventId = 34602,
        Level = LogLevel.Warning,
        Message = "exported node '{Name}': refused a listener (no hook list, or a null hook)"
    )]
    private partial void LogListenerRefused(string name);

    [LoggerMessage(
        EventId = 34603,
        Level = LogLevel.Debug,
        Message = "exported node '{Name}': io area {Io} is not one it uses"
    )]
    private partial void LogIoRefused(string name, SpaIoType io);

    [LoggerMessage(
        EventId = 34604,
        Level = LogLevel.Warning,
        Message = "exported node '{Name}': io area {Io} is {Size} bytes, too small for the struct"
    )]
    private partial void LogIoAreaTooSmall(string name, SpaIoType io, nuint size);

    [LoggerMessage(
        EventId = 34605,
        Level = LogLevel.Debug,
        Message = "exported node '{Name}': parameter {Param} cannot be set on its port"
    )]
    private partial void LogParamRefused(string name, SpaParamType param);

    [LoggerMessage(
        EventId = 34606,
        Level = LogLevel.Warning,
        Message = "exported node '{Name}': refused a format it cannot carry"
    )]
    private partial void LogFormatRefused(string name);

    [LoggerMessage(
        EventId = 34607,
        Level = LogLevel.Debug,
        Message = "exported node '{Name}': format settled on {SampleFormat} {Rate} Hz x{Channels}"
    )]
    private partial void LogFormatSettled(
        string name,
        SpaAudioFormat sampleFormat,
        int rate,
        int channels
    );

    [LoggerMessage(
        EventId = 34608,
        Level = LogLevel.Debug,
        Message = "exported node '{Name}': format cleared"
    )]
    private partial void LogFormatCleared(string name);

    [LoggerMessage(
        EventId = 34609,
        Level = LogLevel.Debug,
        Message = "exported node '{Name}': the peer allocated {Count} buffers"
    )]
    private partial void LogBuffersArrived(string name, uint count);

    [LoggerMessage(
        EventId = 34610,
        Level = LogLevel.Warning,
        Message = "exported node '{Name}': the peer returned buffer {BufferId}, outside its pool of {Count}"
    )]
    private partial void LogReuseRefused(string name, uint bufferId, uint count);
}
