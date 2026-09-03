using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PipeWire.NET.Interop;

/// <summary>
/// Owns a <c>pw_impl_metadata</c>: a metadata store this client serves.
/// </summary>
/// <remarks>
/// Unlike a proxy, this is a local object rather than a handle to something the daemon owns, so it
/// needs no core reference. It still needs the loop, because destroying it unregisters a global and
/// touches loop-owned state.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class PipeWireImplMetadataHandle : SafeHandle
{
    private readonly PipeWireLoopHandle _loop;
    private bool _loopReferenced;
    private void* _events;
    private spa_hook* _hook;
    private GCHandle _self;

    internal PipeWireImplMetadataHandle(pw_impl_metadata* metadata, PipeWireLoopHandle loop)
        : base((IntPtr)metadata, ownsHandle: true)
    {
        ArgumentNullException.ThrowIfNull(loop);

        _loop = loop;
        loop.DangerousAddRef(ref _loopReferenced);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    internal pw_impl_metadata* Metadata => (pw_impl_metadata*)handle;

    /// <summary>
    /// Hands the handle the listener memory, freed once the implementation has been destroyed.
    /// </summary>
    internal void OwnListener(void* events, spa_hook* hook, GCHandle self)
    {
        _events = events;
        _hook = hook;
        _self = self;
    }

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

    /// <inheritdoc/>
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
        var metadata = (pw_impl_metadata*)handle;
        handle = IntPtr.Zero;

        try
        {
            // Under the loop lock: destroying unregisters the global, which the loop thread is
            // otherwise free to be dispatching against.
            if (metadata is not null && _loopReferenced && !_loop.IsInvalid)
            {
                pw_thread_loop* loop = _loop.Loop;
                Native.pw_thread_loop_lock(loop);
                try
                {
                    Native.pw_impl_metadata_destroy(metadata);
                }
                finally
                {
                    Native.pw_thread_loop_unlock(loop);
                }
            }
        }
        finally
        {
            if (_loopReferenced)
            {
                _loop.DangerousRelease();
                _loopReferenced = false;
            }

            // After the destroy, never before.
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
