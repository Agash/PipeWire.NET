using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Interop;

/// <summary>
/// A <c>pw_core_sync</c> round-trip: the daemon answers with a <c>done</c> event carrying the same
/// sequence number once it has processed everything requested before the call.
/// </summary>
/// <remarks>
/// Methods and events are delivered in order, so this doubles as proof that any events the earlier
/// requests produced have already been dispatched. That is what makes it a usable barrier for
/// "the initial enumeration has arrived", where a timer only guesses.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class CoreSync : IDisposable
{
    private readonly PipeWireContext _ctx;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private unsafe pw_core_events* _events;
    private unsafe spa_hook* _hook;
    private PipeWireLoopHandle? _loop;
    private bool _loopReferenced;
    private GCHandle _self;

    // Both are written by the thread that starts the round trip and read by the loop thread in the
    // callbacks. The native loop mutex orders them in practice, but the .NET memory model knows
    // nothing about it, so the accesses say so themselves.
    private volatile int _seq;

    // The request this round-trip is reporting on, or NoWatchedSequence when there is no sequence
    // to correlate against.
    private volatile int _watchedSeq = NoWatchedSequence;

    // Whether this round-trip issued a request at all. A barrier issues none, so no error on the
    // core stream belongs to it: the stream carries every request this connection has in flight,
    // and a barrier that failed on another one would turn somebody else's refusal into its own.
    private volatile bool _carriesRequest;

    // Claimed with an interlocked exchange: AwaitAsync disposes in its finally and the failure path
    // in RoundTripAsync disposes too, and the two can meet.
    private int _disposed;

    /// <summary>No request sequence to correlate errors against.</summary>
    /// <remarks>
    /// A sentinel rather than a nullable, because the field is read from a native callback and
    /// <c>volatile</c> does not apply to <c>int?</c>. Zero is a real sequence number, so it cannot
    /// serve; -1 is not, because a sequence is masked out of a non-negative result code.
    /// </remarks>
    private const int NoWatchedSequence = -1;

    private CoreSync(PipeWireContext ctx) => _ctx = ctx;

    internal static Task RoundTripAsync(PipeWireContext ctx, CancellationToken cancellationToken) =>
        RoundTripAsync(ctx, watchedSeq: null, cancellationToken);

    /// <summary>
    /// Round-trips the core around <paramref name="request"/>, which is issued with the listener
    /// already attached.
    /// </summary>
    /// <remarks>
    /// The listener has to go on first, and under the same lock. A refusal is dispatched on the
    /// loop thread as soon as the daemon answers, so a listener attached after the request was sent
    /// can miss it altogether - and the caller then sees a refused operation report success.
    /// </remarks>
    /// <param name="ctx">The context whose core is round-tripped.</param>
    /// <param name="request">
    /// Issues the request; returns the interface method's own result code. Runs under the loop lock,
    /// which is what makes it race-free against the reply, so it must be one native call and
    /// nothing else - anything that blocks, waits on the loop, or disposes something stops the loop
    /// thread from ever delivering the answer this call is waiting for.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    internal static Task RoundTripAsync(
        PipeWireContext ctx, Func<int> request, CancellationToken cancellationToken)
    {
        var sync = new CoreSync(ctx);
        try
        {
            sync.StartAround(request);
        }
        catch
        {
            sync.Dispose();
            throw;
        }
        return sync.AwaitAsync(cancellationToken);
    }

    /// <summary>
    /// Round-trips the core, failing if the daemon reports an error against
    /// <paramref name="watchedSeq"/> before the barrier completes.
    /// </summary>
    /// <remarks>
    /// For requests already issued. Prefer the overload taking the request itself, which cannot
    /// miss a refusal answered before the listener went on.
    /// </remarks>
    internal static Task RoundTripAsync(PipeWireContext ctx, int? watchedSeq, CancellationToken cancellationToken)
    {
        var sync = new CoreSync(ctx)
        {
            _watchedSeq = watchedSeq ?? NoWatchedSequence,

            // A sequence was handed in, so a request exists even though this call did not issue it.
            _carriesRequest = watchedSeq is not null,
        };
        try
        {
            sync.Start();
        }
        catch
        {
            sync.Dispose();
            throw;
        }
        return sync.AwaitAsync(cancellationToken);
    }

    private unsafe void AllocateListener()
    {
        // A reference of its own on the loop, held for as long as the listener is attached. The
        // context is not enough: it clears its handle fields while proxies are still keeping the
        // native loop and core alive, and the detach in Dispose has to work in exactly that window.
        _loop = _ctx.LoopOwner;
        _loop.DangerousAddRef(ref _loopReferenced);

        // Strong, unlike the listener handles on the long-lived graph objects, and deliberately
        // so. This object lives exactly as long as one round trip, and while that is in flight the
        // GCHandle can be the only thing referencing it: a caller that awaits the task without
        // keeping the object holds nothing else. A weak handle would let it be collected before the
        // reply arrives, and the callback would find a dead target and never complete the waiter.
        // The handle is freed on the completion path, which always runs.
        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        _events = (pw_core_events*)NativeMemory.AllocZeroed((nuint)sizeof(pw_core_events));
        _events->version = Native.PW_VERSION_CORE_EVENTS;
        _events->done = &OnDone;
        _events->error = &OnError;
        _hook = (spa_hook*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook));
    }

    private unsafe void Start()
    {
        AllocateListener();

        using (_ctx.Lock())
        {
            // Both are checked. Without the listener nothing completes the wait, and without the
            // sync no done event is coming - either way the caller waits for an answer that cannot
            // arrive, and only its cancellation token ever ends it.
            int added = Native.pw_core_add_listener(_ctx.CoreHandle, _hook, _events, (void*)GCHandle.ToIntPtr(_self));
            if (added < 0)
                throw new PipeWireException("pw_core_add_listener", added);

            _seq = Native.pw_core_sync(_ctx.CoreHandle, Native.PW_ID_CORE, 0);
            if (_seq < 0)
                throw new PipeWireException("pw_core_sync", _seq);
        }
    }

    private unsafe void StartAround(Func<int> request)
    {
        AllocateListener();

        using (_ctx.Lock())
        {
            // Attached before the request is issued, so nothing the daemon says about it can arrive
            // before there is somebody listening.
            int added = Native.pw_core_add_listener(_ctx.CoreHandle, _hook, _events, (void*)GCHandle.ToIntPtr(_self));
            if (added < 0)
                throw new PipeWireException("pw_core_add_listener", added);

            _carriesRequest = true;

            int rc = request();
            if (rc < 0)
                throw new PipeWireException("request", rc);

            if (Native.SPA_RESULT_IS_ASYNC(rc))
                _watchedSeq = Native.SPA_RESULT_ASYNC_SEQ(rc);

            _seq = Native.pw_core_sync(_ctx.CoreHandle, Native.PW_ID_CORE, 0);
            if (_seq < 0)
                throw new PipeWireException("pw_core_sync", _seq);
        }
    }

    private async Task AwaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The caller's token ends the wait early; the context's ends it at all. A round-trip
            // completes when the daemon answers on the loop, so once the context is disposing there
            // is no answer coming - and a caller that passed no token of its own would wait forever.
            using (cancellationToken.UnsafeRegister(static s => ((CoreSync)s!)._done.TrySetCanceled(), this))
            using (_ctx.Shutdown.UnsafeRegister(
                static s => ((CoreSync)s!)._done.TrySetException(
                    new ObjectDisposedException(nameof(PipeWireContext),
                        "the context was disposed while a round-trip to the daemon was outstanding.")),
                this))
            {
                await _done.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnError(void* data, uint id, int seq, int res, sbyte* message)
    {
        // Contained whole. Decoding the daemon's message is a read through a pointer this process
        // did not allocate, and an exception escaping a native frame aborts rather than unwinding.
        try
        {
            if (data is null) return;
            if (GCHandle.FromIntPtr((nint)data).Target is not CoreSync self) return;

            // A barrier owns no request, so nothing on this stream is its to report.
            if (!self._carriesRequest) return;

            int watched = self._watchedSeq;
            if (watched != NoWatchedSequence)
            {
                // Compared with the async bit masked off at both ends: the value handed to us came
                // from a request's return code, and what arrives here carries the tag. An error on
                // the core itself is not correlated by sequence at all and is fatal to the
                // connection, so the round trip must fail on it rather than wait for a done that is
                // no longer coming.
                if (id != Native.PW_ID_CORE
                    && Native.SPA_RESULT_ASYNC_SEQ(seq) != Native.SPA_RESULT_ASYNC_SEQ(watched))
                {
                    return;
                }
            }

            // A request that completed synchronously - destroying a global, updating permissions -
            // returns 0 rather than an async sequence, so there is nothing to match on and every
            // error in this window is taken as its own. Two such requests overlapping on one
            // connection can cross-attribute; reporting the wrong operation beats the alternative,
            // which is a refused operation returning success.
            string text = DaemonText.String(message) ?? $"code {res}";

            self._done.TrySetException(new PipeWireException("request", res, id, text));
        }
        catch (Exception)
        {
            // Deliberately not logged: there is no instance to log through if the lookup is what
            // failed, and this frame cannot let anything escape.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnDone(void* data, uint id, int seq)
    {
        try
        {
            if (data is null) return;
            if (GCHandle.FromIntPtr((nint)data).Target is CoreSync self &&
                id == Native.PW_ID_CORE && seq == self._seq)
                self._done.TrySetResult();
        }
        catch (Exception)
        {
            // Deliberately not logged: as above.
        }
    }

    public unsafe void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Detached through this object's own loop reference rather than the context's lock. A
        // disposed context refuses its lock while the native core is still alive and still holding
        // this listener, and freeing the events table then leaves the core dispatching into it -
        // which is a segfault inside pw_core_disconnect, not a leak.
        try
        {
            if (_loopReferenced && !_loop!.IsInvalid)
            {
                pw_thread_loop* loop = _loop!.Loop;
                Native.pw_thread_loop_lock(loop);
                try
                {
                    Native.spa_hook_remove(_hook);
                }
                finally
                {
                    Native.pw_thread_loop_unlock(loop);
                }
            }
        }
        finally
        {
            if (_hook is not null) { NativeMemory.Free(_hook); _hook = null; }
            if (_events is not null) { NativeMemory.Free(_events); _events = null; }
            if (_self.IsAllocated) _self.Free();

            if (_loopReferenced)
            {
                _loop!.DangerousRelease();
                _loopReferenced = false;
            }
        }
    }
}
