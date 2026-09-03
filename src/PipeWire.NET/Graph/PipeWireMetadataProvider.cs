using System.Collections.Concurrent;
using System.Linq;
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
/// The counterpart to <see cref="PipeWireMetadataStore"/>, which consumes somebody else's store.
/// The session's <c>default</c> store is shared by everything on the machine, so an application
/// with state of its own wants a store of its own rather than a key prefix in the shared one.
/// </para>
/// <para>
/// The implementation lives here, in this client's context. That is the only safe way to own a
/// store: asking the daemon to create one through the <c>metadata</c> factory produces an object the
/// daemon then expects this client to serve, and a client that does not leaves the daemon waiting
/// on it, unresponsive to everyone.
/// </para>
/// <para>
/// Exported by default, so other clients can bind it. The export type for Metadata comes from
/// <c>libpipewire-module-metadata</c>, which <c>client.conf</c> loads into a client context unless
/// <c>module.metadata</c> is turned off.
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
public sealed unsafe partial class PipeWireMetadataProvider : IDisposable, IAsyncDisposable
{
    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(uint Subject, string Key), PipeWireMetadataEntry> _entries = new();

    private PipeWireImplMetadataHandle? _handle;

    // The proxy pw_core_export hands back. Owned here: destroying it is what withdraws the store
    // from the daemon, and it has to go before the implementation it points at.
    private PipeWireProxyHandle? _exported;

    /// <summary>Whether to push the store to the daemon rather than keeping it local.</summary>
    private bool _export;
    private void* _events;
    private spa_hook* _hook;
    private GCHandle _self;
    private volatile bool _disposed;

    private PipeWireMetadataProvider(PipeWireContext ctx, string name, ILogger logger)
    {
        _ctx = ctx;
        Name = name;
        _logger = logger;
    }

    /// <summary>The store's <c>metadata.name</c>, which is how other clients find it.</summary>
    public string Name { get; }

    /// <summary>Raised when anyone changes an entry, including this client.</summary>
    public event Action<PipeWireMetadataProvider, PipeWireMetadataEntry>? EntryChanged;

    /// <summary>Every entry the store currently holds.</summary>
    /// <remarks>
    /// Coherent as it stands: ConcurrentDictionary.Values takes every one of the dictionary's locks
    /// and returns a collection built there, so this is a point-in-time snapshot rather than a walk
    /// over something being written. It reads like a live enumeration and is not one.
    /// </remarks>
    public IReadOnlyCollection<PipeWireMetadataEntry> Entries => [.. _entries.Values];

    /// <summary>Reads one entry, or <see langword="null"/> if it is not set.</summary>
    public string? Get(string key, uint subject = PipeWireMetadataStore.SubjectCore)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryGetValue((subject, key), out PipeWireMetadataEntry? entry) ? entry.Value : null;
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
    /// That wiring is also why creating a metadata object straight from the factory wedges the
    /// daemon. The factory hands back an object whose server side the creating client is expected
    /// to be, and a client that takes it without serving it leaves every request waiting on an
    /// answer that never comes. Exporting is the supported way to take that role.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="PipeWireException">The daemon refused to register it.</exception>
    public static PipeWireMetadataProvider Create(PipeWireContext ctx, string name, bool export = true)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var provider = new PipeWireMetadataProvider(ctx, name, ctx.LoggerFactory.CreateLogger("PipeWire.NET.MetadataProvider"))
        {
            _export = export,
        };
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

