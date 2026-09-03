using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// A device this process serves, so other clients see a card they can select a profile on.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="PipeWireDeviceControl"/>, which is a client of somebody else's
/// device. This hosts one: the process implements <c>spa_device</c>, exports it, and the daemon
/// publishes it as a global that appears in a mixer alongside real hardware.
/// <para>
/// <b>What this does not do</b> is publish the device's child nodes. A real card announces the PCM
/// nodes it provides through <c>object_info</c>, and each of those is a node implementation and a
/// node export of its own. A device announcing no objects is legal and selectable, and reports its
/// profiles and routes, but it carries no audio. For audio use
/// <see cref="PipeWireRegistry.CreateVirtualNode"/>.
/// </para>
/// <para>
/// Needs <c>libpipewire-module-client-device</c>, which <c>client.conf</c> loads unless
/// <c>module.client-device</c> is turned off. Without it the export has no type to go through and
/// this reports that rather than appearing to work.
/// </para>
/// <para>
/// <b>Dispose it; do not let it fall out of scope.</b> The callbacks the daemon holds refer back
/// here through a weak handle, so an instance the application drops is collected and simply stops
/// answering, with no error. Disposal is what withdraws the device.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class PipeWireDeviceProvider : IDisposable
{
    private readonly PipeWireContext _ctx;
    private readonly string _name;
    private readonly ILogger _logger;

    // The parameters this device answers with, by parameter id. Immutable per publication: the
    // daemon reads them from a callback, and a collection changing underneath that read is a
    // realtime-thread hazard rather than a merely racy one.
    private ImmutableDictionary<SpaParamType, ImmutableArray<SpaObject>> _params =
        ImmutableDictionary<SpaParamType, ImmutableArray<SpaObject>>.Empty;

    private ImmutableArray<KeyValuePair<string, string>> _properties;

    // Unmanaged, because the daemon holds pointers to all of it for as long as the device is
    // exported. A managed field would move under a compacting GC.
    private spa_interface* _iface;
    private spa_device_methods* _methods;
    private spa_hook_list* _listeners;
    private spa_param_info* _paramInfo;
    private uint _paramInfoCount;

    private GCHandle _self;
    private PipeWireProxyHandle? _exported;
    private volatile bool _disposed;

    private PipeWireDeviceProvider(PipeWireContext ctx, string name, ILogger logger)
    {
        _ctx = ctx;
        _name = name;
        _logger = logger;
    }

    /// <summary>Creates and exports a device.</summary>
    /// <param name="context">A started context.</param>
    /// <param name="name">The device's <c>device.name</c>.</param>
    /// <param name="description">What a mixer shows for it.</param>
    /// <param name="parameters">
    /// The parameters it answers with, keyed by parameter id. <c>EnumProfile</c> and
    /// <c>Profile</c> are what make it selectable; <c>EnumRoute</c> and <c>Route</c> give it
    /// ports.
    /// </param>
    /// <param name="properties">Extra device properties, or null.</param>
    /// <returns>The provider, which serves the device until disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="PipeWireException">The daemon refused the export.</exception>
    public static PipeWireDeviceProvider Create(
        PipeWireContext context,
        string name,
        string description,
        IReadOnlyDictionary<SpaParamType, ImmutableArray<SpaObject>>? parameters = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(description);

        var provider = new PipeWireDeviceProvider(
            context, name, context.LoggerFactory.CreateLogger($"PipeWire.NET.Device.{name}"));

        var props = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        props.Add(new("device.name", name));
        props.Add(new("device.description", description));
        props.Add(new("device.api", "pwnet"));
        props.Add(new("media.class", "Audio/Device"));
        if (properties is not null)
        {
            foreach (KeyValuePair<string, string> pair in properties)
            {
                if (pair.Key is "device.name" or "device.description") continue;
                props.Add(pair);
            }
        }

        provider._properties = props.ToImmutable();

        if (parameters is not null)
        {
            provider._params = parameters.ToImmutableDictionary(
                static p => p.Key, static p => p.Value);
        }

        provider.Publish();
        return provider;
    }

    /// <summary>The <c>device.name</c> this was created with.</summary>
    public string Name => _name;

    private void Publish()
    {
        // Everything the daemon will hold a pointer to, allocated once and freed only in Dispose
        // after the proxy that refers to it has gone.
        _methods = (spa_device_methods*)NativeMemory.AllocZeroed((nuint)sizeof(spa_device_methods));
        _methods->version = SpaDevice.VersionMethods;
        _methods->add_listener = &OnAddListener;
        _methods->sync = &OnSync;
        _methods->enum_params = &OnEnumParams;
        _methods->set_param = &OnSetParam;

        _listeners = (spa_hook_list*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook_list));
        _listeners->list.next = &_listeners->list;
        _listeners->list.prev = &_listeners->list;

        // One entry per parameter this device answers, so the daemon knows what to ask for. A
        // device that lists nothing is never asked anything, whatever its methods would return.
        _paramInfoCount = (uint)_params.Count;
        if (_paramInfoCount > 0)
        {
            _paramInfo = (spa_param_info*)NativeMemory.AllocZeroed(
                (nuint)(sizeof(spa_param_info) * (int)_paramInfoCount));

            int i = 0;
            foreach (SpaParamType id in _params.Keys)
            {
                _paramInfo[i].id = (uint)id;
                // Readable, and writable for the two a caller is expected to change.
                _paramInfo[i].flags = id is SpaParamType.Profile or SpaParamType.Route
                    ? SpaParamInfoFlags.Serial | SpaParamInfoFlags.Read | SpaParamInfoFlags.Write
                    : SpaParamInfoFlags.Serial | SpaParamInfoFlags.Read;
                i++;
            }
        }

        _self = GCHandle.Alloc(this, GCHandleType.Weak);

        _iface = (spa_interface*)NativeMemory.AllocZeroed((nuint)sizeof(spa_interface));
        _iface->version = SpaDevice.Version;
        _iface->cb.funcs = _methods;
        _iface->cb.data = (void*)GCHandle.ToIntPtr(_self);

        ReadOnlySpan<byte> typeUtf8 = Encoding.UTF8.GetBytes(SpaDevice.InterfaceType + '\0');
        _iface->type = (sbyte*)NativeMemory.Alloc((nuint)typeUtf8.Length);
        typeUtf8.CopyTo(new Span<byte>(_iface->type, typeUtf8.Length));

        pw_proxy* exported;
        using (_ctx.Lock())
        {
            Span<byte> scratch = stackalloc byte[4096];
            Span<spa_dict_item> items = stackalloc spa_dict_item[32];
            var dict = new SpaDictBuilder(scratch, items);
            foreach (KeyValuePair<string, string> pair in _properties)
            {
                if (dict.Count == items.Length) break;
                dict.Add(pair.Key, pair.Value);
            }

            spa_dict native = dict.Build();

            fixed (byte* t = typeUtf8)
            {
                exported = Native.pw_core_export(
                    _ctx.CoreHandle, (sbyte*)t, &native, _iface, 0);
            }
        }

        if (exported is null)
        {
            ReleaseNative();
            throw new PipeWireException(
                "pw_core_export",
                -38,
                daemonMessage:
                "the context has no export type for Device. libpipewire-module-client-device "
                + "registers it, and client.conf loads it unless module.client-device is off.");
        }

        _exported = new PipeWireProxyHandle(exported, _ctx.LoopOwner, _ctx.CoreOwner!);

        // A second info, and it is not redundant. The first one is emitted from add_listener during
        // the export, which is what makes the daemon create the pw_impl_device for this device - so
        // at the moment it is sent there is no impl device listening, and the parameter list in it
        // reaches nothing. The impl device attaches its listener to the daemon-side resource rather
        // than to us, so our add_listener is never called again and nothing else would resend it.
        // Messages are processed in order on one connection, so this one arrives after the
        // registration the first one triggered.
        using (_ctx.Lock())
            EmitInfoLocked();

        LogExported(_name);
    }

    /// <summary>Replaces a parameter's values, and tells the daemon it changed.</summary>
    /// <param name="parameter">Which parameter.</param>
    /// <param name="values">Its new values.</param>
    /// <remarks>
    /// The daemon caches what the first enumeration answered and re-reads only when the
    /// parameter flags change, so the update toggles the <c>SERIAL</c> bit for this parameter:
    /// the bit exists to signal an update when the read and write flags do not change, and the
    /// toggle is what drops the daemon's cache and makes it ask again. A parameter not listed
    /// at construction is added to the set but is not announced, because the parameter list the
    /// daemon holds was fixed when the device was exported.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The provider has been disposed.</exception>
    public void SetParameter(SpaParamType parameter, ImmutableArray<SpaObject> values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _params = _params.SetItem(parameter, values);

        // Toggle and emit under one hold of the loop lock: the flags live in native memory the
        // loop thread reads while emitting, so toggling outside the lock races the read.
        using (_ctx.Lock())
        {
            if (_disposed || _listeners is null) return;
            NoteParamsChanged(parameter);
            EmitInfoLocked();
        }
    }

    // Toggles the SERIAL bit for a declared parameter: the daemon caches the first enumeration
    // and re-reads only when the flags change, so without this a new value sits in _params while
    // every other client keeps the old one. A single aligned word write; the loop thread only
    // ever reads this memory.
    private void NoteParamsChanged(SpaParamType parameter)
    {
        if (_paramInfo is not null)
        {
            for (uint i = 0; i < _paramInfoCount; i++)
            {
                if (_paramInfo[i].id == (uint)parameter)
                {
                    _paramInfo[i].flags ^= (uint)SpaParamInfoFlags.Serial;
                    break;
                }
            }
        }
    }

    /// <summary>The parameters this device currently answers with.</summary>
    public ImmutableArray<SpaObject> GetParameter(SpaParamType parameter) =>
        _params.TryGetValue(parameter, out ImmutableArray<SpaObject> values) ? values : [];

    /// <summary>Raised when a client writes a parameter, so the host can accept or ignore it.</summary>
    /// <remarks>
    /// Raised on the loop thread. A handler that throws is logged and the write is reported as
    /// accepted anyway, because the daemon has no way to be told otherwise at that point.
    /// </remarks>
    public event Action<PipeWireDeviceProvider, SpaParamType, SpaObject?>? ParameterWritten;

    private void EmitInfo()
    {
        if (_disposed || _listeners is null) return;

        using (_ctx.Lock())
            EmitInfoLocked();
    }

    private void EmitInfoLocked()
    {
        Span<byte> scratch = stackalloc byte[4096];
        Span<spa_dict_item> items = stackalloc spa_dict_item[32];
        var dict = new SpaDictBuilder(scratch, items);
        foreach (KeyValuePair<string, string> pair in _properties)
        {
            if (dict.Count == items.Length) break;
            dict.Add(pair.Key, pair.Value);
        }

        spa_dict native = dict.Build();

        spa_device_info info = default;
        info.version = SpaDevice.VersionInfo;
        info.change_mask = SpaDevice.ChangeMaskProps | SpaDevice.ChangeMaskParams;
        info.props = &native;
        info.@params = _paramInfo;
        info.n_params = _paramInfoCount;

        for (spa_list* node = _listeners->list.next;
             node != &_listeners->list;
             node = node->next)
        {
            var hook = (spa_hook*)node;
            var events = (spa_device_events*)hook->cb.funcs;
            if (events is null || events->info is null) continue;

            events->info(hook->cb.data, &info);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnAddListener(
        void* obj, spa_hook* listener, spa_device_events* events, void* data)
    {
        PipeWireDeviceProvider? self = FromData(obj);
        if (self is null || listener is null) return -22;

        try
        {
            // Zeroed then linked at the tail, which is what spa_hook_list_append does. The hook
            // belongs to the caller; only its links and callbacks are written here.
            NativeMemory.Clear(listener, (nuint)sizeof(spa_hook));
            listener->cb.funcs = events;
            listener->cb.data = data;

            spa_list* tail = self._listeners->list.prev;
            listener->link.prev = tail;
            listener->link.next = &self._listeners->list;
            tail->next = &listener->link;
            self._listeners->list.prev = &listener->link;

            // The header's contract: attaching a listener triggers info, and an object_info per
            // managed object. This device manages none, so info alone.
            self.LogListenerAttached();
            self.EmitInfoLocked();
            return 0;
        }
        catch (Exception ex)
        {
            self.LogCallbackThrew(nameof(OnAddListener), ex);
            return -5;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSync(void* obj, int seq)
    {
        PipeWireDeviceProvider? self = FromData(obj);
        if (self is null) return -22;

        try
        {
            // The daemon parks the client that triggered an async exchange until this result
            // arrives, and stops reading that connection meanwhile, so answering it is what
            // unblocks the requester rather than a formality. Every real device emits it
            // synchronously with no payload and returns zero.
            self.EmitResult(seq, 0, 0, null);
            return 0;
        }
        catch (Exception ex)
        {
            self.LogCallbackThrew(nameof(OnSync), ex);
            return -5;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnEnumParams(
        void* obj, int seq, uint id, uint start, uint num, spa_pod* filter)
    {
        PipeWireDeviceProvider? self = FromData(obj);
        if (self is null) return -22;

        try
        {
            self.LogEnumParams(id, start, num);
            if (!self._params.TryGetValue((SpaParamType)id, out ImmutableArray<SpaObject> values))
                return 0;

            SpaObject? wanted = ParseFilter(filter);

            uint sent = 0;
            for (uint index = start; index < values.Length && sent < num; index++)
            {
                // Skipped candidates still consume their index: next names a position in the
                // whole set, not in the matches, which is what lets a caller page through a
                // filtered enumeration the same way it pages through an unfiltered one. Only
                // reported ones consume the count.
                if (wanted is not null && !MatchesFilter(values[(int)index], wanted))
                    continue;

                byte[] pod = SpaPod.ToBytes(values[(int)index]);

                fixed (byte* p = pod)
                {
                    spa_result_device_params result = default;
                    result.id = id;
                    result.index = index;
                    result.next = index + 1;
                    result.param = (spa_pod*)p;

                    self.EmitResult(seq, 0, SpaDevice.ResultTypeParams, &result);
                }

                sent++;
            }

            self.LogEnumSent(sent, self.ListenerCount);
            return 0;
        }
        catch (Exception ex)
        {
            self.LogCallbackThrew(nameof(OnEnumParams), ex);
            return -5;
        }
    }

    /// <summary>Reads an enumeration filter pod, or null when there is none to apply.</summary>
    private static unsafe SpaObject? ParseFilter(spa_pod* filter)
    {
        if (filter is null) return null;

        // The declared size is the caller's word, capped before a span is built over it the same
        // way every other length off the wire is. An unparseable filter is ignored rather than
        // failing the enumeration: the caller still gets the unfiltered set it would have had.
        uint size = filter->size;
        if (size > MaxParamBytes) return null;

        var bytes = new ReadOnlySpan<byte>(filter, (int)size + 8);
        return SpaPod.TryParse(bytes, out SpaValue? parsed) && parsed is SpaObject o ? o : null;
    }

    /// <summary>Whether a candidate satisfies every constraint of a filter object.</summary>
    /// <remarks>
    /// Full upstream filter semantics (scalar sets, ranges, steps, flags, nested objects and
    /// structs) live in <see cref="SpaPodFilter"/>; this is the call site, not a second
    /// implementation. Anything it cannot decide fails closed per side exactly as upstream
    /// fails the item, never by accepting blindly.
    /// </remarks>
    private static bool MatchesFilter(SpaObject candidate, SpaObject filter) =>
        SpaPodFilter.Matches(candidate, filter);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSetParam(void* obj, uint id, uint flags, spa_pod* param)
    {
        PipeWireDeviceProvider? self = FromData(obj);
        if (self is null) return -22;

        try
        {
            SpaObject? value = null;
            if (param is not null)
            {
                // The declared size is the daemon's word, capped before a span is built over it the
                // same way every other length off the wire is.
                uint size = ((spa_pod*)param)->size;
                if (size <= MaxParamBytes)
                {
                    var bytes = new ReadOnlySpan<byte>(param, (int)size + 8);
                    if (SpaPod.TryParse(bytes, out SpaValue? parsed) && parsed is SpaObject o)
                        value = o;
                }
            }

            self.RaiseParameterWritten((SpaParamType)id, value);

            // Stored, not just announced: a client that writes and reads back must see its own
            // write, with or without the host mediating through SetParameter. The swap is atomic
            // (immutable map), so the loop thread serving enumerations never sees a torn set.
            // Schema stays publish-time - an id the device did not declare is answered but not
            // announced, exactly as SetParameter documents.
            //
            // And announced like a local write: the daemon caches enumerations and re-reads only
            // on a SERIAL change, so without the toggle and the info event every other client
            // keeps the old value while this map holds the new one. Toggle and emit share one
            // hold of the loop lock: the flags live in native memory the emit reads, and this
            // callback already runs on the loop thread, whose mutex is recursive.
            if (value is not null)
            {
                self._params = self._params.SetItem((SpaParamType)id, [value]);
                using (self._ctx.Lock())
                {
                    self.NoteParamsChanged((SpaParamType)id);
                    if (!self._disposed && self._listeners is not null)
                        self.EmitInfoLocked();
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            self.LogCallbackThrew(nameof(OnSetParam), ex);
            return -5;
        }
    }

    /// <summary>A ceiling on a parameter pod a client sends, so a wrong size cannot be spanned.</summary>
    private const uint MaxParamBytes = 64 * 1024;

    private void RaiseParameterWritten(SpaParamType id, SpaObject? value) =>
        SafeCallback.Raise(
            ParameterWritten,
            (Provider: this, Id: id, Value: value),
            static (h, s) => h(s.Provider, s.Id, s.Value),
            static (s, ex) => s.Provider.LogCallbackThrew(nameof(ParameterWritten), ex));

    /// <summary>How many listeners are attached. Diagnostics only.</summary>
    private int ListenerCount
    {
        get
        {
            int n = 0;
            for (spa_list* node = _listeners->list.next; node != &_listeners->list; node = node->next) n++;
            return n;
        }
    }

    private void EmitResult(int seq, int res, uint type, void* result)
    {
        for (spa_list* node = _listeners->list.next;
             node != &_listeners->list;
             node = node->next)
        {
            var hook = (spa_hook*)node;
            var events = (spa_device_events*)hook->cb.funcs;
            if (events is null || events->result is null) continue;

            events->result(hook->cb.data, seq, res, type, result);
        }
    }

    private static PipeWireDeviceProvider? FromData(void* data)
    {
        if (data is null) return null;

        try
        {
            return GCHandle.FromIntPtr((nint)data).Target as PipeWireDeviceProvider;
        }
        catch (Exception)
        {
            // A freed handle throws out of the lookup, and this is a native frame.
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Under the loop lock when it can be taken: the proxy destroy and the native frees below
        // must serialize against callbacks traversing the same tables, and the flags the toggle
        // writes are read from this memory. A lock is only taken when the context is alive to
        // give one - disposing after the context falls back to the previous order, which stays
        // safe because a dead loop dispatches no callbacks. Recursive on purpose: disposing from
        // a device callback is the same thread.
        if (_ctx.TryLock(out PipeWireContext.LoopLock scope))
        {
            using (scope)
            {
                // The proxy first: it is what refers to the interface, and freeing the interface
                // underneath a live proxy leaves the daemon dispatching through freed memory.
                _exported?.Dispose();
                _exported = null;

                ReleaseNative();
            }

            return;
        }

        _exported?.Dispose();
        _exported = null;

        ReleaseNative();
    }

    private void ReleaseNative()
    {
        if (_iface is not null)
        {
            if (_iface->type is not null) NativeMemory.Free(_iface->type);
            NativeMemory.Free(_iface);
            _iface = null;
        }

        if (_methods is not null) { NativeMemory.Free(_methods); _methods = null; }
        if (_listeners is not null) { NativeMemory.Free(_listeners); _listeners = null; }
        if (_paramInfo is not null) { NativeMemory.Free(_paramInfo); _paramInfo = null; }

        if (_self.IsAllocated) _self.Free();
    }

    [LoggerMessage(EventId = 34300, Level = LogLevel.Information,
        Message = "exported device {Name}; other clients can select its profiles")]
    private partial void LogExported(string name);

    [LoggerMessage(EventId = 34302, Level = LogLevel.Debug,
        Message = "enum_params id={Id} start={Start} num={Num}")]
    private partial void LogEnumParams(uint id, uint start, uint num);

    [LoggerMessage(EventId = 34304, Level = LogLevel.Debug,
        Message = "enum_params sent {Sent} results to {Listeners} listeners")]
    private partial void LogEnumSent(uint sent, int listeners);

    [LoggerMessage(EventId = 34303, Level = LogLevel.Debug, Message = "a listener attached")]
    private partial void LogListenerAttached();

    [LoggerMessage(EventId = 34301, Level = LogLevel.Error,
        Message = "a device provider callback ({Callback}) threw")]
    private partial void LogCallbackThrew(string callback, Exception exception);
}
