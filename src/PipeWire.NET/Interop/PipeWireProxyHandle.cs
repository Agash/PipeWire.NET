using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Interop;

/// <summary>
/// Owns a <c>pw_proxy</c> returned by <c>pw_core_create_object</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>pw_proxy_destroy</c> is the single destruction operation: it asks the server to destroy the
/// resource when one is still live, and finishes local teardown when the server already removed it.
/// Calling it twice trips an assertion in PipeWire and aborts the process, so the handle owns that
/// call exclusively - a <c>removed</c> event handler must record the removal and leave the destroy
/// to disposal.
/// </para>
/// <para>
/// Destruction runs under the thread-loop lock, which is why the handle holds a reference on
/// <see cref="PipeWireLoopHandle"/> for its whole lifetime.
/// </para>
/// <para>
/// The listener's memory belongs to the handle too, for the same reason the proxy does: the daemon
/// dispatches through the events table until the proxy is destroyed, and destruction is deferred
/// for as long as anyone holds a reference. Freeing that memory when the owner is disposed rather
/// than when the proxy actually goes would leave a live listener pointing into freed memory.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class PipeWireProxyHandle : SafeHandle
{
    private readonly PipeWireLoopHandle _loop;
    private readonly PipeWireCoreHandle? _core;
    private bool _loopReferenced;
    private bool _coreReferenced;
    private void* _events;
    private spa_hook* _hook;
    private GCHandle _self;

    internal PipeWireProxyHandle(pw_proxy* proxy, PipeWireLoopHandle loop, PipeWireCoreHandle? core = null)
        : base((IntPtr)proxy, ownsHandle: true)
    {
        ArgumentNullException.ThrowIfNull(loop);

        // Both, and in this order. Destroying a proxy needs the loop to take its lock, and needs
        // its core to still be connected or the daemon never learns the object is gone.
        // Rolled back explicitly. If the second reference throws, the first is already taken and
        // the constructor never completes, so nothing would ever release it.
        _loop = loop;
        _core = core;
        try
        {
            loop.DangerousAddRef(ref _loopReferenced);
            core?.DangerousAddRef(ref _coreReferenced);
        }
        catch
        {
            if (_coreReferenced) { core!.DangerousRelease(); _coreReferenced = false; }
            if (_loopReferenced) { loop.DangerousRelease(); _loopReferenced = false; }
            throw;
        }
    }

    /// <summary>
    /// Hands the handle the listener memory to free once the proxy has actually been destroyed.
    /// </summary>
    internal void OwnListener(void* events, spa_hook* hook, GCHandle self)
    {
        _events = events;
        _hook = hook;
        _self = self;
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    internal pw_proxy* Proxy => (pw_proxy*)handle;

    // Set by deterministic disposal before the base runs. The finalizer must never block on the
    // loop mutex, so only a deterministic release destroys inline; an abandoned handle goes to
    // the reaper, which owns every reference from there and waits whatever the loop needs.
    private bool _deterministic;
    private bool _enqueued;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _deterministic = true;
        base.Dispose(disposing);
    }

    protected override bool ReleaseHandle()
    {
        if (!_deterministic && !_enqueued && NeedsLoop(out nint loop))
        {
            _enqueued = true;
            NativeReaper.Enqueue(this, loop, ReleaseHandle);
            return true;
        }

        return ReleaseCore();
    }

    private bool NeedsLoop(out nint loop)
    {
        loop = 0;
        if (handle == IntPtr.Zero || !_loopReferenced || _loop.IsInvalid) return false;
        loop = (nint)_loop.Loop;
        return loop != 0;
    }

    private bool ReleaseCore()
    {
        var proxy = (pw_proxy*)handle;
        handle = IntPtr.Zero;

        try
        {
            if (proxy is not null && _loopReferenced && !_loop.IsInvalid)
            {
                pw_thread_loop* loop = _loop.Loop;

                Native.pw_thread_loop_lock(loop);
                try
                {
                    Native.pw_proxy_destroy(proxy);
                }
                finally
                {
                    Native.pw_thread_loop_unlock(loop);
                }
            }
        }
        finally
        {
            // Released in the reverse order they were taken: the core first, so it can disconnect
            // once the last proxy is gone, then the loop the core itself needs.
            if (_coreReferenced)
            {
                _core!.DangerousRelease();
                _coreReferenced = false;
            }
            if (_loopReferenced)
            {
                _loop.DangerousRelease();
                _loopReferenced = false;
            }

            // After the destroy, never before: destroying the proxy is what detaches the listener,
            // so until that has run the daemon can still dispatch through this table.
            if (_hook is not null)
            {
                NativeMemory.Free(_hook);
                _hook = null;
            }
            if (_events is not null)
            {
                NativeMemory.Free(_events);
                _events = null;
            }
            if (_self.IsAllocated)
                _self.Free();
        }
        return true;
    }
}
