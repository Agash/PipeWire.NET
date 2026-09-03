using System.Collections.Concurrent;
using System.Globalization;
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

    // Bumped by every clear. A write issued before a clear must not apply after it: the local
    // apply is optimistic and can land out of order with one, which puts a cleared entry back.
    private long _epoch;

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
            ctx, registry, id, Native.PW_TYPE_INTERFACE_METADATA, version, Native.PW_VERSION_METADATA,
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
    /// <para>
    /// It orders this connection and nothing else. A store like <c>default</c> is served by the
    /// session manager, a separate process, so a write by another client travels through it and
    /// back; no barrier on either client waits for that hop. To observe someone else's write, wait
    /// for <see cref="EntryChanged"/>, not for this.
    /// </para>
    /// </remarks>
    public Task ReadyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CoreSync.RoundTripAsync(_ctx, cancellationToken);
    }

    /// <summary>Every entry the store currently holds.</summary>
    /// <remarks>
    /// Coherent as it stands: ConcurrentDictionary.Values takes every one of the dictionary's locks
    /// and returns a collection built there, so this is a point-in-time snapshot rather than a walk
    /// over something being written. It reads like a live enumeration and is not one.
    /// </remarks>
    public IReadOnlyCollection<PipeWireMetadataEntry> Entries => [.. _entries.Values];

    private static void ThrowIfContainsNul(string? text, string paramName)
    {
        if (text is not null && text.Contains('\0'))
            throw new ArgumentException("metadata fields cannot contain NUL bytes.", paramName);
    }

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
    /// <param name="cancellationToken">
    /// Abandons the wait. The request is already on its way, so cancelling does not recall
    /// it: the daemon can still apply the change after this throws.
    /// </param>
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
    /// <exception cref="ArgumentException">A field contains an embedded NUL byte.</exception>
    /// <exception cref="InvalidOperationException">The daemon refused the write.</exception>
    public async Task SetAsync(
        string key,
        string? value,
        string? type = null,
        uint subject = SubjectCore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfContainsNul(key, nameof(key));
        ThrowIfContainsNul(value, nameof(value));
        ThrowIfContainsNul(type, nameof(type));
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
        long epoch = Volatile.Read(ref _epoch);
        MetadataReconciler.PendingWrite pending = _reconciler.NoteWrite(subject, key, type, value);

        // Whether the request actually went out decides what a failure means in the catch below.
        bool issued = false;

        // What the cache held before the optimistic apply, so a refusal can be undone.
        var applied = new PipeWireMetadataEntry(subject, key, type, value);
        PipeWireMetadataEntry? previous = null;
        bool didApply = false;

        try
        {
            // Permission is refused out of band, on the core's error stream, so the call returning
            // without a negative code proves nothing on its own - and the write has to go out with
            // the listener already attached, or a refusal answered in between is never seen.
            Task roundTrip = CoreSync.RoundTripAsync(
                _ctx,
                () =>
                {
                    issued = true;
                    return Write(subject, key, type, value);
                },
                cancellationToken);

            // Raised here, not when the echo lands. A write superseded before its echo arrives
            // never gets one, and a caller should still be told about a change it made. Skipped
            // when a clear happened while this was in flight, since the clear is the later intent.
            //
            // Checked and applied under one gate, which ClearAsync also takes. Separately, the
            // check could pass against the old epoch and the apply then land after the clear had
            // emptied the store, putting a cleared entry back.
            lock (_clearGate)
            {
                if (Volatile.Read(ref _epoch) == epoch)
                {
                    _entries.TryGetValue((subject, key), out previous);
                    Apply(applied);
                    Raise(applied);
                    didApply = true;
                }
            }

            await roundTrip.ConfigureAwait(false);
            _reconciler.Settle(subject, key);
        }
        catch (PipeWireException)
        {
            // A refusal, not a cancellation. The value was applied optimistically and the daemon
            // said no, so nothing will ever correct it: no echo is coming for a write that did not
            // happen, and the key reads back as the value it was refused. Cancellation deliberately
            // does not roll back - the request is already on its way and the daemon may still apply
            // it, so the optimistic value remains the better guess there.
            Rollback(subject, key, applied, previous, didApply);
            if (!issued) _reconciler.Forget(subject, key, pending);
            else _reconciler.Settle(subject, key);
            throw;
        }
        catch
        {
            // Only a write that never went out is forgotten. One that was issued and then failed,
            // which is what cancelling gives you, is still going to be echoed: the daemon was never
            // told to stop. Forgetting it makes that echo read as another client's change, so it
            // gets applied - after any newer write, which it then overwrites. A cancelled write
            // followed by a successful one leaves the cancelled value in the cache.
            //
            // Left outstanding, the record does its job: the echo is recognised as ours and, being
            // superseded by the later write, suppressed. It expires with the window if the echo
            // never comes at all.
            //
            // By identity, not by value: removing every entry with the same value would discard the
            // bookkeeping of an identical write that is still in flight.
            // Records age from acknowledgement, and a write that went out and then failed will
            // never get one. Starting the clock here is what keeps it from being kept for ever;
            // it stays recognisable for the window, which is the whole reason it was not forgotten.
            if (!issued) _reconciler.Forget(subject, key, pending);
            else _reconciler.Settle(subject, key);
            throw;
        }
    }

    /// <summary>Serialises the optimistic apply against a store-wide clear.</summary>
    private readonly object _clearGate = new();

    /// <summary>Undoes an optimistic apply the daemon refused.</summary>
    /// <remarks>
    /// Only when the cache still holds exactly what this write put there. A later write that
    /// succeeded, or an external change that arrived meanwhile, is the newer truth and must not be
    /// replaced by a value from before a write that never happened.
    /// </remarks>
    private void Rollback(
        uint subject,
        string key,
        PipeWireMetadataEntry applied,
        PipeWireMetadataEntry? previous,
        bool didApply)
    {
        if (!didApply) return;

        lock (_clearGate)
        {
            _entries.TryGetValue((subject, key), out PipeWireMetadataEntry? now);

            bool stillOurs = now is null
                ? applied.Value is null
                : string.Equals(now.Value, applied.Value, StringComparison.Ordinal)
                  && string.Equals(now.Type, applied.Type, StringComparison.Ordinal);

            if (!stillOurs) return;

            if (previous is null)
                _entries.TryRemove((subject, key), out _);
            else
                _entries[(subject, key)] = previous;

            // The subscriber was told the value changed, so it has to be told it changed back. A
            // removal is reported as an entry with no value, which is how every other removal here
            // is reported.
            Raise(previous ?? new PipeWireMetadataEntry(subject, key, null, null));
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

    // ------------------------------------------------------------------ the settings store

    /// <summary>The graph's sample rate in Hz, or null when the store does not carry it.</summary>
    /// <remarks>
    /// Only meaningful on the <c>settings</c> store, and this is the rate the graph is negotiating
    /// around rather than the rate it is running at: a driver that cannot follow keeps its own and
    /// resamples. Unlike the default-device keys, the settings values are bare integers with no
    /// type, not JSON (<c>pipewire/settings.c:255-275</c>).
    /// </remarks>
    public int? ClockRate => Integer("clock.rate");

    /// <summary>The graph's buffer size in samples, or null when the store does not carry it.</summary>
    /// <inheritdoc cref="ClockRate" path="/remarks"/>
    public int? ClockQuantum => Integer("clock.quantum");

    /// <summary>The smallest quantum the graph will negotiate down to.</summary>
    /// <inheritdoc cref="ClockRate" path="/remarks"/>
    public int? ClockMinQuantum => Integer("clock.min-quantum");

    /// <summary>The largest quantum the graph will negotiate up to.</summary>
    /// <inheritdoc cref="ClockRate" path="/remarks"/>
    public int? ClockMaxQuantum => Integer("clock.max-quantum");

    /// <summary>The rate the graph is pinned to, or 0 when it is free to negotiate.</summary>
    /// <inheritdoc cref="ClockRate" path="/remarks"/>
    public int? ClockForcedRate => Integer("clock.force-rate");

    /// <summary>The quantum the graph is pinned to, or 0 when it is free to negotiate.</summary>
    /// <inheritdoc cref="ClockRate" path="/remarks"/>
    public int? ClockForcedQuantum => Integer("clock.force-quantum");

    /// <summary>Pins the graph to one quantum, or releases it.</summary>
    /// <param name="samples">The quantum to hold, or 0 to let the graph negotiate again.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// Affects every client on the machine, not just this one, which is what makes it the setting a
    /// low-latency application reaches for and the setting most likely to make someone else glitch.
    /// Release it when finished.
    /// <para>
    /// <b>Nothing here is validated for you, and usually not by the daemon either.</b> The range
    /// check against <see cref="ClockMinQuantum"/> and <see cref="ClockMaxQuantum"/> only runs when
    /// the session enables <c>settings.check-quantum</c>, which is off by default
    /// (<c>pipewire/settings.c:178-187</c>): with it off any value is applied as written, and with
    /// it on an out-of-range value is dropped with an info-level log and no error. Either way the
    /// write reports success, so read <see cref="ClockForcedQuantum"/> back to find out what
    /// actually took.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="samples"/> is negative.</exception>
    public Task SetForcedQuantumAsync(int samples, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(samples);
        return SetAsync("clock.force-quantum", samples.ToString(CultureInfo.InvariantCulture),
            null, SubjectCore, cancellationToken);
    }

    /// <summary>Pins the graph to one sample rate, or releases it.</summary>
    /// <param name="hz">The rate to hold, or 0 to let the graph negotiate again.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// <inheritdoc cref="SetForcedQuantumAsync" path="/remarks/para"/>
    /// The rate is checked against the daemon's allowed-rates list rather than a range, and that
    /// check is likewise gated on <c>settings.check-rate</c> (<c>pipewire/settings.c:171-177</c>);
    /// read <see cref="ClockForcedRate"/> back to confirm.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hz"/> is negative.</exception>
    public Task SetForcedRateAsync(int hz, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hz);
        return SetAsync("clock.force-rate", hz.ToString(CultureInfo.InvariantCulture),
            null, SubjectCore, cancellationToken);
    }

    /// <summary>Reads a settings value that is a bare integer, or null if it is absent or not one.</summary>
    private int? Integer(string key) =>
        Find(key)?.Value is { } raw && int.TryParse(raw, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    /// <summary>Sets the default audio sink by node name.</summary>
    /// <param name="nodeName">The <c>node.name</c> of the sink, not its id.</param>
    /// <param name="cancellationToken">
    /// Abandons the wait. The request is already on its way, so cancelling does not recall
    /// it: the daemon can still apply the change after this throws.
    /// </param>
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
    /// <param name="cancellationToken">
    /// Abandons the wait. The request is already on its way, so cancelling does not recall
    /// it: the daemon can still apply the change after this throws.
    /// </param>
    /// <inheritdoc cref="SetDefaultAudioSinkAsync" path="/remarks"/>
    /// <exception cref="ArgumentException"><paramref name="nodeName"/> is null or empty.</exception>
    public Task SetDefaultAudioSourceAsync(string nodeName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeName);
        return SetAsync("default.audio.source", NameJson(nodeName), "Spa:String:JSON",
            SubjectCore, cancellationToken);
    }

    /// <summary>Removes every entry in the store.</summary>
    /// <param name="cancellationToken">
    /// Abandons the wait. The request is already on its way, so cancelling does not recall
    /// it: the daemon can still apply the change after this throws.
    /// </param>
    /// <exception cref="InvalidOperationException">The daemon refused.</exception>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await CoreSync.RoundTripAsync(_ctx, ClearNative, cancellationToken).ConfigureAwait(false);

        // The bump and the emptying are one step, under the gate a write takes to apply itself, so
        // a write in flight either lands entirely before this or sees the new epoch and declines.
        // Split apart, a write can pass its epoch check against the old value and then apply into a
        // store this has already emptied, putting a cleared entry back.
        //
        // The in-flight records go too: left alone, echoes of writes issued before the clear are
        // still recognised as ours and their values go back into an emptied store.
        lock (_clearGate)
        {
            Interlocked.Increment(ref _epoch);
            _entries.Clear();
            _reconciler.Clear();
        }
    }

    private unsafe int ClearNative()
    {
        // Referenced for the duration of the call. Destroying a proxy clears its pointer before it
        // takes the loop lock, so the lock alone does not stop this becoming null mid-call.
        if (!_bound!.TryUse(out BoundProxy.Use proxy))
            throw new ObjectDisposedException(nameof(PipeWireMetadataStore));

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
            throw new ObjectDisposedException(nameof(PipeWireMetadataStore));

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
        // Sampled at the top, before anything is decided. A clear is issued from a caller thread
        // and completes its round trip there, while this runs on the loop thread: the daemon
        // dispatches in order, but the two threads do not, so an echo that was dispatched before
        // the clear can still be executing here when the clear empties the store. Comparing the
        // epoch under the same gate the clear takes is what separates "arrived before the clear"
        // from "arrived after it", which the event itself carries no way to tell.
        long epoch = Volatile.Read(ref _epoch);

        // A null key means "every entry for this subject is gone", which is how a store reports a
        // bulk removal. Anything else is one entry set or, with a null value, removed.
        if (key is null)
        {
            // SPA_ID_INVALID means every subject, not a subject numbered 0xFFFFFFFF. Comparing it
            // to a stored subject matches nothing, so a store-wide clear would drop no entries at
            // all and the cache would keep reporting values the server no longer has.
            bool everySubject = subject == Native.SPA_ID_INVALID;

            // Reported one entry at a time. A subject-wide clear changes the store exactly as an
            // individual removal does, and a consumer that only listens would otherwise never learn
            // that the entries went.
            foreach ((uint Subject, string Key) existing in _entries.Keys)
            {
                if (!everySubject && existing.Subject != subject) continue;
                if (_entries.TryRemove(existing, out PipeWireMetadataEntry? removed))
                    Raise(new PipeWireMetadataEntry(removed.Subject, removed.Key, removed.Type, null));
            }

            return;
        }

        var entry = new PipeWireMetadataEntry(subject, key, type, value);

        MetadataReconciler.EchoAction action = _reconciler.Classify(subject, key, type, value);
        if (action == MetadataReconciler.EchoAction.Drop) return;

        lock (_clearGate)
        {
            // A clear landed between this event being dispatched and being handled, so whatever
            // this reports is from before the store was emptied. Applying it now would put a
            // cleared entry back, and the correcting event for that never comes.
            if (Volatile.Read(ref _epoch) != epoch) return;

            Apply(entry);
        }

        // Outside the gate. Subscribers are user code running on the loop thread, and holding a
        // lock across them lets one that waits on anything else stall every writer.
        if (action != MetadataReconciler.EchoAction.AlreadyKnown) Raise(entry);
    }

    /// <summary>Reports one change to subscribers, isolating a handler that throws.</summary>
    private void Raise(PipeWireMetadataEntry entry)
    {
        SafeCallback.Raise(EntryChanged, h => h(this, entry), ex => LogHandlerFaulted(Id, entry.Key, ex));
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
                DaemonText.Bytes((sbyte*)p));
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
