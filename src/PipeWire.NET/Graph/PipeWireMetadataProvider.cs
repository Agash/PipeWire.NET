using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// A metadata store this client owns and other clients can read and write.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="PipeWireMetadataProxy"/>, which consumes somebody else's store.
/// The session's <c>default</c> store is shared by everything on the machine, so an application
/// with state of its own wants a store of its own rather than a key prefix in the shared one.
/// </para>
/// <para>
/// The store is implemented here: this class is the <c>pw_metadata</c> the daemon talks to, so every
/// change, from this client or any other, and every client binding the store comes through it. That is
/// also the only safe way to own a store: asking the daemon to create one through the <c>metadata</c>
/// factory produces an object the daemon then expects this client to serve, and a client that does
/// not leaves the daemon waiting on it, unresponsive to everyone.
/// </para>
/// <para>
/// Exported by default, so other clients can bind it. The export type for Metadata comes from
/// <c>libpipewire-module-metadata</c>, which <c>client.conf</c> loads into a client context unless
/// <c>module.metadata</c> is turned off.
/// </para>
/// <para>
/// <b>Changes reach every client, binding ones included.</b> The daemon's metadata module, from a
/// client binding the store until this store has answered the ping that follows the bind, passes
/// changes only to the client binding, and every client already bound misses them for good. The store
/// works around it: a change is resent, as the store then holds it, unless a round trip proved the
/// daemon had it before any bind, and changes made while a bind is in progress are held until it is
/// over. The cost is one core round trip for each burst of changes.
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
public sealed partial class PipeWireMetadataProvider : IDisposable, IAsyncDisposable
{
    // Changes encoded on the stack up to this size; larger ones rent.
    private const int StackEncoding = 1024;

    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    private readonly bool _export;
    private readonly ConcurrentDictionary<
        (uint Subject, string Key),
        PipeWireMetadataEntry
    > _entries = new();

    // The implementation, in unmanaged memory because the daemon's side keeps raw pointers into it:
    // the interface the store is known by, its method table and its listeners.
    private unsafe spa_interface* _interface;
    private unsafe pw_metadata_methods* _methods;
    private unsafe spa_hook_list* _hooks;
    private GCHandle _self;

    // An in-process store's global. Unused when exporting.
    private PipeWireImplMetadataHandle? _impl;

    // The proxy pw_core_export hands back. Owned here: destroying it is what withdraws the store
    // from the daemon, and it has to go before the implementation it points at.
    private PipeWireProxyHandle? _exported;

    // Whether every listener added from here on is a client binding the store. The ones added while
    // the store is published are its own: the export's, or the in-process global's.
    private bool _published;

    // The bind-window workaround, all under the loop lock. A change is unconfirmed from when it is
    // sent until a round trip started after it comes back with no bind seen in between; while any
    // bind is in progress changes are held. Each entry is the change's place in sending order.
    private readonly Dictionary<(uint Subject, string? Key), long> _unconfirmed = [];
    private long _sent;
    private long _bindEpoch;
    private int _bindsInProgress;
    private bool _confirming;
    private int _binds;
    private volatile bool _disposed;

    private PipeWireMetadataProvider(PipeWireContext ctx, string name, bool export, ILogger logger)
    {
        _ctx = ctx;
        Name = name;
        _export = export;
        _logger = logger;
    }

    /// <summary>The store's <c>metadata.name</c>, which is how other clients find it.</summary>
    public string Name { get; }

    /// <summary>Raised when anyone changes an entry, including this client.</summary>
    /// <remarks>Handlers run on the PipeWire loop thread; one that throws is reported and does not stop the others.</remarks>
    public event Action<PipeWireMetadataProvider, PipeWireMetadataEntry>? EntryChanged;

    /// <summary>
    /// Raised when a client binds the store, once it has been sent everything the store holds.
    /// </summary>
    /// <remarks>Handlers run on the PipeWire loop thread; one that throws is reported and does not stop the others.</remarks>
    public event Action<PipeWireMetadataProvider>? ClientBound;

    /// <summary>How many times a client has bound the store.</summary>
    public int BindCount => Volatile.Read(ref _binds);

    /// <summary>Every entry the store currently holds.</summary>
    /// <remarks>
    /// Coherent as it stands: ConcurrentDictionary.Values takes every one of the dictionary's locks
    /// and returns a collection built there, so this is a point-in-time snapshot rather than a walk
    /// over something being written. It reads like a live enumeration and is not one.
    /// </remarks>
    public IReadOnlyCollection<PipeWireMetadataEntry> Entries => [.. _entries.Values];

