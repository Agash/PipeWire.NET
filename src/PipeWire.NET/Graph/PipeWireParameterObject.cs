using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>
/// A bound object whose settings are exchanged as SPA parameters: a node or a device.
/// </summary>
/// <remarks>
/// <para>
/// The registry reports what an object is. Parameters are what it can do and what it is currently
/// doing - a node's volume and mute, a device's profiles and routes - and reaching them needs a
/// proxy bound to that object rather than to the registry.
/// </para>
/// <para>
/// Enumerating is a request/answer exchange, not a return value: the daemon replies with one
/// <c>param</c> event per value and never says it has finished, so the end is found by round-tripping
/// the core afterwards. That is what <see cref="EnumerateParametersAsync(SpaParamType, CancellationToken)"/> hides.
/// </para>
/// <para>
/// Writing is asynchronous and unacknowledged. A parameter the object does not support is dropped
/// silently, which is why <see cref="SetParameterAsync"/> confirms by reading back rather than by
/// trusting the call.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public abstract class PipeWireParameterObject : IDisposable, IAsyncDisposable
{
    private readonly PipeWireContext _ctx;
    // Concurrent rather than a dictionary behind a lock, and deliberately so. A param event is
    // dispatched by the loop thread with the loop lock already held, so anything it waits on must
    // never be held by a thread that is itself waiting for the loop lock.
    private readonly ConcurrentDictionary<int, List<SpaObject>> _answers = new();
    private BoundProxy? _bound;
    // The array reference, not the ImmutableArray wrapping it. A struct assignment has no atomicity
    // guarantee and cannot be published with Volatile; the single reference inside it can.
    private PipeWireParameterInfo[] _parameters = [];
    private int _nextTag;
    private volatile bool _disposed;

    private protected PipeWireParameterObject(PipeWireContext ctx, uint id)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        Id = id;
    }

    /// <summary>The global id of the object this is bound to.</summary>
    public uint Id { get; }

    /// <summary>
    /// Which parameters this object has, and whether each may be read or written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty until the daemon has sent the object's info, which it does unprompted shortly after
    /// binding - <see cref="ReadyAsync"/> is what waits for it.
    /// </para>
    /// <para>
    /// Worth checking before enumerating. Asking for a parameter an object does not have is an
    /// error the daemon reports, not an empty answer.
    /// </para>
    /// </remarks>
    public ImmutableArray<PipeWireParameterInfo> Parameters =>
        ImmutableCollectionsMarshal.AsImmutableArray(Volatile.Read(ref _parameters));

    /// <summary>Whether this object has a parameter, and it may be read.</summary>
    /// <param name="parameter">The parameter to look for.</param>
    public bool CanRead(SpaParamType parameter)
    {
        foreach (PipeWireParameterInfo info in Parameters)
        {
            if (info.Parameter == parameter) return info.CanRead;
        }

        return false;
    }

    /// <summary>Whether this object has a parameter, and it may be written.</summary>
    /// <param name="parameter">The parameter to look for.</param>
    public bool CanWrite(SpaParamType parameter)
    {
        foreach (PipeWireParameterInfo info in Parameters)
        {
            if (info.Parameter == parameter) return info.CanWrite;
        }

        return false;
    }

    /// <summary>
    /// Waits for the daemon to have described this object, so <see cref="Parameters"/> is populated.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// The info event arrives unprompted after binding. Events are ordered, so a core round-trip
    /// cannot answer before it has been dispatched.
    /// </remarks>
    public Task ReadyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CoreSync.RoundTripAsync(_ctx, cancellationToken);
    }

    /// <summary>Raised when the daemon re-describes the object.</summary>
    /// <remarks>
    /// Fires when a device switches profile, or a node gains ports - anything that changes what the
    /// object is. Handlers run on the PipeWire loop thread.
    /// </remarks>
    public event Action<PipeWireParameterObject>? InfoChanged;

    /// <summary>Raised when a subscribed parameter changes.</summary>
    /// <remarks>
    /// Only fires for parameters passed to <see cref="SubscribeParameters"/>. Handlers run on the
    /// PipeWire loop thread; one that throws is reported and does not stop the others.
    /// </remarks>
    public event Action<PipeWireParameterObject, SpaObject>? ParameterChanged;

    private protected PipeWireContext Context => _ctx;

    private protected BoundProxy Bound =>
        _bound ?? throw new InvalidOperationException("the object has not been bound yet.");

    private protected void Attach(BoundProxy bound) => _bound = bound;

    /// <summary>
    /// A number to hand to <c>enum_params</c>. Only for tracing: the protocol does not echo it.
    /// </summary>
    private int NextRequestTag() => Interlocked.Increment(ref _nextTag) & Native.SPA_ASYNC_SEQ_MASK;

    // Each takes the proxy pointer rather than reading it from the binding. Destroying a proxy
    // clears that pointer before it takes the loop lock, so a pointer re-read inside the call could
    // be null even though the lock is held and a check just passed.

    /// <summary>Dispatches this interface's <c>enum_params</c>.</summary>
    private protected abstract unsafe int EnumParamsNative(
        void* proxy, int seq, uint id, uint start, uint num, spa_pod* filter);

    /// <summary>Dispatches this interface's <c>set_param</c>.</summary>
    private protected abstract unsafe int SetParamNative(void* proxy, uint id, uint flags, spa_pod* param);

    /// <summary>Dispatches this interface's <c>subscribe_params</c>.</summary>
    private protected abstract unsafe int SubscribeParamsNative(void* proxy, uint* ids, uint count);

    /// <summary>
    /// Reads every value of one parameter.
    /// </summary>
    /// <param name="parameter">Which parameter to read, such as <see cref="SpaParamType.Props"/>.</param>
    /// <param name="cancellationToken">Abandons the wait; the request itself cannot be recalled.</param>
    /// <returns>
    /// The values, in the order the daemon sent them. Empty means the object has no such parameter,
    /// which is not an error - a node with no volume control simply has no <c>Props</c>.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The binding has been disposed.</exception>
    public async Task<ImmutableArray<SpaObject>> EnumerateParametersAsync(
        SpaParamType parameter, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        return await EnumerateFilteredAsync(parameter, 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads every value of one parameter that matches a filter object.
    /// </summary>
    /// <param name="parameter">Which parameter to read, such as <see cref="SpaParamType.Profile"/>.</param>
    /// <param name="filter">
    /// An object whose scalar properties constrain the results: a candidate is reported only when
    /// every scalar property here equals the candidate's property with the same key. Non-scalar
    /// constraints (choices, ranges, nested objects) are not applied and leave the candidate in,
    /// erring toward a superset the caller can narrow itself; a wrong reject would lose results.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait; the request itself cannot be recalled.</param>
    /// <returns>
    /// The matching values, in the order the daemon sent them. Whether the daemon or this
    /// library's own provider applies the filter depends on who serves the object; either way the
    /// answer contains only matches.
    /// </returns>
    /// <remarks>
    /// Values can arrive normalized: a daemon serving from its cache projects them through the
    /// request filter, so a plain string may come back as a fixed single-default choice. Read
    /// through choice defaults rather than asserting exact shapes.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The binding has been disposed.</exception>
    public Task<ImmutableArray<SpaObject>> EnumerateParametersAsync(
        SpaParamType parameter, SpaObject filter, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();

        // Serialized once and pinned for the synchronous issue below. The native call reads the
        // filter while issuing the request and never afterwards, and the issue runs synchronously
        // inside BeginEnumeration (the wait its task represents is what is asynchronous), so the
        // pin only has to outlive this call, not the enumeration. This stays non-async so no
        // pointer crosses an await boundary.
        byte[] bytes = SpaPod.ToBytes(filter);
        unsafe
        {
            fixed (byte* p = bytes)
                return EnumerateFilteredAsync(parameter, (nint)p, cancellationToken);
        }
    }

    private async Task<ImmutableArray<SpaObject>> EnumerateFilteredAsync(
        SpaParamType parameter, nint filter, CancellationToken cancellationToken)
    {
        // The sequence number the answers carry is not the one handed to enum_params. The protocol
        // replaces it with the connection's own message sequence and returns that, async-tagged, so
        // the collector can only be filed under what came back.
        //
        // Events are ordered, so the daemon cannot answer the round-trip before it has sent every
        // param the request produced. Without the barrier there is nothing to wait on: the protocol
        // has no "that was the last one".
        int key = 0;
        Task roundTrip = BeginEnumeration(
            parameter, filter, k => key = k, cancellationToken);

        try
        {
            await roundTrip.ConfigureAwait(false);

            if (!_answers.TryGetValue(key, out List<SpaObject>? got))
                return [];

            // The list itself is the lock, and nothing else is taken while it is held, so the loop
            // thread appending to it can never be blocked behind another lock.
            lock (got)
                return [.. got];
        }
        finally
        {
            _answers.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Reads the single value of a parameter that has one, or <see langword="null"/> if it has none.
    /// </summary>
    /// <param name="parameter">Which parameter to read.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public async Task<SpaObject?> GetParameterAsync(
        SpaParamType parameter, CancellationToken cancellationToken = default)
    {
        ImmutableArray<SpaObject> all =
            await EnumerateParametersAsync(parameter, cancellationToken).ConfigureAwait(false);
        return all.IsDefaultOrEmpty ? null : all[0];
    }

    /// <summary>
    /// Writes a parameter.
    /// </summary>
    /// <param name="parameter">Which parameter to write.</param>
    /// <param name="value">The value, which must be an object pod of the matching type.</param>
    /// <param name="cancellationToken">
    /// Abandons the wait. The request is already on its way, so cancelling does not recall
    /// it: the daemon can still apply the change after this throws.
    /// </param>
    /// <remarks>
    /// Returns once the daemon has processed the write, not once it has taken effect: an object may
    /// legitimately clamp a volume or ignore a property it does not support. Read the parameter back
    /// if the resulting value matters.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The binding has been disposed.</exception>
    public async Task SetParameterAsync(
        SpaParamType parameter, SpaObject value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // Issued inside the round-trip, so a refusal answered before the listener went on cannot be
        // lost - a rejected write reporting success is the failure that hides.
        byte[] pod = SpaPod.ToBytes(value);
        await BeginWrite(parameter, pod, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Issues a parameter write inside its round-trip, under a held reference.</summary>
    private unsafe Task BeginWrite(
        SpaParamType parameter, byte[] pod, CancellationToken cancellationToken)
    {
        if (!Bound.TryUse(out BoundProxy.Use proxy))
            throw new ObjectDisposedException(nameof(PipeWireParameterObject));

        using (proxy)
        {
            nint obj = (nint)proxy.Object;
            return CoreSync.RoundTripAsync(_ctx, () =>
            {
                fixed (byte* p = pod)
                    return SetParamNative((void*)obj, (uint)parameter, 0, (spa_pod*)p);
            }, cancellationToken);
        }
    }

    private unsafe Task BeginEnumeration(
        SpaParamType parameter, nint filter, Action<int> onKey, CancellationToken cancellationToken)
    {
        if (!Bound.TryUse(out BoundProxy.Use proxy))
            throw new ObjectDisposedException(nameof(PipeWireParameterObject));

        // Every implementation of EnumParamsNative marshals to a remote proxy (pw_node/pw_device/
        // pw_port over the daemon connection), and a marshalled enum_params always answers async
        // with a sequence. A synchronous result can only come from calling a locally implemented
        // object directly, which never passes through this path - so the bucket below is filed
        // exactly when the request was actually queued, and no synchronous public path exists to
        // demonstrate otherwise.

        // The request is issued synchronously inside the round-trip, so the reference only has to
        // outlive that call. A pointer cannot be captured, so it travels as an IntPtr.
        using (proxy)
        {
            nint obj = (nint)proxy.Object;
            return CoreSync.RoundTripAsync(_ctx, () =>
            {
                // 0 to uint.MaxValue: every value the object has of this parameter.
                // The filter travels as an IntPtr because a pointer cannot be captured; it is only
                // ever dereferenced here, synchronously, while the caller's pin is still held.
                int rc = EnumParamsNative(
                    (void*)obj, NextRequestTag(), (uint)parameter, 0, uint.MaxValue,
                    (spa_pod*)filter);

                // Filed while the loop lock is still held, so no param event for this request can
                // have been dispatched yet - the loop thread dispatches them, and it is blocked on
                // that lock until the scope closes.
                // Only when the daemon actually queued the request. A synchronous result has no
                // sequence, and filing a collector under key 0 makes OnParam route any seq-0 param
                // into this enumeration: the caller collects a parameter it did not ask for, and
                // the subscriber that should have been told about it is not.
                if (Native.SPA_RESULT_IS_ASYNC(rc))
                {
                    int key = Native.SPA_RESULT_ASYNC_SEQ(rc);
                    _answers[key] = [];
                    onKey(key);
                }

                return rc;
            }, cancellationToken);
        }
    }

    private SpaParamType[] _subscribed = [];

    /// <summary>The parameters currently subscribed to, in the order they were asked for.</summary>
    /// <remarks>
    /// Empty until <see cref="SubscribeParameters"/> succeeds. The daemon keeps one set per bound
    /// object and each call replaces it, so this is the whole subscription rather than a history.
    /// </remarks>
    public ImmutableArray<SpaParamType> SubscribedParameters => [.. Volatile.Read(ref _subscribed)];

    /// <summary>Stops watching every parameter this binding had subscribed to.</summary>
    /// <remarks>
    /// The daemon takes an empty set as "watch nothing", which is the same call with no ids.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The binding has been disposed.</exception>
    public void UnsubscribeParameters() => SubscribeParameters();

    /// <summary>
    /// Asks the daemon to raise <see cref="ParameterChanged"/> whenever one of these parameters
    /// changes, rather than only when asked.
    /// </summary>
    /// <param name="parameters">The parameters to watch. Replaces any previous subscription.</param>
    /// <exception cref="ObjectDisposedException">The binding has been disposed.</exception>
    public unsafe void SubscribeParameters(params ReadOnlySpan<SpaParamType> parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Span<uint> ids = parameters.Length <= 16
            ? stackalloc uint[parameters.Length]
            : new uint[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
            ids[i] = (uint)parameters[i];

        int rc;
        if (!Bound.TryUse(out BoundProxy.Use proxy))
            throw new ObjectDisposedException(nameof(PipeWireParameterObject));

        using (proxy)
        using (_ctx.Lock())
        {
            fixed (uint* p = ids)
                rc = SubscribeParamsNative(proxy.Object, p, (uint)ids.Length);
        }

        // Recorded so a caller can see what it asked for and stop it again. The call replaces the
        // daemon's set rather than adding to it, so the last request is the whole subscription.
        if (rc >= 0)
            Volatile.Write(ref _subscribed, parameters.ToArray());

        if (rc < 0)
            throw new PipeWireException("subscribe_params", rc);
    }

    /// <summary>
    /// Files one <c>param</c> event, from the loop thread.
    /// </summary>
    /// <remarks>
    /// An unsolicited event - one whose sequence number matches no request in flight - is a
    /// subscription firing, so it is raised rather than dropped. A pod that does not parse is
    /// dropped: there is nothing useful to hand a caller, and throwing here would cross a reverse
    /// P/Invoke boundary and abort the process.
    /// </remarks>
    private protected unsafe void OnParam(int seq, spa_pod* param)
    {
        if (param is null) return;

        // The size is the producer's, and it is used to build the very span the parser then bounds
        // itself against, so a lie here is one the parser cannot see through. Guarded the way
        // PipeWireProfilerReader guards the same shape.
        uint size = *(uint*)param;
        if (size > int.MaxValue - 8) return;

        var bytes = new ReadOnlySpan<byte>(param, 8 + (int)size);
        if (!SpaPod.TryParse(bytes, out SpaValue? value) || value is not SpaObject parsed)
            return;

        bool filed = _answers.TryGetValue(Native.SPA_RESULT_ASYNC_SEQ(seq), out List<SpaObject>? into);
        if (filed)
        {
            lock (into!)
                into.Add(parsed);
        }
        else
        {
            RaiseParameterChanged(parsed);
        }
    }

    private void RaiseParameterChanged(SpaObject value)
    {
        SafeCallback.Raise(ParameterChanged, h => h(this, value), OnHandlerFaulted);
    }

    /// <summary>
    /// Files one <c>info</c> event, from the loop thread.
    /// </summary>
    /// <param name="parameters">The object's <c>spa_param_info</c> array.</param>
    /// <param name="count">How many entries it has.</param>
    private protected unsafe void OnInfo(spa_param_info* parameters, uint count)
    {
        var described = new PipeWireParameterInfo[parameters is null ? 0 : count];
        for (uint i = 0; i < described.Length; i++)
        {
            described[i] = new PipeWireParameterInfo(
                (SpaParamType)parameters![i].id,
                (parameters[i].flags & SpaParamInfoFlags.Read) != 0,
                (parameters[i].flags & SpaParamInfoFlags.Write) != 0);
        }

        Volatile.Write(ref _parameters, described);

        SafeCallback.Raise(InfoChanged, h => h(this), OnHandlerFaulted);
    }

    /// <summary>Reports a subscriber that threw, where the logger is in scope.</summary>
    private protected abstract void OnHandlerFaulted(Exception exception);

    /// <summary>Resolves the instance a native callback belongs to, or null once it is gone.</summary>
    private protected static unsafe T? FromUserData<T>(void* data) where T : PipeWireParameterObject
    {
        var self = (T?)GCHandle.FromIntPtr((nint)data).Target;
        return self is null || self._disposed ? null : self;
    }

    /// <summary>Tears the binding down. Disposal here does no I/O.</summary>
    /// <remarks>
    /// Offered alongside the async form because nothing about this disposal is asynchronous,
    /// so a caller should not be forced to write "await using" for it.
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

        // Anything still waiting for answers gets none; the round-trip it is awaiting completes or
        // faults on its own, and the entry is removed by the enumerating call's finally block.
        _bound?.Dispose();
        _bound = null;

        GC.SuppressFinalize(this);
    }

}
