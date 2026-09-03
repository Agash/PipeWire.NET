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

    // 0 until disposal is claimed. Read from TryUse on any thread, so volatile; claimed with an
    // interlocked exchange, so two concurrent disposals cannot both proceed to schedule a teardown.
    private volatile int _disposed;

    /// <summary>The deferred teardown, when disposal was asked for on the loop thread.</summary>
    /// <remarks>
    /// Tracked rather than abandoned. The destroy has to happen off the loop thread, but a caller
    /// that disposes a control and then disposes the context needs the first to have finished
    /// before the second tears the loop down.
    /// </remarks>
    private Task? _deferredTeardown;

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
        if (_disposed != 0 || proxy is null) return false;

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
    internal bool IsDisposed => _disposed != 0;

    /// <summary>Waits for a teardown that was deferred off the loop thread.</summary>
    /// <remarks>
    /// For teardown paths that have to know the proxy is really gone before releasing what it was
    /// built on. Returns immediately when disposal ran inline, which is the usual case.
    /// </remarks>
    internal Task DrainAsync() => _deferredTeardown ?? Task.CompletedTask;

    /// <summary>
    /// Binds a global by id and attaches a listener to it.
    /// </summary>
    /// <param name="ctx">The context whose core the object belongs to.</param>
    /// <param name="registry">The registry to bind through.</param>
    /// <param name="id">The global id to bind.</param>
    /// <param name="interfaceType">The interface to bind as, such as <c>PipeWire:Interface:Node</c>.</param>
    /// <param name="version">
    /// The version the daemon announced for this global. Clamped to
    /// <paramref name="maxVersion"/>, because the daemon writes into the events table at the
    /// version that was asked for: binding a newer one than this build's table was sized from lets
    /// it write past the end of it.
    /// </param>
    /// <param name="maxVersion">The highest version this build's events table can accept.</param>
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
        uint maxVersion,
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
                // pw_registry_bind forwards the version to the daemon unchanged, so the clamp has
                // to happen on this side. A daemon older than this build gets its own version and
                // the extra callbacks stay unused; a newer one is held to what this build knows.
                uint bindVersion = Math.Min(version, maxVersion);

                pw_proxy* proxy;
                ReadOnlySpan<byte> typeUtf8 = Encoding.UTF8.GetBytes(interfaceType + '\0');
                fixed (byte* t = typeUtf8)
                    proxy = Native.pw_registry_bind(registry, id, (sbyte*)t, bindVersion, 0);

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
                // Weak, not Normal. The handle is freed by the proxy handle's release path, the
                // proxy handle is reached through the object the handle points at, and a strongly
                // rooted object is never finalized: the release that frees the handle would be
                // waiting on the handle being freed. A caller who drops a control without disposing
                // it would leak the control, its proxy and its listener for the life of the
                // process, with nothing in a descriptor or daemon-side count to show for it.
                // Every callback already resolves Target and returns when it is null, which is the
                // window between the object going away and the finalizer destroying the proxy.
                GCHandle self = GCHandle.Alloc(owner, GCHandleType.Weak);

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
        // Claimed atomically. A check followed by a set lets two concurrent disposals both pass and
        // schedule a teardown each.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Deferred when called from the loop thread. The proxy itself survives being destroyed
        // during its own dispatch - the protocol module holds a reference across the demarshal
        // (module-protocol-native.c:1078-1082) and skips zombie proxies afterwards - but the
        // listener memory does not: destroying the proxy is what detaches the hook, and freeing the
        // events table while spa_hook_list_call is still walking it is a use-after-free. Handing
        // the teardown to another thread lets the dispatch finish first.
        if (_ctx.IsOnLoopThread)
        {
            BoundProxy self = this;
            _deferredTeardown = Task.Run(self.DisposeCore);
            return;
        }

        DisposeCore();
    }

    private void DisposeCore()
    {
        // The listener's memory goes with the handle, not with this: disposal only destroys the
        // proxy once the last in-flight call has released it, and freeing the events table any
        // earlier would leave the daemon dispatching into it. The handle holds the core and the
        // loop open for exactly as long as that takes.
        _proxy?.Dispose();
        _proxy = null;
    }
}
