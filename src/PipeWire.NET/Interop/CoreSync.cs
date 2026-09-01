using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Generated;

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
    private GCHandle _self;
    private int _seq;

    // The request this round-trip is reporting on, or null when it is only a barrier.
    private int? _watchedSeq;
    private bool _disposed;

    private CoreSync(PipeWireContext ctx) => _ctx = ctx;

    internal static Task RoundTripAsync(PipeWireContext ctx, CancellationToken cancellationToken) =>
        RoundTripAsync(ctx, watchedSeq: null, cancellationToken);

    /// <summary>
    /// Round-trips the core, and fails if the daemon reports an error against
    /// <paramref name="watchedSeq"/> before the barrier completes.
    /// </summary>
    /// <remarks>
    /// An asynchronous request reports its outcome out of band, so this is the only way to learn
    /// whether one actually succeeded. Events are ordered, so an error for a request issued before
    /// the sync is always delivered before that sync's <c>done</c>.
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

    private unsafe void Start()
    {
        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        _events = (pw_core_events*)NativeMemory.AllocZeroed((nuint)sizeof(pw_core_events));
        _events->version = Native.PW_VERSION_CORE_EVENTS;
        _events->done = &OnDone;
        _events->error = &OnError;
        _hook = (spa_hook*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook));

        using (_ctx.Lock())
        {
            Native.pw_core_add_listener(_ctx.CoreHandle, _hook, _events, (void*)GCHandle.ToIntPtr(_self));
            _seq = Native.pw_core_sync(_ctx.CoreHandle, Native.PW_ID_CORE, 0);
        }
    }

    private async Task AwaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            using (cancellationToken.UnsafeRegister(static s => ((CoreSync)s!)._done.TrySetCanceled(), this))
                await _done.Task.ConfigureAwait(false);
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
        if (self._watchedSeq is not int watched || seq != watched) return;

        string text = message is null
            ? $"code {res}"
            : System.Text.Encoding.UTF8.GetString(
                MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)message));

        self._done.TrySetException(
            new InvalidOperationException($"PipeWire rejected the request on object {id}: {text}"));
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

        // A disposed context has already destroyed the loop and everything hooked into it, so
        // detaching now would touch freed memory and taking the lock would throw out of Dispose.
        if (!_ctx.IsDisposed)
        {
            using (_ctx.Lock())
                Native.spa_hook_remove(_hook);
        }

        if (_hook is not null) { NativeMemory.Free(_hook); _hook = null; }
        if (_events is not null) { NativeMemory.Free(_events); _events = null; }
        if (_self.IsAllocated) _self.Free();
    }
}
