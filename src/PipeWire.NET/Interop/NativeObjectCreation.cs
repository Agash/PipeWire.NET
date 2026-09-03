using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Interop;

/// <summary>
/// Awaits the two daemon round-trips that follow <c>pw_core_create_object</c>: the proxy's
/// <c>bound</c> event, which assigns the global id, and a <c>pw_core_sync</c> barrier, which
/// guarantees the matching registry <c>global</c> event has been delivered.
/// </summary>
/// <remarks>
/// Both are needed. <c>bound</c> establishes identity but carries no object properties, and
/// <c>bound_props</c> carries only the creation-time subset - a link's four endpoint ids arrive on
/// the registry event. Waiting for <c>done</c> is what makes the object safe to look up.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class NativeObjectCreation : IDisposable
{
    private readonly PipeWireContext _ctx;
    private readonly TaskCompletionSource<uint> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _synced = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private unsafe pw_proxy_events* _proxyEvents;
    private unsafe pw_core_events*  _coreEvents;
    private unsafe spa_hook*        _proxyHook;
    private unsafe spa_hook*        _coreHook;
    private PipeWireLoopHandle? _loop;
    private bool _loopReferenced;
    private GCHandle         _self;
    private IntPtr           _proxy;

    // Written by the thread that starts the creation and read by the loop thread in the callbacks
    // below. The native loop mutex orders them in practice, but nothing in the .NET memory model
    // knows about it, so the accesses say so themselves.
    private volatile uint    _proxyId = Native.SPA_ID_INVALID;

    // The daemon reports a refused creation on the core rather than on a proxy that was never
    // bound, but the core stream carries every client's errors. Kept as the reason to quote if this
    // creation turns out to have failed, never as the trigger for deciding that it did.
    private volatile int     _lastCoreResult;
    private volatile string? _lastCoreMessage;
    // Two barriers, two sequence numbers. The first proves the daemon has dealt with the create
    // request, so a bound that has not arrived by then never will. The second proves the new object
    // has reached our own registry. Sharing one field would let the first one satisfy the second's
    // await, and the caller would be handed an id the graph does not know yet.
    private volatile int     _probeSeq = NoSequence;
    private volatile int     _syncSeq = NoSequence;
    private volatile bool    _probed;

    // Claimed with an interlocked exchange: CompleteAsync disposes in its finally and a caller can
    // dispose from outside, and both freeing the same native blocks is heap corruption rather than
    // a wasted call.
    private int              _disposed;

    private NativeObjectCreation(PipeWireContext ctx) => _ctx = ctx;

    /// <summary>
    /// Creates an object through <paramref name="factoryName"/> and returns its global id once the
    /// registry has been told about it.
    /// </summary>
    /// <returns>The new object's global id, and an owning handle for its proxy.</returns>
    /// <remarks>
    /// Reports on <see cref="PipeWireDiagnostics"/>: creation is a factory call, a barrier proving
    /// the daemon dealt with it, and a second barrier proving the registry has it. Which of the
    /// three a slow creation is waiting in is not answerable from log lines once two are in flight.
    /// </remarks>
    internal static unsafe Task<(uint Id, PipeWireProxyHandle Proxy)> CreateAsync(
        PipeWireContext ctx,
        ReadOnlySpan<byte> factoryName,
        ReadOnlySpan<byte> interfaceType,
        uint interfaceVersion,
        spa_dict props,
        CancellationToken cancellationToken,
        Action<uint>? onBound = null)
    {
        // The native setup is synchronous so the spans never cross an await.
        var pending = new NativeObjectCreation(ctx);
        try
        {
            pending.Start(factoryName, interfaceType, interfaceVersion, props);
        }
        catch
        {
            pending.DestroyProxy();
            pending.Dispose();
            throw;
        }
        // Issued before anything is awaited. Events are ordered, so the daemon answering this sync
        // means it has already processed the create: if bound has not arrived by then, it never
        // will. That is what makes a failed creation detectable without reading errors addressed to
        // other objects off the shared core stream.
        pending.RequestProbeSync();

        return pending.CompleteAsync(onBound, cancellationToken);
    }

    /// <param name="onBound">
    /// Invoked with the new id the moment <c>bound</c> arrives, which is before the registry's
    /// <c>global</c> for the same object. That ordering is what lets a caller register interest in
    /// the id in time to catch the object as it is published, rather than looking it up afterwards
    /// and racing anything that removes it in between.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait and destroys the half-created object.</param>
    private async Task<(uint Id, PipeWireProxyHandle Proxy)> CompleteAsync(
        Action<uint>? onBound, CancellationToken cancellationToken)
    {
        try
        {
            using (cancellationToken.UnsafeRegister(static s => ((NativeObjectCreation)s!).Cancel(), this))
            {
                uint id = await _bound.Task.ConfigureAwait(false);
                onBound?.Invoke(id);

                // A second barrier, so the object is in our own registry before the caller is told
                // it exists. The first one only proved the daemon had dealt with the request.
                RequestSync();
                await _synced.Task.ConfigureAwait(false);
                return (id, TakeProxy());
            }
        }
        catch
        {
            DestroyProxy();
            throw;
        }
        finally
        {
            Dispose();
        }
    }

    private unsafe void Start(ReadOnlySpan<byte> factoryName, ReadOnlySpan<byte> interfaceType,
                       uint interfaceVersion, spa_dict props)
    {
        // A reference of its own on the loop, held for as long as the listeners are attached. The
        // context is not enough: it clears its handle fields while the created proxy is still
        // keeping the native loop and core alive, and the detach in Dispose has to work in exactly
        // that window.
        _loop = _ctx.LoopOwner;
        _loop.DangerousAddRef(ref _loopReferenced);

        // Strong, unlike the listener handles on the long-lived graph objects, and deliberately
        // so. This object lives exactly as long as one creation, and while that is in flight the
        // GCHandle can be the only thing referencing it: a caller that awaits the task without
        // keeping the object holds nothing else. A weak handle would let it be collected before the
        // reply arrives, and the callback would find a dead target and never complete the waiter.
        // The handle is freed on the completion path, which always runs.
        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        void* data = (void*)GCHandle.ToIntPtr(_self);

        _proxyEvents = (pw_proxy_events*)NativeMemory.AllocZeroed((nuint)sizeof(pw_proxy_events));
        _proxyEvents->version = Native.PW_VERSION_PROXY_EVENTS;
        _proxyEvents->bound   = &OnBound;
        _proxyEvents->error   = &OnProxyError;
        _proxyHook = (spa_hook*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook));

        _coreEvents = (pw_core_events*)NativeMemory.AllocZeroed((nuint)sizeof(pw_core_events));
        _coreEvents->version = Native.PW_VERSION_CORE_EVENTS;
        _coreEvents->done    = &OnDone;
        _coreEvents->error   = &OnCoreError;
        _coreHook = (spa_hook*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook));

        using (_ctx.Lock())
        {
            Native.pw_core_add_listener(_ctx.CoreHandle, _coreHook, _coreEvents, data);

            fixed (byte* factory = factoryName)
            fixed (byte* type = interfaceType)
            {
                spa_dict local = props;
                _proxy = (IntPtr)Native.pw_core_create_object(
                    _ctx.CoreHandle, (sbyte*)factory, (sbyte*)type, interfaceVersion, &local, 0);
            }

            if (_proxy == IntPtr.Zero)
                throw new PipeWireException("pw_core_create_object", -12);  // ENOMEM

            // The core reports an error against the proxy it happened on, so the id has to be
            // known before any error can arrive or an error belonging to somebody else cannot be
            // told from ours.
            _proxyId = Native.pw_proxy_get_id((pw_proxy*)_proxy);

            Native.pw_proxy_add_listener((pw_proxy*)_proxy, _proxyHook, _proxyEvents, data);
        }
    }

    private unsafe void RequestProbeSync()
    {
        using (_ctx.Lock())
        {
            _probeSeq = Native.pw_core_sync(_ctx.CoreHandle, Native.PW_ID_CORE, 0);
            _probed = true;
        }
    }

    private unsafe void RequestSync()
    {
        using (_ctx.Lock())
            _syncSeq = Native.pw_core_sync(_ctx.CoreHandle, Native.PW_ID_CORE, 0);
    }

    /// <summary>The sequence number a probe has not been issued under.</summary>
    /// <remarks>
    /// Zero is a real sequence, so a default-initialised field would be satisfied by an unrelated
    /// <c>done</c> the daemon sent before the probe went out - and the wait would complete before
    /// the object existed.
    /// </remarks>
    private const int NoSequence = -1;

    private void Cancel()
    {
        _bound.TrySetCanceled();
        _synced.TrySetCanceled();
    }

    /// <summary>Hands the proxy to an owning handle; this instance no longer destroys it.</summary>
    private unsafe PipeWireProxyHandle TakeProxy()
    {
        // Holds both: the loop it needs to take a lock during destruction, and the core that must
        // still be connected for the daemon to learn the object is gone.
        var owned = new PipeWireProxyHandle((pw_proxy*)_proxy, _ctx.LoopOwner, _ctx.CoreOwner);
        _proxy = IntPtr.Zero;
        return owned;
    }

    private unsafe void DestroyProxy()
    {
        if (_proxy == IntPtr.Zero) return;

        // TryLock, not Lock. This runs on the failure and cancellation paths, where the context may
        // already be disposing; a throwing lock there replaces the caller's original error with an
        // ObjectDisposedException and skips the destroy, leaving the object the daemon did create
        // with nobody to take it down.
        if (_ctx.TryLock(out PipeWireContext.LoopLock scope))
        {
            using (scope)
                Native.pw_proxy_destroy((pw_proxy*)_proxy);
        }

        _proxy = IntPtr.Zero;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnBound(void* data, uint globalId)
    {
        try
        {
            if (FromData(data) is { } self) self._bound.TrySetResult(globalId);
        }
        catch (Exception)
        {
            // Deliberately not logged: there is no instance to log through when the lookup is what
            // failed, and an exception leaving this frame aborts the process.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnDone(void* data, uint id, int seq)
    {
        try
        {
            OnDoneCore(data, id, seq);
        }
        catch (Exception)
        {
            // Deliberately not logged: as above.
        }
    }

    private static unsafe void OnDoneCore(void* data, uint id, int seq)
    {
        if (FromData(data) is not { } self) return;
        if (id != Native.PW_ID_CORE) return;

        // The daemon has processed everything sent before the probe. If the object had been
        // created, bound would already have arrived, because events are ordered. So a probe that
        // finds nothing bound is the daemon declining to create it, whatever it said and to whom.
        if (self._probed && seq == self._probeSeq)
        {
            if (!self._bound.Task.IsCompleted)
            {
                self._bound.TrySetException(new PipeWireException(
                    "create",
                    self._lastCoreResult != 0 ? self._lastCoreResult : -22, // EINVAL
                    objectId: null,
                    self._lastCoreMessage ?? "the daemon did not create the object"));
            }

            return;
        }

        // Only once the second barrier has actually been issued. Zero is a real sequence number, so
        // a done the daemon sent for something else before RequestSync ran would otherwise satisfy
        // this wait and hand the caller an id its own registry has not seen yet.
        int syncSeq = self._syncSeq;
        if (syncSeq != NoSequence && seq == syncSeq) self._synced.TrySetResult();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnProxyError(void* data, int seq, int res, sbyte* message)
    {
        try
        {
            Fail(data, res, message, objectId: null);
        }
        catch (Exception)
        {
            // Deliberately not logged: as above.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnCoreError(void* data, uint id, int seq, int res, sbyte* message)
    {
        try
        {
            OnCoreErrorCore(data, id, res, message);
        }
        catch (Exception)
        {
            // Deliberately not logged: as above.
        }
    }

    private static unsafe void OnCoreErrorCore(void* data, uint id, int res, sbyte* message)
    {
        if (FromData(data) is not { } self) return;

        string? text = DaemonText.String(message);

        // Errors about our own proxy fail us. Everything else on this stream belongs to another
        // object, including the core's own: a client racing a removal makes the daemon answer
        // "unknown resource" against the core, which is not fatal to the connection and has nothing
        // to do with whatever creation happens to be in flight. Acting on those failed unrelated
        // creations under concurrent use. The text is kept so a creation that does turn out to have
        // failed can say why.
        if (id != self._proxyId)
        {
            self._lastCoreResult = res;
            self._lastCoreMessage = text;
            return;
        }

        var error = new PipeWireException("create", res, id, text);
        self._bound.TrySetException(error);
        self._synced.TrySetException(error);
    }

    private static unsafe void Fail(void* data, int res, sbyte* message, uint? objectId)
    {
        if (FromData(data) is not { } self) return;

        string? text = DaemonText.String(message);

        // A PipeWireException, not a bare InvalidOperationException with the code spelled into the
        // message. Creation fails for reasons a caller acts on differently: a refused permission is
        // worth reporting to a user, a format that cannot be negotiated is worth trying differently,
        // and telling them apart should not mean parsing a sentence.
        var error = new PipeWireException("create", res, objectId, text);
        self._bound.TrySetException(error);
        self._synced.TrySetException(error);
    }

    /// <summary>Resolves the instance a native callback belongs to, or null if it is gone.</summary>
    /// <remarks>
    /// Contained, because a freed handle throws out of <see cref="GCHandle.FromIntPtr"/> and the
    /// callers are native frames.
    /// </remarks>
    private static unsafe NativeObjectCreation? FromData(void* data)
    {
        if (data is null) return null;

        try
        {
            return GCHandle.FromIntPtr((nint)data).Target as NativeObjectCreation;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public unsafe void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Deferred off the loop thread, the way a bound proxy's teardown is. Detaching a hook while
        // the daemon is walking the list it is in, then freeing the events table behind it, is a
        // use-after-free; disposal here runs from the continuation of an awaited creation, which
        // the loop thread can be the one to resume.
        if (_ctx.IsOnLoopThread)
        {
            NativeObjectCreation self = this;
            _ = Task.Run(self.ReleaseNative);
            return;
        }

        ReleaseNative();
    }

    private unsafe void ReleaseNative()
    {

        // Both hooks must be detached before their memory is freed. On success the caller keeps the
        // proxy, so its listener would otherwise be left pointing at freed memory. spa_hook_remove
        // is a no-op on a hook that was never attached.
        //
        // Detached through this object's own loop reference, not the context's lock. A disposed
        // context refuses its lock while the created proxy is still keeping the core alive - and
        // freeing an attached listener then hands pw_core_disconnect a proxy whose events table is
        // gone, which faults inside pw_proxy_destroy rather than merely leaking.
        try
        {
            if (_loopReferenced && !_loop!.IsInvalid)
            {
                pw_thread_loop* loop = _loop.Loop;
                Native.pw_thread_loop_lock(loop);
                try
                {
                    Native.spa_hook_remove(_coreHook);
                    Native.spa_hook_remove(_proxyHook);
                }
                finally
                {
                    Native.pw_thread_loop_unlock(loop);
                }
            }
        }
        finally
        {
            if (_proxyHook is not null)   { NativeMemory.Free(_proxyHook);   _proxyHook = null; }
            if (_coreHook is not null)    { NativeMemory.Free(_coreHook);    _coreHook = null; }
            if (_proxyEvents is not null) { NativeMemory.Free(_proxyEvents); _proxyEvents = null; }
            if (_coreEvents is not null)  { NativeMemory.Free(_coreEvents);  _coreEvents = null; }
            if (_self.IsAllocated) _self.Free();

            if (_loopReferenced)
            {
                _loop!.DangerousRelease();
                _loopReferenced = false;
            }
        }
    }
}