    private void Start()
    {
        ReadOnlySpan<byte> nameUtf8 = Encoding.UTF8.GetBytes(Name + '\0');

        using (_ctx.Lock())
        {
            // The name is carried twice on purpose. The first argument names the implementation;
            // the properties become the global's, and metadata.name there is how other clients find
            // it. Registered without it, the store exists but nothing can look it up.
            Span<byte> scratch = stackalloc byte[256];
            Span<spa_dict_item> items = stackalloc spa_dict_item[2];
            var props = new SpaDictBuilder(scratch, items);
            props.Add(PipeWireKeys.MetadataName, Name);
            spa_dict dict = props.Build();

            pw_impl_metadata* impl;
            fixed (byte* n = nameUtf8)
                impl = Native.pw_context_create_metadata(
                    _ctx.ContextHandle, (sbyte*)n, Native.pw_properties_new_dict(&dict), 0);

            if (impl is null)
                throw new PipeWireException("pw_context_create_metadata", -12);

            _handle = new PipeWireImplMetadataHandle(impl, _ctx.LoopOwner);

            _events = NativeMemory.AllocZeroed((nuint)sizeof(pw_impl_metadata_events));
            var table = (pw_impl_metadata_events*)_events;
            table->version = Native.PW_VERSION_IMPL_METADATA_EVENTS;
            table->property = &OnPropertyCallback;

            _hook = (spa_hook*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook));
            _self = GCHandle.Alloc(this, GCHandleType.Weak);

            _handle.OwnListener(_events, _hook, _self);

            Native.pw_impl_metadata_add_listener(impl, _hook, table, (void*)GCHandle.ToIntPtr(_self));

            // Register publishes a global in this client's own context. Exporting is what makes it
            // reach the daemon, and through it every other client.
            if (!_export)
            {
                int rc = Native.pw_impl_metadata_register(impl, Native.pw_properties_new_dict(&dict));
                if (rc < 0)
                    throw new PipeWireException("pw_impl_metadata_register", rc);
            }

            // Register publishes the global in this client's own context; exporting is what pushes
            // it to the daemon so every other client can bind it. The export type for Metadata is
            // registered by libpipewire-module-metadata, which client.conf loads into a client
            // context by default, so this ordinarily succeeds. A context whose configuration has
            // turned that module off cannot export, and says so rather than pretending: the store
            // still works locally, which is worth keeping, but nothing else will see it.
            Span<byte> exportScratch = stackalloc byte[256];
            Span<spa_dict_item> exportItems = stackalloc spa_dict_item[2];
            var exportProps = new SpaDictBuilder(exportScratch, exportItems);
            exportProps.Add(PipeWireKeys.MetadataName, Name);
            spa_dict exportDict = exportProps.Build();

            ReadOnlySpan<byte> typeUtf8 = Encoding.UTF8.GetBytes(Native.PW_TYPE_INTERFACE_METADATA + '\0');

            pw_proxy* exported = null;
            if (_export)
            {
                // The implementation interface, not the pw_impl_metadata that owns it. The metadata
                // module's export function takes its object as a pw_metadata and immediately casts
                // it to spa_interface to read the callback table out of it, so handing it the impl
                // pointer reads whatever happens to sit at that offset and takes the process down.
                pw_metadata* implementation = Native.pw_impl_metadata_get_implementation(impl);

                if (implementation is null)
                    throw new PipeWireException("pw_impl_metadata_get_implementation", -22);

                fixed (byte* t = typeUtf8)
                {
                    exported = Native.pw_core_export(
                        _ctx.CoreHandle, (sbyte*)t, &exportDict, implementation, 0);
                }
            }

            if (exported is not null)
            {
                _exported = new PipeWireProxyHandle(exported, _ctx.LoopOwner, _ctx.CoreOwner!);
                LogExported(Name);
            }
            else
            {
                LogNotExported(Name);
            }
        }

