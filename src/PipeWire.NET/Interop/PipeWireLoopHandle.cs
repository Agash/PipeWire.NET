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

    // Set by deterministic disposal before the base runs. Stopping joins the loop thread, so a
    // finalizer doing it stalls every finalizer behind one abandoned loop. Only a deterministic
    // release stops inline; an abandoned loop goes to the reaper. Dispose the context explicitly
    // and neither path runs.
    private bool _deterministic;
    private bool _enqueued;
    private pw_thread_loop* _reapLoop;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _deterministic = true;
        base.Dispose(disposing);
    }

    /// <remarks>
    /// Stopping joins the loop thread, so a finalizer doing it inline stalls every finalizer
    /// behind one abandoned loop. Only a deterministic release stops inline; an abandoned loop
    /// goes to the reaper, which joins off-thread. Dispose the context explicitly and neither
    /// path runs.
    /// </remarks>
    protected override bool ReleaseHandle()
    {
        var loop = (pw_thread_loop*)handle;
        handle = IntPtr.Zero;
        if (loop is null) return true;

        if (!_deterministic && !_enqueued)
        {
            _enqueued = true;
            _reapLoop = loop;
            NativeReaper.EnqueueLoop(this, (nint)loop, ReleaseReaped);
            return true;
        }

        ReleaseLoop(loop);
        return true;
    }

    private bool ReleaseReaped()
    {
        // The reaper only hands this over once nothing bound to the loop is still queued; the
        // guards inside ReleaseLoop stay load-bearing for a loop that lost the finalizer race.
        ReleaseLoop(_reapLoop);
        return true;
    }

    private void ReleaseLoop(pw_thread_loop* loop)
    {
        // Stopping the loop from inside it is the thread waiting for itself, which hangs with
        // nothing in the log to say why. There is no version of this that works, so the loop is
        // deliberately left running and leaked: a leaked thread is recoverable and a hung process
        // is not. PipeWireContext refuses disposal on this thread for the same reason, which is why
        // reaching here means something bypassed it.
        if (Native.pw_thread_loop_in_thread(loop)) return;

        Native.pw_thread_loop_stop(loop);
        Native.pw_thread_loop_destroy(loop);
    }
}
