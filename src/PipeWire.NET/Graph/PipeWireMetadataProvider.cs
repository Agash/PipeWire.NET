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
/// Incomplete: the store is not yet exported, so other clients cannot bind it. Exporting needs an
/// export type for Metadata registered in this context, which <c>libpipewire-module-metadata</c>
/// provides and a plain client does not load.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class PipeWireMetadataProvider : IDisposable, IAsyncDisposable
{
    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(uint Subject, string Key), PipeWireMetadataEntry> _entries = new();

    private PipeWireImplMetadataHandle? _handle;
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
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="PipeWireException">The daemon refused to register it.</exception>
    public static PipeWireMetadataProvider Create(PipeWireContext ctx, string name)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var provider = new PipeWireMetadataProvider(ctx, name, ctx.LoggerFactory.CreateLogger("PipeWire.NET.MetadataProvider"));
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
            int rc = Native.pw_impl_metadata_register(impl, Native.pw_properties_new_dict(&dict));
            if (rc < 0)
                throw new PipeWireException("pw_impl_metadata_register", rc);

            // Not yet exported. pw_core_export needs an export type for Metadata registered in
            // this context, which libpipewire-module-metadata provides and a plain client does not
            // load. Until that is wired the store is served correctly but visible only here.
        }

        LogRegistered(Name);
    }

    /// <summary>
    /// Sets, or with a null value removes, an entry.
    /// </summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="value">Its value, or <see langword="null"/> to remove it.</param>
    /// <param name="type">The value's type, such as <c>Spa:String</c>.</param>
    /// <param name="subject">The object the entry is about; 0 is the session itself.</param>
    /// <exception cref="ObjectDisposedException">The store has been disposed.</exception>
    /// <exception cref="PipeWireException">The write was refused.</exception>
    public void Set(
        string key,
        string? value,
        string? type = "Spa:String",
        uint subject = PipeWireMetadataStore.SubjectCore)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ReadOnlySpan<byte> keyUtf8 = Encoding.UTF8.GetBytes(key + '\0');
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
                    _handle.Metadata, subject, (sbyte*)k,
                    type is null ? null : (sbyte*)t,
                    value is null ? null : (sbyte*)v);

                if (rc < 0) throw new PipeWireException("pw_impl_metadata_set_property", rc);
            }
        }
    }

    /// <summary>Removes every entry.</summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach ((uint Subject, string Key) entry in _entries.Keys)
            Set(entry.Key, null, subject: entry.Subject);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPropertyCallback(void* data, uint subject, sbyte* key, sbyte* type, sbyte* value)
    {
        if (data is null) return 0;
        if (GCHandle.FromIntPtr((nint)data).Target is not PipeWireMetadataProvider self) return 0;

        try
        {
            string? k = key is null ? null : Marshal.PtrToStringUTF8((nint)key);
            string? t = type is null ? null : Marshal.PtrToStringUTF8((nint)type);
            string? v = value is null ? null : Marshal.PtrToStringUTF8((nint)value);

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
            foreach ((uint Subject, string Key) existing in _entries.Keys)
            {
                if (existing.Subject != subject) continue;
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
        Action<PipeWireMetadataProvider, PipeWireMetadataEntry>? handlers = EntryChanged;
        if (handlers is null) return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PipeWireMetadataProvider, PipeWireMetadataEntry>)handler)(this, entry); }
            catch (Exception ex) { LogHandlerFaulted(Name, ex); }
        }
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
        _handle?.Dispose();
        _handle = null;
        _hook = null;
        _events = null;
        _entries.Clear();
    }

    [LoggerMessage(EventId = 34200, Level = LogLevel.Information, Message = "serving metadata store {Name}")]
    private partial void LogRegistered(string name);

    [LoggerMessage(EventId = 34201, Level = LogLevel.Error, Message = "dispatching a metadata change failed")]
    private partial void LogDispatchFailed(Exception ex);

    [LoggerMessage(EventId = 34202, Level = LogLevel.Error,
                   Message = "an EntryChanged handler for store {Name} threw")]
    private partial void LogHandlerFaulted(string name, Exception ex);
}
