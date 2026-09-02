using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Interop;

/// <summary>
/// Owns the <c>pw_context</c>.
/// </summary>
/// <remarks>
/// <para>
/// The last link in the ownership chain: proxy depends on core, core depends on context, context
/// depends on loop. It has to be a link because <c>pw_context_destroy</c> does
/// <c>spa_list_consume(core, &amp;context-&gt;core_list) pw_core_disconnect(core)</c> - it
/// disconnects every core it owns whatever anything else believes about their lifetime. Deferring
/// the core alone therefore achieves nothing while the context is still torn down immediately after.
/// </para>
/// <para>
/// This is not about safety. Disconnecting with proxies outstanding is memory-safe, because
/// <c>destroy_proxy</c> nulls each <c>p-&gt;core</c> and every dereference guards on it. It is about
/// each proxy getting destroyed properly instead of being abandoned with a <c>leaked proxy</c>
/// warning and never freed.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class PipeWireContextHandle : SafeHandle
{
    private readonly PipeWireLoopHandle _loop;
    private bool _loopReferenced;

    internal PipeWireContextHandle(pw_context* context, PipeWireLoopHandle loop)
        : base((IntPtr)context, ownsHandle: true)
    {
        ArgumentNullException.ThrowIfNull(loop);
        _loop = loop;
        loop.DangerousAddRef(ref _loopReferenced);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>The underlying context.</summary>
    internal pw_context* Context => (pw_context*)handle;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        var context = (pw_context*)handle;
        handle = IntPtr.Zero;

        try
        {
            if (context is not null && _loopReferenced && !_loop.IsInvalid)
            {
                pw_thread_loop* loop = _loop.Loop;
                Native.pw_thread_loop_lock(loop);
                try
                {
                    Native.pw_context_destroy(context);
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
