using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Generated;

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

    protected override bool ReleaseHandle()
    {
        var loop = (pw_thread_loop*)handle;
        handle = IntPtr.Zero;
        if (loop is null) return true;

        Native.pw_thread_loop_stop(loop);
        Native.pw_thread_loop_destroy(loop);
        return true;
    }
}