    /// <summary>Reads one entry, or <see langword="null"/> if it is not set.</summary>
    public string? Get(string key, uint subject = PipeWireMetadataProxy.SubjectCore)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryGetValue((subject, key), out PipeWireMetadataEntry? entry)
            ? entry.Value
            : null;
    }

    /// <summary>
    /// Creates a store and publishes it.
    /// </summary>
    /// <param name="ctx">The context to host it in.</param>
    /// <param name="name">Its <c>metadata.name</c>.</param>
    /// <param name="export">
    /// Publish the store to the daemon so every other client can bind it. On by default, which is
    /// what serving a store normally means. Pass <see langword="false"/> for one that stays inside
    /// this process.
    /// </param>
    /// <remarks>
    /// <para>
    /// Registering and exporting are alternatives, not steps, and this is the distinction the two
    /// paths keep apart. Registering publishes a global in this client's own context, so the store
    /// is real but only this process can see it. Exporting creates the object through the daemon's
    /// metadata factory and wires this implementation to it in both directions: requests from other
    /// clients arrive here, and changes made here are sent out to them.
    /// </para>
    /// <para>
    /// Serve a store for as long as you need it rather than making and unmaking one repeatedly.
    /// An exported store is cheap to hold and expensive to churn: withdrawing and recreating one
    /// while the session manager is rebuilding a device - a card profile change, from this or any
    /// other client - can leave the daemon holding a client it never reads from again, which
    /// stops it answering every client on the machine until the session restarts. Holding one
    /// store across the same work does not do this. The session managers that ship with PipeWire
    /// export their stores once and keep them.
    /// </para>
    /// <para>
    /// That wiring is also why creating a metadata object straight from the factory wedges the
    /// daemon. The factory hands back an object whose server side the creating client is expected
    /// to be, and a client that takes it without serving it leaves every request waiting on an
    /// answer that never comes. Exporting is the supported way to take that role.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="PipeWireException">The daemon refused to register it.</exception>
    public static PipeWireMetadataProvider Create(
        PipeWireContext ctx,
        string name,
        bool export = true
    )
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var provider = new PipeWireMetadataProvider(
            ctx,
            name,
            export,
            ctx.LoggerFactory.CreateLogger("PipeWire.NET.MetadataProvider")
        );
        try
        {
            provider.Start();
            return provider;
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    private unsafe void Start()
    {
        // Everything the daemon's side may call into has to be reachable from a raw pointer and
        // outlive this call, so the native structures live in unmanaged memory.
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        _methods = (pw_metadata_methods*)
            NativeMemory.AllocZeroed((nuint)sizeof(pw_metadata_methods));
        _methods->version = NativeConstants.PW_VERSION_METADATA_METHODS;
        _methods->add_listener = &OnAddListener;
        _methods->set_property = &OnSetProperty;
        _methods->clear = &OnClear;
        _hooks = (spa_hook_list*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook_list));
        SpaHookList.Init(_hooks);

        // The pointer outlives the fixed block deliberately: a u8 literal is a span over static data
        // in the assembly image, which never moves.
        _interface = (spa_interface*)NativeMemory.AllocZeroed((nuint)sizeof(spa_interface));
        fixed (byte* type = NativeConstants.PW_TYPE_INTERFACE_Metadata)
            _interface->type = (sbyte*)type;
        _interface->version = NativeConstants.PW_VERSION_METADATA;
        _interface->cb.funcs = _methods;
        _interface->cb.data = (void*)GCHandle.ToIntPtr(_self);

        // The name is carried twice on purpose. The first argument names the implementation; the
        // properties become the global's, and metadata.name there is how other clients find it.
        Span<byte> scratch = stackalloc byte[256];
        Span<spa_dict_item> items = stackalloc spa_dict_item[2];
        var props = new SpaDictBuilder(scratch, items);
        props.Add(NativeConstants.PW_KEY_METADATA_NAME, Name);
        spa_dict dict = props.Build();

        using (_ctx.Lock())
        {
            if (_export)
            {
                // The export marshals the daemon's requests to this interface and adds the one
                // listener its changes go out through.
                pw_proxy* exported;
                fixed (byte* t = NativeConstants.PW_TYPE_INTERFACE_Metadata)
                    exported = Native.pw_core_export(
                        _ctx.CoreHandle,
                        (sbyte*)t,
                        &dict,
                        _interface,
                        0
                    );

                // A context whose configuration turned libpipewire-module-metadata off has no export
                // type for Metadata, and says so rather than pretending: the store still works locally.
                if (exported is null)
                {
                    LogNotExported(Name);
                }
                else
                {
                    _exported = new PipeWireProxyHandle(exported, _ctx.LoopOwner, _ctx.CoreOwner!);
                    _ctx.ServedStores.TryAdd(this, 0);
                    LogExported(Name);
                }
            }
            else
            {
                // A global in this client's own context, served by this implementation in place of
                // libpipewire's own.
                ReadOnlySpan<byte> nameUtf8 = Encoding.UTF8.GetBytes(Name + '\0');
                pw_impl_metadata* impl;
                fixed (byte* n = nameUtf8)
                    impl = Native.pw_context_create_metadata(
                        _ctx.ContextHandle,
                        (sbyte*)n,
                        Native.pw_properties_new_dict(&dict),
                        0
                    );

                if (impl is null)
                    throw new PipeWireInteropException(
                        "pw_context_create_metadata",
                        -NativeLibc.ENOMEM
                    );

                _impl = new PipeWireImplMetadataHandle(impl, _ctx.LoopOwner);
                int rc = Native.pw_impl_metadata_set_implementation(impl, (pw_metadata*)_interface);
                if (rc < 0)
                    throw new PipeWireInteropException("pw_impl_metadata_set_implementation", rc);

                rc = Native.pw_impl_metadata_register(impl, Native.pw_properties_new_dict(&dict));
                if (rc < 0)
                    throw new PipeWireInteropException("pw_impl_metadata_register", rc);
            }

            _published = true;
        }

        LogRegistered(Name);
    }

    /// <summary>The id of the global the daemon made for this store, once it has; otherwise null.</summary>
    internal unsafe uint? ServedGlobalId
    {
        get
        {
            PipeWireProxyHandle? exported = _exported;
            if (exported is null || exported.IsInvalid)
                return null;
            uint id = Native.pw_proxy_get_bound_id(exported.Proxy);
            return id == NativeConstants.SPA_ID_INVALID ? null : id;
        }
    }

    /// <summary>
    /// Waits for the daemon to have processed the export, so other clients can see the store.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// <see cref="Create"/> returns as soon as the export has been sent, not once the daemon has
    /// dealt with it, so a store is not yet visible to anybody else when it comes back. Requests
    /// are ordered, so a core round-trip cannot answer before the export ahead of it has been
    /// processed. An unexported store has nothing to wait for and completes at once.
    /// <para>
    /// The session managers that ship with PipeWire wait for the equivalent - their exported
    /// objects are not treated as usable until the daemon has bound them - and a caller that
    /// serves a store for others to read should do the same before announcing it.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The store has been disposed.</exception>
    public Task ReadyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _exported is not null
            ? CoreSync.RoundTripAsync(_ctx, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>
    /// Sets, or with a null value removes, an entry.
    /// </summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="value">Its value, or <see langword="null"/> to remove it.</param>
    /// <param name="type">
    /// The value's type, such as <c>Spa:String:JSON</c>, or <see langword="null"/> for none.
    /// Null by default, matching <see cref="PipeWireMetadataProxy.SetAsync"/> and
    /// <c>pw-metadata</c>. The type takes part in echo matching, so the two halves of this library
    /// have to agree on it.
    /// </param>
    /// <param name="subject">The object the entry is about; 0 is the session itself.</param>
    /// <exception cref="ObjectDisposedException">The store has been disposed.</exception>
    public void Set(
        string key,
        string? value,
        string? type = null,
        uint subject = PipeWireMetadataProxy.SubjectCore
    )
    {
        ArgumentNullException.ThrowIfNull(key);

        // Native strings end at the first NUL while the cache keys on the whole string, so an
        // embedded NUL would file one entry here and another in the daemon and never reconcile.
        ThrowIfContainsNul(key, nameof(key));
        ThrowIfContainsNul(value, nameof(value));
        ThrowIfContainsNul(type, nameof(type));
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The store is this object, so the change is made here and announced before this returns.
        using (_ctx.Lock())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Change(subject, key, type, value);
        }
    }

    /// <summary>Removes every entry.</summary>
    /// <remarks>
    /// One announcement per subject the store holds, not one per entry, inside one hold of the loop
    /// lock, so no reader observes the store half-cleared.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The store has been disposed.</exception>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using (_ctx.Lock())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ClearAll();
        }
    }

    private static void ThrowIfContainsNul(string? text, string paramName)
    {
        if (text is not null && text.Contains('\0'))
            throw new ArgumentException("metadata fields cannot contain NUL bytes.", paramName);
    }

    // - The store, under the loop lock -
    //
    // The semantics are libpipewire's own implementation's (impl-metadata.c): a change is announced
    // only when it changes something, a removal carries no type, a key-less change clears a subject
    // and is announced once for it, and a clear does that for every subject.

    private void Change(uint subject, string? key, string? type, string? value)
    {
        if (key is null)
        {
            ClearSubject(subject);
            return;
        }

        bool exists = _entries.TryGetValue((subject, key), out PipeWireMetadataEntry? current);
        PipeWireMetadataEntry entry;
        if (value is null)
        {
            if (!exists)
                return;
            _entries.TryRemove((subject, key), out _);
            entry = new PipeWireMetadataEntry(subject, key, null, null);
        }
        else
        {
            type ??= current?.Type;
            if (exists && current!.Type == type && current.Value == value)
                return;
            entry = new PipeWireMetadataEntry(subject, key, type, value);
            _entries[(subject, key)] = entry;
        }

        Raise(entry);
        Announce(subject, key, entry.Type, entry.Value);
    }

    private void ClearSubject(uint subject)
    {
        bool removed = false;
        foreach ((uint Subject, string Key) existing in _entries.Keys)
        {
            if (
                existing.Subject == subject
                && _entries.TryRemove(existing, out PipeWireMetadataEntry? gone)
            )
            {
                removed = true;
                Raise(new PipeWireMetadataEntry(gone.Subject, gone.Key, gone.Type, null));
            }
        }

        if (removed)
            Announce(subject, null, null, null);
    }

    private void ClearAll()
    {
        foreach (uint subject in _entries.Keys.Select(static e => e.Subject).Distinct().ToArray())
            ClearSubject(subject);
    }

    // A change on its way to the listeners: through the daemon to every bound client when exported,
    // held while a bind is in progress, and kept as unconfirmed until a round trip proves the daemon
    // had it before any bind.
    private unsafe void Announce(uint subject, string? key, string? type, string? value)
    {
        if (_exported is null)
        {
            Emit(_hooks, subject, key, type, value);
            return;
        }

        _unconfirmed[(subject, key)] = ++_sent;
        if (_bindsInProgress > 0)
            return;

        Emit(_hooks, subject, key, type, value);
        Confirm();
    }

    // One round trip covers everything sent before it: if it comes back with no bind seen since it
    // started, the daemon handled all of it before any bind opened its window, and it reached every
    // client bound at the time.
    private void Confirm()
    {
        if (_confirming || _disposed)
            return;

        _confirming = true;
        long upTo = _sent;
        long epoch = _bindEpoch;
        _ = ConfirmAsync(upTo, epoch);
    }

    private async Task ConfirmAsync(long upTo, long epoch)
    {
        try
        {
            await CoreSync.RoundTripAsync(_ctx, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PipeWireException or ObjectDisposedException)
        {
            // The connection or the context went away: nothing is waiting on confirmation any more.
            LogRoundTripFailed(Name, ex);
            return;
        }

        Confirmed(upTo, epoch);
    }

    private void Confirmed(long upTo, long epoch)
    {
        if (!_ctx.TryLock(out PipeWireContext.LoopLock scope))
            return;

        using (scope)
        {
            _confirming = false;
            if (_disposed)
                return;

            // A bind since the round trip started may have swallowed any of it; that bind resends.
            if (_bindEpoch == epoch)
            {
                foreach (KeyValuePair<(uint, string?), long> sent in _unconfirmed.ToArray())
                {
                    if (sent.Value <= upTo)
                        _unconfirmed.Remove(sent.Key);
                }
            }

            if (_bindsInProgress == 0 && _unconfirmed.Count > 0 && _sent > upTo)
                Confirm();
        }
    }

    // A client bound the store. The daemon pings this store straight after asking for the replay, and
    // answers a round trip started now only after that ping, so when it comes back the pong has gone
    // out ahead of anything sent from then on.
    private void BeginBind()
    {
        _bindEpoch++;
        _bindsInProgress++;
        _ = EndBindAsync();
    }

    private async Task EndBindAsync()
    {
        try
        {
            await CoreSync.RoundTripAsync(_ctx, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PipeWireException or ObjectDisposedException)
        {
            LogRoundTripFailed(Name, ex);
            return;
        }

        BindEnded();
    }

    private void BindEnded()
    {
        if (!_ctx.TryLock(out PipeWireContext.LoopLock scope))
            return;

        using (scope)
        {
            if (_disposed || --_bindsInProgress > 0)
                return;

            Resend();
        }
    }

    // Everything not proven delivered before a bind, and everything held during one, goes out again as
    // the store holds it now: a cleared subject as its clear and then whatever it holds since, an entry
    // as its value or its removal.
    private unsafe void Resend()
    {
        if (_unconfirmed.Count == 0)
            return;

        (uint Subject, string? Key)[] touched =
        [
            .. _unconfirmed.OrderBy(static p => p.Value).Select(static p => p.Key),
        ];
        foreach ((uint subject, string? key) in touched)
        {
            if (key is null)
            {
                Emit(_hooks, subject, null, null, null);
                foreach (PipeWireMetadataEntry entry in _entries.Values)
                {
                    if (entry.Subject == subject)
                        Emit(_hooks, subject, entry.Key, entry.Type, entry.Value);
                }
            }
            else if (_entries.TryGetValue((subject, key), out PipeWireMetadataEntry? entry))
            {
                Emit(_hooks, subject, key, entry.Type, entry.Value);
            }
            else
            {
                Emit(_hooks, subject, key, null, null);
            }

            _unconfirmed[(subject, key)] = ++_sent;
        }

        Confirm();
    }

    // pw_metadata_emit_property: to every listener on the list, safe against one removing itself. The
    // strings are encoded once, on the stack when they fit.
    private static unsafe void Emit(
        spa_hook_list* hooks,
        uint subject,
        string? key,
        string? type,
        string? value
    )
    {
        if (hooks is null)
            return;

        int keyBytes = Utf8Length(key);
        int typeBytes = Utf8Length(type);
        int valueBytes = Utf8Length(value);
        int total = keyBytes + typeBytes + valueBytes;
        byte[]? rented = null;
        Span<byte> buffer =
            total <= StackEncoding
                ? stackalloc byte[StackEncoding]
                : (rented = ArrayPool<byte>.Shared.Rent(total));
        try
        {
            Encode(key, buffer);
            Encode(type, buffer[keyBytes..]);
            Encode(value, buffer[(keyBytes + typeBytes)..]);
            fixed (byte* text = buffer)
            {
                sbyte* k = key is null ? null : (sbyte*)text;
                sbyte* t = type is null ? null : (sbyte*)(text + keyBytes);
                sbyte* v = value is null ? null : (sbyte*)(text + keyBytes + typeBytes);
                for (spa_list* l = hooks->list.next, next; l != &hooks->list; l = next)
                {
                    next = l->next;
                    var hook = (spa_hook*)l;
                    var events = (pw_metadata_events*)hook->cb.funcs;
                    if (events is not null && events->property is not null)
                        events->property(hook->cb.data, subject, k, t, v);
                }
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int Utf8Length(string? text) =>
        text is null ? 0 : Encoding.UTF8.GetByteCount(text) + 1;

    private static void Encode(string? text, Span<byte> into)
    {
        if (text is null)
            return;
        int written = Encoding.UTF8.GetBytes(text, into);
        into[written] = 0;
    }

    private void Raise(PipeWireMetadataEntry entry) =>
        SafeCallback.Raise(EntryChanged, h => h(this, entry), ex => LogHandlerFaulted(Name, ex));

    // - The method table -
    //
    // Static, because a function pointer into managed code cannot close over an instance; the
    // instance arrives as the object argument, the cb.data the interface was made with. Called on
    // the loop thread, with the loop lock held.

    private static unsafe PipeWireMetadataProvider? From(void* data)
    {
        if (data is null)
            return null;
        try
        {
            return GCHandle.FromIntPtr((nint)data).Target as PipeWireMetadataProvider;
        }
        catch (InvalidOperationException)
        {
            // Deliberately not logged: a freed handle means the provider, and its logger, are gone.
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnAddListener(
        void* obj,
        spa_hook* hook,
        pw_metadata_events* events,
        void* data
    )
    {
        PipeWireMetadataProvider? self = From(obj);
        if (self is null || self._disposed || hook is null || events is null)
            return -NativeLibc.EINVAL;

        try
        {
            // impl_add_listener: the new listener alone is sent everything the store holds, then the
            // others are joined back.
            spa_hook_list save;
            SpaHookList.Isolate(self._hooks, &save, hook, events, data);
            try
            {
                foreach (PipeWireMetadataEntry entry in self._entries.Values)
                    Emit(self._hooks, entry.Subject, entry.Key, entry.Type, entry.Value);
            }
            finally
            {
                SpaHookList.Join(self._hooks, &save);
            }

            // The listeners the store adds itself while it is published come first; any after are a
            // client binding it.
            if (self._published)
            {
                Interlocked.Increment(ref self._binds);
                if (self._exported is not null)
                    self.BeginBind();
                SafeCallback.Raise(
                    self.ClientBound,
                    h => h(self),
                    ex => self.LogHandlerFaulted(self.Name, ex)
                );
            }

            return 0;
        }
        catch (Exception ex)
        {
            // A native callback frame: an escaping exception aborts the process.
            self.LogDispatchFailed(ex);
            return -NativeLibc.EIO;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnSetProperty(
        void* obj,
        uint subject,
        sbyte* key,
        sbyte* type,
        sbyte* value
    )
    {
        PipeWireMetadataProvider? self = From(obj);
        if (self is null || self._disposed)
            return 0;

        try
        {
            self.Change(
                subject,
                DaemonText.String(key),
                DaemonText.String(type),
                DaemonText.String(value)
            );
        }
        catch (Exception ex)
        {
            self.LogDispatchFailed(ex);
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnClear(void* obj)
    {
        PipeWireMetadataProvider? self = From(obj);
        if (self is null || self._disposed)
            return 0;

        try
        {
            self.ClearAll();
        }
        catch (Exception ex)
        {
            self.LogDispatchFailed(ex);
        }

        return 0;
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeCore();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private void DisposeCore()
    {
        if (_disposed)
            return;
        _disposed = true;

        // The listeners first: withdrawing the export and destroying the in-process global each
        // unlink the hook they hold on this implementation, which has to exist while they do.
        _ctx.ServedStores.TryRemove(this, out _);
        _exported?.Dispose();
        _exported = null;
        _impl?.Dispose();
        _impl = null;

        // Under the loop lock, so no callback is running into the memory as it goes. A context
        // already gone has no loop left to call in from.
        if (_ctx.TryLock(out PipeWireContext.LoopLock scope))
        {
            using (scope)
                FreeNative();
        }
        else
        {
            FreeNative();
        }

        _entries.Clear();
    }

    private unsafe void FreeNative()
    {
        if (_interface is not null)
        {
            NativeMemory.Free(_interface);
            _interface = null;
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

    [LoggerMessage(
        EventId = 1440,
        Level = LogLevel.Information,
        Message = "exported metadata store {Name}; other clients can bind it"
    )]
    private partial void LogExported(string name);

    [LoggerMessage(
        EventId = 1441,
        Level = LogLevel.Warning,
        Message = "metadata store {Name} could not be exported, so only this client can see it. "
            + "The context has no export type for Metadata, which libpipewire-module-metadata "
            + "registers and client.conf loads unless module.metadata is turned off."
    )]
    private partial void LogNotExported(string name);

    [LoggerMessage(
        EventId = 1442,
        Level = LogLevel.Information,
        Message = "serving metadata store {Name}"
    )]
    private partial void LogRegistered(string name);

    [LoggerMessage(
        EventId = 1443,
        Level = LogLevel.Error,
        Message = "dispatching a metadata change failed"
    )]
    private partial void LogDispatchFailed(Exception ex);

    [LoggerMessage(
        EventId = 1444,
        Level = LogLevel.Error,
        Message = "a handler for metadata store {Name} threw"
    )]
    private partial void LogHandlerFaulted(string name, Exception ex);

    [LoggerMessage(
        EventId = 1445,
        Level = LogLevel.Debug,
        Message = "metadata store {Name} could not confirm its changes reached the daemon; the connection is gone"
    )]
    private partial void LogRoundTripFailed(string name, Exception ex);
}
