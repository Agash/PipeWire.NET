using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET;

/// <summary>
/// Manages the PipeWire thread-loop, context, and daemon connection.
/// </summary>
/// <remarks>
/// <para>
/// Backed by <c>pw_thread_loop</c>, which runs the event loop on its own thread and
/// provides a recursive lock. ALL PipeWire object operations (creating streams,
/// connecting, disconnecting, destroying) must happen under that lock - take it with
/// <see cref="Lock"/> from any thread. Stream callbacks are invoked by the loop thread
/// with the lock already held, so they must not re-lock.
/// </para>
/// <para>
/// One <see cref="PipeWireContext"/> is typically shared by all streams in the process.
/// Dispose it only after all dependent streams are disposed.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PipeWireContext : IDisposable, IAsyncDisposable
{
    // Serialises disposal against TryLock, so a teardown path cannot observe a live context and
    // then find it disposed by the time it takes the loop lock.
    private readonly Lock          _disposeGate = new();

    // Fired when the context is disposed, so anything waiting on the daemon can be released. A
    // round-trip has no other way to end: its completion arrives as a "done" event on a loop that
    // disposal is about to stop, so a caller waiting on one with no cancellation of its own would
    // otherwise wait for a reply that can never come.
    private readonly CancellationTokenSource _shutdown = new();
    private PipeWireLoopHandle?    _loopHandle;
    private PipeWireContextHandle? _contextHandle;
    private PipeWireCoreHandle?   _coreHandle;
    private volatile bool          _started;
    private volatile bool          _disposed;

    /// <summary>
    /// The logger factory streams created against this context use for diagnostics. Defaults to
    /// <see cref="NullLoggerFactory"/> (no output) when none is supplied. A host that wants PipeWire
    /// stream tracing on its own <c>--verbose</c>/Debug switch passes its application factory here.
    /// </summary>
    internal ILoggerFactory LoggerFactory { get; }

    /// <summary>Initializes PipeWire and creates the thread-loop + context (not yet started).</summary>
    /// <param name="name">Context name advertised to the daemon and used as the loop-thread name.</param>
    /// <param name="loggerFactory">
    /// Optional factory for stream diagnostics. Pass the host's factory to surface PipeWire stream
    /// state transitions, format/buffer negotiation, and errors at Debug/Trace level; omit for none.
    /// </param>
    public PipeWireContext(string name = "PipeWire.NET", ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        InitializeNative(name);
    }

    private unsafe void InitializeNative(string name)
    {
        Native.pw_init(null, null);

        pw_thread_loop* loop;
        ReadOnlySpan<byte> nameUtf8 = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* n = nameUtf8)
            loop = Native.pw_thread_loop_new((sbyte*)n, null);

        if (loop is null)
            throw new InvalidOperationException("pw_thread_loop_new failed.");

        _loopHandle = new PipeWireLoopHandle(loop);

        pw_context* context = Native.pw_context_new(
            Native.pw_thread_loop_get_loop(loop),
            props: null,
            user_data_size: 0);

        if (context is null)
        {
            _loopHandle.Dispose();
            _loopHandle = null;
            throw new InvalidOperationException("pw_context_new failed.");
        }

        _contextHandle = new PipeWireContextHandle(context, _loopHandle);
    }

    /// <summary>
    /// Starts the loop thread and connects to the daemon. Idempotent.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the loop fails to start or the daemon connection fails (daemon not running).
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // Under the same gate as disposal, and for a sharper reason than idempotence. Two threads
        // both seeing _started false would both call pw_thread_loop_start; the second fails because
        // the loop is already running, and its error path then stops the loop the first is in the
        // middle of connecting on.
        lock (_disposeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return Task.CompletedTask;

            StartNative();
            _started = true;
        }

        return Task.CompletedTask;
    }

    private unsafe void StartNative()
    {
        pw_thread_loop* loop = LoopHandle;
        if (Native.pw_thread_loop_start(loop) < 0)
            throw new InvalidOperationException("pw_thread_loop_start failed.");

        // The loop thread is live from here, but _started is only set once this returns and disposal
        // gates pw_thread_loop_stop on it - so a throw below would strand the thread.
        try
        {
            pw_core* core;

            // Connecting touches the loop's objects -> must hold the loop lock.
            Native.pw_thread_loop_lock(loop);
            try
            {
                core = Native.pw_context_connect(_contextHandle!.Context, properties: null, user_data_size: 0);
            }
            finally
            {
                Native.pw_thread_loop_unlock(loop);
            }

            if (core is null)
                throw new InvalidOperationException(
                    "pw_context_connect failed. Ensure the PipeWire daemon is running " +
                    "(pipewire.service / wireplumber.service).");

            _coreHandle = new PipeWireCoreHandle(core, _loopHandle!, _contextHandle!);
        }
        catch
        {
            Native.pw_thread_loop_stop(loop);
            throw;
        }
    }

    /// <summary>
    /// Acquires the thread-loop lock. Dispose the returned scope to release it.
    /// Hold this around every PipeWire object operation invoked from outside a callback.
    /// </summary>
    public LoopLock Lock()
    {
        // Through the same gate as TryLock rather than a bare check. Testing _disposed and then
        // taking the lock is two steps, and disposal between them leaves the loop handle already
        // null - which surfaced as a NullReferenceException out of what should have been a clean
        // ObjectDisposedException.
        if (!TryLock(out LoopLock scope))
            throw new ObjectDisposedException(nameof(PipeWireContext));

        return scope;
    }

    /// <summary>
    /// Takes the loop lock if the context is still alive, without throwing if it is not.
    /// </summary>
    /// <param name="scope">The held lock, valid only when this returns <see langword="true"/>.</param>
    /// <remarks>
    /// For teardown paths, where <see cref="Lock"/> cannot be used safely. Checking
    /// <see cref="IsDisposed"/> and then calling <see cref="Lock"/> is not the same thing: another
    /// thread can dispose the context between the two, and the lock then throws out of a
    /// <c>Dispose</c> that has no business throwing. Deciding under the same lock that guards
    /// disposal is what closes that window.
    /// </remarks>
    public unsafe bool TryLock(out LoopLock scope)
    {
        scope = default;

        // The loop lock first, the gate second, and never the other way round. Callbacks run on the
        // loop thread with the loop lock already held, and a callback is allowed to call back in -
        // so a thread holding the gate while waiting for the loop lock would deadlock against a
        // callback waiting for the gate.
        PipeWireLoopHandle? loop = _loopHandle;
        if (loop is null) return false;

        // A reference, not a validity check. Testing the handle and then reading its pointer is two
        // steps, and disposal between them hands pw_thread_loop_lock a null pointer - which is a
        // segmentation fault, not an exception. The reference is what makes the loop outlive the
        // lock that is about to be taken on it, and it is released when the scope closes.
        bool referenced = false;
        try
        {
            loop.DangerousAddRef(ref referenced);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        if (!referenced) return false;

        var candidate = new LoopLock(loop);

        lock (_disposeGate)
        {
            if (!_disposed)
            {
                scope = candidate;
                return true;
            }
        }

        // Disposal won the race and teardown is about to run, so the lock is handed straight back.
        candidate.Dispose();
        return false;
    }

    /// <summary>Cancelled when the context is disposed.</summary>
    internal CancellationToken Shutdown => _shutdown.Token;

    /// <summary>
    /// True once the context has been disposed and its loop destroyed.
    /// </summary>
    /// <remarks>
    /// Exists for teardown paths. A caller disposing in the wrong order - the context before
    /// something built on it - would otherwise have its own disposal throw, and there is nothing
    /// useful for it to do about that: the loop is gone, so the native objects it wanted to destroy
    /// went with it.
    /// </remarks>
    public bool IsDisposed => _disposed;

    internal unsafe pw_core* CoreHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _coreHandle is null ? null : _coreHandle.Core;
        }
    }

    /// <summary>The connection, for objects that must keep it alive for their own lifetime.</summary>
    internal PipeWireCoreHandle? CoreOwner => _coreHandle;

    internal unsafe pw_thread_loop* LoopHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _loopHandle is null ? null : _loopHandle.Loop;
        }
    }

    /// <summary>The loop's owning handle, for objects whose lifetime must not outlive it.</summary>
    internal PipeWireLoopHandle LoopOwner =>
        _loopHandle ?? throw new InvalidOperationException("The context has no loop.");

    /// <inheritdoc/>
    /// <summary>Tears down synchronously. Disposal here does no I/O.</summary>
    /// <remarks>
    /// Offered alongside the async form because nothing about this disposal is asynchronous -
    /// the async method completes synchronously - so a consumer should not be forced to write
    /// "await using" for it.
    /// </remarks>
    public void Dispose() => DisposeCore();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private void DisposeCore()
    {
        lock (_disposeGate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Released before the loop is torn down, and outside the gate: the continuations run here,
        // and one of them taking the gate would deadlock against this method still holding it.
        _shutdown.Cancel();

        DisposeNative();
        _shutdown.Dispose();
    }

    private unsafe void DisposeNative()
    {
        // Stopping the loop joins its thread, so no callback can be in flight afterwards.
        if (_loopHandle is not null && _started)
            Native.pw_thread_loop_stop(_loopHandle.Loop);

        // Releases the connection only once every proxy holding it has gone. Disconnecting while
        // proxies are still registered makes PipeWire log "leaked proxy" and abandon each one.
        _coreHandle?.Dispose();
        _coreHandle = null;

        // Releases only once the core - and through it every proxy - has gone.
        _contextHandle?.Dispose();
        _contextHandle = null;
        // Releases the loop only once every proxy handle referencing it has gone.
        _loopHandle?.Dispose();
        _loopHandle = null;
    }

    /// <summary>
    /// RAII scope holding the PipeWire thread-loop lock. Created by <see cref="Lock"/>.
    /// </summary>
    public readonly unsafe ref struct LoopLock
    {
        private readonly PipeWireLoopHandle? _handle;
        private readonly pw_thread_loop* _loop;

        internal LoopLock(PipeWireLoopHandle handle)
        {
            // Holds the reference its caller took, and the raw pointer beside it. Releasing reads
            // neither the context nor the handle's field, both of which disposal may already have
            // cleared - only the pointer captured here, which the reference keeps valid.
            _handle = handle;
            _loop = handle.Loop;
            Native.pw_thread_loop_lock(_loop);
        }

        /// <summary>Releases the loop lock, and the reference taken on the loop.</summary>
        public void Dispose()
        {
            if (_loop is null) return;

            Native.pw_thread_loop_unlock(_loop);
            _handle?.DangerousRelease();
        }
    }
}
