using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Interop;

/// <summary>
/// Owns a <c>pw_thread_loop</c>. Every proxy handle takes a reference on this one, so the loop
/// cannot be released while an object that needs its lock is still alive.
/// </summary>
/// <remarks>
/// For that guarantee to hold, this must be the <em>only</em> path that destroys the loop.
/// <see cref="PipeWireContext"/> therefore hands ownership over rather than calling
/// <c>pw_thread_loop_destroy</c> itself.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class PipeWireLoopHandle : SafeHandle
{
    internal PipeWireLoopHandle(pw_thread_loop* loop) : base((IntPtr)loop, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    internal pw_thread_loop* Loop => (pw_thread_loop*)handle;

    /// <remarks>
    /// Runs on the finalizer thread when a caller drops the last reference without disposing, and
    /// blocks there: stopping the loop joins its thread. That is the price of releasing the loop at
    /// all, and the alternative - leaving it running - leaks a thread rather than a stall, so the
    /// join stays. Dispose the context explicitly and this never runs.
    /// </remarks>
    protected override bool ReleaseHandle()
    {
        var loop = (pw_thread_loop*)handle;
        handle = IntPtr.Zero;
        if (loop is null) return true;

        // Stopping the loop from inside it is the thread waiting for itself, which hangs with
        // nothing in the log to say why. There is no version of this that works, so the loop is
        // deliberately left running and leaked: a leaked thread is recoverable and a hung process
        // is not. PipeWireContext refuses disposal on this thread for the same reason, which is why
        // reaching here means something bypassed it.
        if (Native.pw_thread_loop_in_thread(loop)) return true;

        Native.pw_thread_loop_stop(loop);
        Native.pw_thread_loop_destroy(loop);
        return true;
    }
}
