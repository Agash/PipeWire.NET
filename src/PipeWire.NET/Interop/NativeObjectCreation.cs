using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Generated;

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
    private GCHandle         _self;
    private IntPtr           _proxy;
    private int              _syncSeq;
    private bool             _disposed;

    private NativeObjectCreation(PipeWireContext ctx) => _ctx = ctx;

    /// <summary>
    /// Creates an object through <paramref name="factoryName"/> and returns its global id once the
    /// registry has been told about it.
    /// </summary>
    /// <returns>The new object's global id, and an owning handle for its proxy.</returns>
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
                throw new InvalidOperationException("pw_core_create_object returned null.");

            Native.pw_proxy_add_listener((pw_proxy*)_proxy, _proxyHook, _proxyEvents, data);
        }
    }

    private unsafe void RequestSync()
    {
        using (_ctx.Lock())
            _syncSeq = Native.pw_core_sync(_ctx.CoreHandle, Native.PW_ID_CORE, 0);
    }

    private void Cancel()
    {
        _bound.TrySetCanceled();
        _synced.TrySetCanceled();
    }

    /// <summary>Hands the proxy to an owning handle; this instance no longer destroys it.</summary>
    private unsafe PipeWireProxyHandle TakeProxy()
    {
        var owned = new PipeWireProxyHandle((pw_proxy*)_proxy, _ctx.LoopOwner);
        _proxy = IntPtr.Zero;
        return owned;
    }

    private unsafe void DestroyProxy()
    {
        if (_proxy == IntPtr.Zero) return;
        using (_ctx.Lock())
            Native.pw_proxy_destroy((pw_proxy*)_proxy);
        _proxy = IntPtr.Zero;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnBound(void* data, uint globalId)
    {
        if (FromData(data) is { } self) self._bound.TrySetResult(globalId);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnDone(void* data, uint id, int seq)
    {
        if (FromData(data) is { } self && id == Native.PW_ID_CORE && seq == self._syncSeq)
            self._synced.TrySetResult();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnProxyError(void* data, int seq, int res, sbyte* message) =>
        Fail(data, res, message);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnCoreError(void* data, uint id, int seq, int res, sbyte* message) =>
        Fail(data, res, message);

    private static unsafe void Fail(void* data, int res, sbyte* message)
    {
        if (FromData(data) is not { } self) return;
        string text = message is null ? "(no message)" : Marshal.PtrToStringUTF8((nint)message) ?? "(no message)";
        var error = new InvalidOperationException($"PipeWire reported error {res}: {text}");
        self._bound.TrySetException(error);
        self._synced.TrySetException(error);
    }

    private static unsafe NativeObjectCreation? FromData(void* data) =>
        data is null ? null : GCHandle.FromIntPtr((nint)data).Target as NativeObjectCreation;

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Both hooks must be detached before their memory is freed. On success the caller keeps the
        // proxy, so its listener would otherwise be left pointing at freed memory. spa_hook_remove is
        // a no-op on a hook that was never attached.
        // Skipped when the context is gone: the loop took the hooks with it, so detaching would be
        // a use-after-free and taking the lock would throw out of Dispose.
        if (!_ctx.IsDisposed)
        {
            using (_ctx.Lock())
            {
                Native.spa_hook_remove(_coreHook);
                Native.spa_hook_remove(_proxyHook);
            }
        }

        if (_proxyHook is not null)   { NativeMemory.Free(_proxyHook);   _proxyHook = null; }
        if (_coreHook is not null)    { NativeMemory.Free(_coreHook);    _coreHook = null; }
        if (_proxyEvents is not null) { NativeMemory.Free(_proxyEvents); _proxyEvents = null; }
        if (_coreEvents is not null)  { NativeMemory.Free(_coreEvents);  _coreEvents = null; }
        if (_self.IsAllocated) _self.Free();
    }
}
