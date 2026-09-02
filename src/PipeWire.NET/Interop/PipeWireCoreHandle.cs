using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Interop;

/// <summary>
/// Owns the <c>pw_core</c> connection.
/// </summary>
/// <remarks>
/// <para>
/// Sits between the loop and the proxies in the ownership chain, because that is the real native
/// dependency: a proxy belongs to a core, and a core runs on a loop.
/// </para>
/// <para>
/// Disconnecting a core walks every proxy still registered against it, logs
/// <c>leaked proxy</c> and abandons it - the memory is never freed and the server-side object is
/// never properly destroyed. It is not a use-after-free, because <c>destroy_proxy</c> sets
/// <c>p-&gt;core = NULL</c> and every dereference guards on it, but it does leak. Holding a
/// reference from each proxy defers the disconnect until they have all gone, so each is destroyed
/// through its own handle first and the core has nothing left to abandon.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class PipeWireCoreHandle : SafeHandle
{
    private readonly PipeWireLoopHandle _loop;
    private readonly PipeWireContextHandle _context;
    private bool _loopReferenced;
    private bool _contextReferenced;

    internal PipeWireCoreHandle(pw_core* core, PipeWireLoopHandle loop, PipeWireContextHandle context)
        : base((IntPtr)core, ownsHandle: true)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(context);

        // pw_context_destroy disconnects every core it owns, so the context has to outlive us.
        // Rolled back explicitly: if the second reference throws, the first is already taken and
        // the constructor never completes, so nothing would ever release it.
        _loop = loop;
        _context = context;
        try
        {
            loop.DangerousAddRef(ref _loopReferenced);
            context.DangerousAddRef(ref _contextReferenced);
        }
        catch
        {
            if (_contextReferenced) { context.DangerousRelease(); _contextReferenced = false; }
            if (_loopReferenced) { loop.DangerousRelease(); _loopReferenced = false; }
            throw;
        }
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>The underlying connection.</summary>
    internal pw_core* Core => (pw_core*)handle;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        var core = (pw_core*)handle;
        handle = IntPtr.Zero;

        try
        {
            // Disconnecting talks to the daemon and touches loop-owned state, so it runs under the
            // loop lock like every other core operation.
            if (core is not null && _loopReferenced && !_loop.IsInvalid)
            {
                pw_thread_loop* loop = _loop.Loop;
                Native.pw_thread_loop_lock(loop);
                try
                {
                    Native.pw_core_disconnect(core);
                }
                finally
                {
                    Native.pw_thread_loop_unlock(loop);
                }
            }
        }
        finally
        {
            if (_contextReferenced)
            {
                _context.DangerousRelease();
                _contextReferenced = false;
            }
            if (_loopReferenced)
            {
                _loop.DangerousRelease();
                _loopReferenced = false;
            }
        }
        return true;
    }
}
