using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Interop;

/// <summary>
/// Owns a <c>pw_stream</c>.
/// </summary>
/// <remarks>
/// A stream sits in the same place as a proxy in the ownership chain: it needs the loop to take a
/// lock while it is torn down, and its core to still be connected for the disconnect to reach the
/// daemon. Holding both means disposal works whichever order the caller unwinds in, rather than
/// throwing out of its own <c>DisposeAsync</c> when the context happened to go first.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class PipeWireStreamHandle : SafeHandle
{
    private readonly PipeWireLoopHandle _loop;
    private readonly PipeWireCoreHandle? _core;
    private bool _loopReferenced;
    private bool _coreReferenced;
    private void* _events;
    private spa_hook* _hook;
    private GCHandle _self;

    internal PipeWireStreamHandle(pw_stream* stream, PipeWireLoopHandle loop, PipeWireCoreHandle? core)
        : base((IntPtr)stream, ownsHandle: true)
    {
        ArgumentNullException.ThrowIfNull(loop);

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
    /// Hands the handle the listener memory, freed once the native object has been destroyed.
    /// </summary>
    /// <remarks>
    /// Destruction is deferred while anyone holds a reference, so freeing this from the wrapper
    /// leaves a live listener pointing into freed memory.
    /// </remarks>
    internal void OwnListener(void* events, spa_hook* hook, GCHandle self)
    {
        _events = events;
        _hook = hook;
        _self = self;
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    internal pw_stream* Stream => (pw_stream*)handle;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        var stream = (pw_stream*)handle;
        handle = IntPtr.Zero;

        try
        {
            // Under the loop lock, so a callback cannot be in flight against the stream being
            // destroyed.
            if (stream is not null && _loopReferenced && !_loop.IsInvalid)
            {
                pw_thread_loop* loop = _loop.Loop;
                Native.pw_thread_loop_lock(loop);
                try
                {
                    Native.pw_stream_disconnect(stream);
                    Native.pw_stream_destroy(stream);
                }
                finally
                {
                    Native.pw_thread_loop_unlock(loop);
                }
            }
        }
        finally
        {
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
            // After the destroy, never before: destroying the object is what detaches the listener.
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
