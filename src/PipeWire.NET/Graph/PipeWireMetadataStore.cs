using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Graph;

/// <summary>
/// A metadata store bound for reading and writing: the daemon's shared settings.
/// </summary>
/// <remarks>
/// <para>
/// Where the system-wide answers live. The <c>default</c> store holds
/// <c>default.audio.sink</c> and <c>default.audio.source</c> - which device new streams go to - and
/// <c>settings</c> holds the graph clock rate and quantum.
/// </para>
/// <para>
/// Entries are plain strings, usually with a JSON value, and need none of the parameter machinery
/// nodes and devices do. The store pushes every entry it has as soon as the listener attaches, so
/// <see cref="ReadyAsync"/> is what waits for that first burst before reading.
/// </para>
/// <para>
/// Writing needs permission the daemon grants to a session manager, not to an ordinary client.
/// A refused write is reported on the core's error stream, which is why
/// <see cref="SetAsync"/> round-trips rather than returning as soon as the call is made.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireMetadataStore : IDisposable, IAsyncDisposable
{
    /// <summary>The subject that means "the daemon", used for settings that are not about one object.</summary>
    public const uint SubjectCore = 0;

    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(uint Subject, string Key), PipeWireMetadataEntry> _entries = new();

    private readonly MetadataReconciler _reconciler = new(TimeSpan.FromSeconds(5));

    private BoundProxy? _bound;
    private volatile bool _disposed;

    private PipeWireMetadataStore(PipeWireContext ctx, uint id, ILogger logger)
    {
        _ctx = ctx;
        Id = id;
        _logger = logger;
    }

    /// <summary>The global id of the store this is bound to.</summary>
    public uint Id { get; }

    /// <summary>Raised when an entry is added, changed or removed.</summary>
    /// <remarks>
    /// A removal arrives as an entry whose value is <see langword="null"/>. Handlers run on the
    /// PipeWire loop thread; one that throws is reported and does not stop the others.
    /// </remarks>
    public event Action<PipeWireMetadataStore, PipeWireMetadataEntry>? EntryChanged;

    internal static unsafe PipeWireMetadataStore Bind(
        PipeWireContext ctx, pw_registry* registry, uint id, uint version, ILogger logger)
    {
        var store = new PipeWireMetadataStore(ctx, id, logger);
        store._bound = BoundProxy.Bind(
            ctx, registry, id, Native.PW_TYPE_INTERFACE_METADATA, version,
            sizeof(pw_metadata_events),
            events =>
            {
                var table = (pw_metadata_events*)events;
                table->version = Native.PW_VERSION_METADATA_EVENTS;
                table->property = &OnPropertyCallback;
            },
            static (proxy, hook, events, data) => Native.pw_metadata_add_listener(
                (pw_metadata*)proxy, (spa_hook*)hook, (pw_metadata_events*)events, (void*)data),
            store);

        return store;
    }

    /// <summary>
    /// Waits for the store to have sent everything it holds.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// The store starts pushing entries the moment the listener attaches, so reading before this
    /// completes reports whatever happened to have arrived. Events are ordered, so a core round-trip
    /// cannot answer before the whole burst has been dispatched.
    /// </remarks>
    public Task ReadyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CoreSync.RoundTripAsync(_ctx, cancellationToken);
    }

    /// <summary>Every entry the store currently holds.</summary>
    public IReadOnlyCollection<PipeWireMetadataEntry> Entries => [.. _entries.Values];

    /// <summary>The value of one entry, or <see langword="null"/> if the store has no such entry.</summary>
    /// <param name="key">The entry key, such as <c>default.audio.sink</c>.</param>
    /// <param name="subject">Which object the entry is about; <see cref="SubjectCore"/> for the daemon.</param>
    public string? Get(string key, uint subject = SubjectCore)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryGetValue((subject, key), out PipeWireMetadataEntry? entry) ? entry.Value : null;
    }

    /// <summary>
    /// Sets an entry, or removes it when <paramref name="value"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="key">The entry key.</param>
    /// <param name="value">The value, normally JSON. <see langword="null"/> removes the entry.</param>
    /// <param name="type">The value's type, or <see langword="null"/> to let the daemon decide.</param>
    /// <param name="subject">Which object the entry is about.</param>
    /// <param name="cancellationToken">Abandons the wait for the daemon to catch up.</param>
    /// <remarks>
    /// <para>
    /// Returns once the daemon has processed the write, and <see cref="Get"/> reflects it straight
    /// away. The store's own report of the change can arrive after the sync that proves the write
    /// landed, so the value is applied locally rather than waited for.
    /// </para>
    /// <para>
    /// While writes to a key are outstanding, a report carrying one of this client's own superseded
    /// values is ignored - that is its older write catching up. A report carrying anything else is
    /// another client's change and is applied normally, so the store never goes deaf to the rest of
    /// the session.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The daemon refused the write.</exception>
    public async Task SetAsync(
        string key,
        string? value,
        string? type = null,
        uint subject = SubjectCore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // Applied locally rather than waited for. The store does echo every change back, but that
        // event can be dispatched after the sync that proves the write was processed - so a read
        // straight after this call would otherwise see the old value, or none at all. Waiting for
        // the echo instead is not an option either: writing the value a key already holds is applied
        // without being echoed at all, which would hang.
        //
        // The marker goes in BEFORE the write. Set afterwards it is set too late to be matched by
        // its own echo, and then sits there permanently - silently discarding every later change
        // another client makes to the same key.
        MetadataReconciler.PendingWrite pending = _reconciler.NoteWrite(subject, key, type, value);

        try
        {
            // Permission is refused out of band, on the core's error stream, so the call returning
            // without a negative code proves nothing on its own - and the write has to go out with
            // the listener already attached, or a refusal answered in between is never seen.
            Task roundTrip = CoreSync.RoundTripAsync(
                _ctx, () => Write(subject, key, type, value), cancellationToken);

            Apply(new PipeWireMetadataEntry(subject, key, type, value));
            await roundTrip.ConfigureAwait(false);
            _reconciler.Settle(subject, key);
        }
        catch
        {
            // A write that never landed must not stay outstanding, or its value keeps suppressing
            // an echo that will never come.
            // This write's own entry, by identity. Removing every entry with the same value would
            // discard the bookkeeping of an identical write that is still in flight.
            _reconciler.Forget(subject, key, pending);
            throw;
        }
    }

    private void Apply(PipeWireMetadataEntry entry)
    {
        if (entry.Value is null)
            _entries.TryRemove((entry.Subject, entry.Key), out _);
        else
            _entries[(entry.Subject, entry.Key)] = entry;
    }

    /// <summary>
    /// The default audio sink new playback streams are routed to, or <see langword="null"/> if unset.
    /// </summary>
    /// <remarks>
    /// Only meaningful on the <c>default</c> store. The value is JSON of the form
    /// <c>{ "name": "alsa_output..." }</c>, so <see cref="PipeWireMetadataEntry.NameValue"/> is what
    /// pulls the node name out of it.
    /// </remarks>
    public PipeWireMetadataEntry? DefaultAudioSink => Find("default.audio.sink");

    /// <summary>The default audio source new capture streams are routed from.</summary>
    /// <inheritdoc cref="DefaultAudioSink" path="/remarks"/>
    public PipeWireMetadataEntry? DefaultAudioSource => Find("default.audio.source");

    /// <summary>Sets the default audio sink by node name.</summary>
    /// <param name="nodeName">The <c>node.name</c> of the sink, not its id.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// By name rather than by id deliberately, and that is the daemon's choice: ids are reused as
    /// objects come and go, so a default stored by id would drift onto a different device.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="nodeName"/> is null or empty.</exception>
    public Task SetDefaultAudioSinkAsync(string nodeName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeName);
        return SetAsync("default.audio.sink", NameJson(nodeName), "Spa:String:JSON",
            SubjectCore, cancellationToken);
    }

    /// <summary>Sets the default audio source by node name.</summary>
    /// <param name="nodeName">The <c>node.name</c> of the source, not its id.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <inheritdoc cref="SetDefaultAudioSinkAsync" path="/remarks"/>
    /// <exception cref="ArgumentException"><paramref name="nodeName"/> is null or empty.</exception>
    public Task SetDefaultAudioSourceAsync(string nodeName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeName);
        return SetAsync("default.audio.source", NameJson(nodeName), "Spa:String:JSON",
            SubjectCore, cancellationToken);
    }

    /// <summary>Removes every entry in the store.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <exception cref="InvalidOperationException">The daemon refused.</exception>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await CoreSync.RoundTripAsync(_ctx, ClearNative, cancellationToken).ConfigureAwait(false);

        // The cache and the in-flight record are dropped with it. Left alone, a read straight after
        // a clear still reports the old entries, and echoes of writes issued before the clear are
        // still recognised as ours and put their values back into an emptied store.
        _entries.Clear();
        _reconciler.Clear();
    }

    private unsafe int ClearNative()
    {
        // Referenced for the duration of the call. Destroying a proxy clears its pointer before it
        // takes the loop lock, so the lock alone does not stop this becoming null mid-call.
        if (!_bound!.TryUse(out BoundProxy.Use proxy))
            throw new ObjectDisposedException(GetType().Name);

        using (proxy)
        using (_ctx.Lock())
            return Native.pw_metadata_clear((pw_metadata*)proxy.Object);
    }

    private PipeWireMetadataEntry? Find(string key) =>
        _entries.TryGetValue((SubjectCore, key), out PipeWireMetadataEntry? entry) ? entry : null;

    // Escaped by the JSON writer rather than by hand: a node name may contain control characters,
    // which JSON requires escaped and a backslash/quote pass leaves raw, producing invalid JSON the
    // session manager then refuses.
    private static string NameJson(string nodeName) =>
        $$"""{ "name": "{{System.Text.Json.JsonEncodedText.Encode(nodeName)}}" }""";

    private unsafe int Write(uint subject, string key, string? type, string? value)
    {
        ReadOnlySpan<byte> keyUtf8 = Encoding.UTF8.GetBytes(key + '\0');
        ReadOnlySpan<byte> typeUtf8 = type is null ? default : Encoding.UTF8.GetBytes(type + '\0');
        ReadOnlySpan<byte> valueUtf8 = value is null ? default : Encoding.UTF8.GetBytes(value + '\0');

        // Referenced for the duration of the call. Destroying a proxy clears its pointer before it
        // takes the loop lock, so the lock alone does not stop this becoming null mid-call.
        if (!_bound!.TryUse(out BoundProxy.Use proxy))
            throw new ObjectDisposedException(GetType().Name);

        using (proxy)
        using (_ctx.Lock())
        {
            fixed (byte* k = keyUtf8)
            fixed (byte* t = typeUtf8)
            fixed (byte* v = valueUtf8)
            {
                return Native.pw_metadata_set_property(
                    (pw_metadata*)proxy.Object, subject, (sbyte*)k,
                    type is null ? null : (sbyte*)t,
                    value is null ? null : (sbyte*)v);
            }
        }
    }

    private void OnProperty(uint subject, string? key, string? type, string? value)
    {
        // A null key means "every entry for this subject is gone", which is how a store reports a
        // bulk removal. Anything else is one entry set or, with a null value, removed.
        if (key is null)
        {
            // Reported one entry at a time. A subject-wide clear changes the store exactly as an
            // individual removal does, and a consumer that only listens would otherwise never learn
            // that the entries went.
            foreach ((uint Subject, string Key) existing in _entries.Keys)
            {
                if (existing.Subject != subject) continue;
                if (_entries.TryRemove(existing, out PipeWireMetadataEntry? removed))
                    Raise(new PipeWireMetadataEntry(removed.Subject, removed.Key, removed.Type, null));
            }

            return;
        }

        var entry = new PipeWireMetadataEntry(subject, key, type, value);

        if (!_reconciler.ShouldApply(subject, key, type, value)) return;

        Apply(entry);
        Raise(entry);
    }

    /// <summary>Reports one change to subscribers, isolating a handler that throws.</summary>
    private void Raise(PipeWireMetadataEntry entry)
    {
        Action<PipeWireMetadataStore, PipeWireMetadataEntry>? handlers = EntryChanged;
        if (handlers is null) return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PipeWireMetadataStore, PipeWireMetadataEntry>)handler)(this, entry); }
            catch (Exception ex) { LogHandlerFaulted(Id, entry.Key, ex); }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnPropertyCallback(
        void* data, uint subject, sbyte* key, sbyte* type, sbyte* value)
    {
        // An exception escaping a reverse P/Invoke aborts the process, so nothing here may throw.
        try
        {
            var self = (PipeWireMetadataStore?)GCHandle.FromIntPtr((nint)data).Target;
            if (self is null || self._disposed) return 0;

            self.OnProperty(subject, Utf8(key), Utf8(type), Utf8(value));
        }
        catch
        {
            // Deliberately not logged: the instance the logger belongs to is what failed to resolve.
        }

        return 0;

        static string? Utf8(sbyte* p) =>
            p is null ? null : Encoding.UTF8.GetString(
                MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)p));
    }

    /// <inheritdoc/>
    /// <summary>Tears down synchronously. Disposal here does no I/O.</summary>
    /// <remarks>
    /// Offered alongside the async form because nothing about this disposal is asynchronous -
    /// the async method completes synchronously - so a consumer should not be forced to write
    /// "await using" for it.
    /// </remarks>
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

        _bound?.Dispose();
        _bound = null;
        _entries.Clear();
        _reconciler.Clear();

        GC.SuppressFinalize(this);
    }

    [LoggerMessage(EventId = 33200, Level = LogLevel.Error,
                   Message = "an EntryChanged handler for store {StoreId} key {Key} threw")]
    private partial void LogHandlerFaulted(uint storeId, string key, Exception exception);
}
