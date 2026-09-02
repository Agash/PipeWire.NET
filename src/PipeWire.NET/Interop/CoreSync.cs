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
    private int _seq;

    // The request this round-trip is reporting on, or null when it is only a barrier.
    private int? _watchedSeq;
    private bool _disposed;

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
    /// <param name="request">Issues the request; returns the interface method's own result code.</param>
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
        var sync = new CoreSync(ctx) { _watchedSeq = watchedSeq };
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
        if (data is null) return;
        if (GCHandle.FromIntPtr((nint)data).Target is not CoreSync self) return;
        // Compared with the async bit masked off at both ends: the value handed to us came from a
        // request's return code, and what arrives here carries the tag.
        if (self._watchedSeq is not int watched ||
            Native.SPA_RESULT_ASYNC_SEQ(seq) != Native.SPA_RESULT_ASYNC_SEQ(watched))
        {
            return;
        }

        string text = message is null
            ? $"code {res}"
            : System.Text.Encoding.UTF8.GetString(
                MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)message));

        self._done.TrySetException(new PipeWireException("request", res, id, text));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnDone(void* data, uint id, int seq)
    {
        if (data is null) return;
        if (GCHandle.FromIntPtr((nint)data).Target is CoreSync self &&
            id == Native.PW_ID_CORE && seq == self._seq)
            self._done.TrySetResult();
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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
