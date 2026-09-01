using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Generated;

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
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class PipeWireProxyHandle : SafeHandle
{
    private readonly PipeWireLoopHandle _loop;
    private bool _loopReferenced;

    internal PipeWireProxyHandle(pw_proxy* proxy, PipeWireLoopHandle loop)
        : base((IntPtr)proxy, ownsHandle: true)
    {
        ArgumentNullException.ThrowIfNull(loop);
        _loop = loop;
        loop.DangerousAddRef(ref _loopReferenced);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    internal pw_proxy* Proxy => (pw_proxy*)handle;

    protected override bool ReleaseHandle()
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
            if (_loopReferenced)
            {
                _loop.DangerousRelease();
                _loopReferenced = false;
            }
        }
        return true;
    }
}
