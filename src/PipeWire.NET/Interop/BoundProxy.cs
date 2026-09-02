using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PipeWire.NET.Interop;

/// <summary>
/// A proxy to one existing global, with a listener attached to it.
/// </summary>
/// <remarks>
/// <para>
/// The registry says what objects exist; a bound proxy is what makes one of them addressable. Every
/// interface beyond the registry itself - a node's parameters, a device's routes, a metadata store's
/// entries - is reached this way.
/// </para>
/// <para>
/// Owns three things that have to be released in order: the listener memory the daemon writes
/// through, the events table it dispatches from, and the proxy itself. The proxy goes first, because
/// destroying it is what stops the daemon calling into memory that is about to be freed.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class BoundProxy : IDisposable
{
    private readonly PipeWireContext _ctx;
    private PipeWireProxyHandle? _proxy;
    private bool _disposed;

    private BoundProxy(PipeWireContext ctx) => _ctx = ctx;

    /// <summary>The proxy, for dispatching interface methods through.</summary>
    /// <remarks>
    /// Only safe to read while a <see cref="Use"/> scope is held. Destroying a proxy clears this
    /// pointer before it takes the loop lock, so holding the lock is not on its own enough to stop
    /// it becoming null between a check and the call that follows.
    /// </remarks>
    internal void* Object => _proxy is null || _proxy.IsInvalid ? null : _proxy.Proxy;

    /// <summary>
    /// Takes a reference on the proxy so it cannot be destroyed while a native call is in flight.
    /// </summary>
    /// <param name="scope">The held reference; valid only when this returns <see langword="true"/>.</param>
    internal bool TryUse(out Use scope)
    {
        scope = default;

        PipeWireProxyHandle? proxy = _proxy;
        if (_disposed || proxy is null) return false;

        bool referenced = false;
        try
        {
            proxy.DangerousAddRef(ref referenced);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        if (!referenced) return false;
        if (proxy.IsInvalid)
        {
            proxy.DangerousRelease();
            return false;
        }

        scope = new Use(proxy);
        return true;
    }

    /// <summary>A held reference to the proxy, for the duration of one native call.</summary>
    internal readonly ref struct Use
    {
        private readonly PipeWireProxyHandle? _proxy;

        internal Use(PipeWireProxyHandle proxy)
        {
            _proxy = proxy;
            Object = proxy.Proxy;
        }

        /// <summary>The proxy pointer, valid for as long as this scope is held.</summary>
        internal void* Object { get; }

        /// <summary>Releases the reference.</summary>
        public void Dispose() => _proxy?.DangerousRelease();
    }

    /// <summary>True once the proxy has been destroyed.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>
    /// Binds a global by id and attaches a listener to it.
    /// </summary>
    /// <param name="ctx">The context whose core the object belongs to.</param>
    /// <param name="registry">The registry to bind through.</param>
    /// <param name="id">The global id to bind.</param>
    /// <param name="interfaceType">The interface to bind as, such as <c>PipeWire:Interface:Node</c>.</param>
    /// <param name="version">The interface version to ask for.</param>
    /// <param name="eventsSize">Size of the interface's events table.</param>
    /// <param name="fillEvents">Writes the callback pointers into the zeroed events table.</param>
    /// <param name="addListener">Dispatches the interface's <c>add_listener</c> method.</param>
    /// <param name="owner">Passed to each callback as its user data, kept alive for the binding.</param>
    /// <exception cref="InvalidOperationException">The daemon refused the bind or the listener.</exception>
    internal static BoundProxy Bind(
        PipeWireContext ctx,
        pw_registry* registry,
        uint id,
        string interfaceType,
        uint version,
        int eventsSize,
        Action<IntPtr> fillEvents,
        Func<IntPtr, IntPtr, IntPtr, IntPtr, int> addListener,
        object owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(fillEvents);
        ArgumentNullException.ThrowIfNull(addListener);
        ArgumentNullException.ThrowIfNull(owner);

        var bound = new BoundProxy(ctx);
        try
        {
            // Binding touches the core's object map, so it runs under the loop lock like every
            // other call into a proxy.
            using (ctx.Lock())
            {
                pw_proxy* proxy;
                ReadOnlySpan<byte> typeUtf8 = Encoding.UTF8.GetBytes(interfaceType + '\0');
                fixed (byte* t = typeUtf8)
                    proxy = Native.pw_registry_bind(registry, id, (sbyte*)t, version, 0);

                if (proxy is null)
                {
                    throw new InvalidOperationException(
                        $"the daemon refused to bind global {id} as {interfaceType}.");
                }

                var handle = new PipeWireProxyHandle(proxy, ctx.LoopOwner, ctx.CoreOwner);
                bound._proxy = handle;

                void* events = NativeMemory.AllocZeroed((nuint)eventsSize);
                fillEvents((IntPtr)events);

                var hook = (spa_hook*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook));
                GCHandle self = GCHandle.Alloc(owner, GCHandleType.Normal);

                // Handed over before the listener is attached, so a failure below still frees it.
                handle.OwnListener(events, hook, self);

                int rc = addListener(
                    (IntPtr)proxy, (IntPtr)hook, (IntPtr)events, GCHandle.ToIntPtr(self));

                if (rc < 0)
                    throw new PipeWireException("add_listener", rc);
            }

            return bound;
        }
        catch
        {
            bound.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The listener's memory goes with the handle, not with this: disposal only destroys the
        // proxy once the last in-flight call has released it, and freeing the events table any
        // earlier would leave the daemon dispatching into it. The handle holds the core and the
        // loop open for exactly as long as that takes.
        _proxy?.Dispose();
        _proxy = null;
    }
}