        LogRegistered(Name);
    }

    /// <summary>
    /// Sets, or with a null value removes, an entry.
    /// </summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="value">Its value, or <see langword="null"/> to remove it.</param>
    /// <param name="type">
    /// The value's type, such as <c>Spa:String:JSON</c>, or <see langword="null"/> for none.
    /// Null by default, matching <see cref="PipeWireMetadataStore.SetAsync"/> and
    /// <c>pw-metadata</c>. The type takes part in echo matching, so the two halves of this library
    /// have to agree on it.
    /// </param>
    /// <param name="subject">The object the entry is about; 0 is the session itself.</param>
    /// <exception cref="ObjectDisposedException">The store has been disposed.</exception>
    /// <exception cref="PipeWireException">The write was refused.</exception>
    public void Set(
        string key,
        string? value,
        string? type = null,
        uint subject = PipeWireMetadataStore.SubjectCore)
    {
        ArgumentNullException.ThrowIfNull(key);
        SetProperty(subject, key, type, value);
    }

    /// <summary>The write itself. A null key is the store's clear form and is not offered publicly.</summary>
    private void SetProperty(uint subject, string? key, string? type, string? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ReadOnlySpan<byte> keyUtf8 = key is null ? default : Encoding.UTF8.GetBytes(key + '\0');
        ReadOnlySpan<byte> typeUtf8 = type is null ? default : Encoding.UTF8.GetBytes(type + '\0');
        ReadOnlySpan<byte> valueUtf8 = value is null ? default : Encoding.UTF8.GetBytes(value + '\0');

        // No round-trip: the store is ours, so the write lands in this process and the callback
        // that updates the cache runs before this returns.
        using (_ctx.Lock())
        {
            if (_handle is null || _handle.IsInvalid)
                throw new ObjectDisposedException(nameof(PipeWireMetadataProvider));

            fixed (byte* k = keyUtf8)
            fixed (byte* t = typeUtf8)
            fixed (byte* v = valueUtf8)
            {
                int rc = Native.pw_impl_metadata_set_property(
                    _handle.Metadata, subject,
                    key is null ? null : (sbyte*)k,
                    type is null ? null : (sbyte*)t,
                    value is null ? null : (sbyte*)v);

                if (rc < 0) throw new PipeWireException("pw_impl_metadata_set_property", rc);
            }
        }
    }

    /// <summary>Removes every entry.</summary>
    /// <remarks>
    /// One call per subject the store holds, not one per entry. A null key is how the metadata
    /// implementation spells "clear this subject", and it empties the subject's storage and emits a
    /// single notification inside one hold of the loop lock. Set per entry instead, the lock is
    /// dropped between each, so a reader can observe the store half-cleared.
    /// <para>
    /// The subject list is snapshotted first because the entries are removed as the calls land. A
    /// subject that appears after the snapshot is not cleared: it was written after the caller
    /// asked, and clearing it would be this call reaching past the state it was given.
    /// </para>
    /// </remarks>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (uint subject in _entries.Keys.Select(static e => e.Subject).Distinct().ToArray())
            SetProperty(subject, key: null, type: null, value: null);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPropertyCallback(void* data, uint subject, sbyte* key, sbyte* type, sbyte* value)
    {
        if (data is null) return 0;

        PipeWireMetadataProvider self;
        try
        {
            // A freed handle throws out of the lookup, and this is a native frame.
            if (GCHandle.FromIntPtr((nint)data).Target is not PipeWireMetadataProvider found) return 0;
            self = found;
        }
        catch (Exception)
        {
            return 0;
        }

        try
        {
            string? k = DaemonText.String(key);
            string? t = DaemonText.String(type);
            string? v = DaemonText.String(value);

            self.Apply(subject, k, t, v);
        }
        catch (Exception ex)
        {
            // A native callback frame: an escaping exception aborts the process.
            self.LogDispatchFailed(ex);
        }

        // Non-zero would tell the daemon to reject the change.
        return 0;
    }

    private void Apply(uint subject, string? key, string? type, string? value)
    {
        // A null key means every entry for the subject is gone, which is how a bulk removal arrives.
        if (key is null)
        {
            // SPA_ID_INVALID means every subject, not a subject numbered 0xFFFFFFFF. Comparing it
            // to a stored subject matches nothing, so a store-wide clear would drop no entries at
            // all and the cache would keep reporting values the server no longer has.
            //
            // Defensive: whether the daemon uses this form for a clear is not something the test
            // box can show, because a served store cannot yet be bound by another client and
            // clearing the session's own store would take the machine's audio routing with it. If
            // the daemon never sends it the branch is dead, and if it does this is the difference
            // between an empty cache and a stale one.
            bool everySubject = subject == Native.SPA_ID_INVALID;

            foreach ((uint Subject, string Key) existing in _entries.Keys)
            {
                if (!everySubject && existing.Subject != subject) continue;
                if (_entries.TryRemove(existing, out PipeWireMetadataEntry? removed))
                    Raise(new PipeWireMetadataEntry(removed.Subject, removed.Key, removed.Type, null));
            }

            return;
        }

        var entry = new PipeWireMetadataEntry(subject, key, type, value);

        if (value is null) _entries.TryRemove((subject, key), out _);
        else _entries[(subject, key)] = entry;

        Raise(entry);
    }

    private void Raise(PipeWireMetadataEntry entry)
    {
        SafeCallback.Raise(EntryChanged, h => h(this, entry), ex => LogHandlerFaulted(Name, ex));
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
        if (_disposed) return;
        _disposed = true;

        // The listener's memory goes with the handle, freed after the implementation is destroyed.
        // Before the implementation: the exported proxy points at it, and withdrawing the store
        // from the daemon has to happen while the thing being withdrawn still exists.
        _exported?.Dispose();
        _exported = null;

        _handle?.Dispose();
        _handle = null;
        _hook = null;
        _events = null;
        _entries.Clear();
    }

    [LoggerMessage(EventId = 34203, Level = LogLevel.Information,
        Message = "exported metadata store {Name}; other clients can bind it")]
    private partial void LogExported(string name);

    [LoggerMessage(EventId = 34204, Level = LogLevel.Warning,
        Message = "metadata store {Name} could not be exported, so only this client can see it. "
                + "The context has no export type for Metadata, which libpipewire-module-metadata "
                + "registers and client.conf loads unless module.metadata is turned off.")]
    private partial void LogNotExported(string name);

    [LoggerMessage(EventId = 34200, Level = LogLevel.Information, Message = "serving metadata store {Name}")]
    private partial void LogRegistered(string name);

    [LoggerMessage(EventId = 34201, Level = LogLevel.Error, Message = "dispatching a metadata change failed")]
    private partial void LogDispatchFailed(Exception ex);

    [LoggerMessage(EventId = 34202, Level = LogLevel.Error,
                   Message = "an EntryChanged handler for store {Name} threw")]
    private partial void LogHandlerFaulted(string name, Exception ex);
}
