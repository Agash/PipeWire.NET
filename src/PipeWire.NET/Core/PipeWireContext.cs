using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Generated;

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
public sealed class PipeWireContext : IAsyncDisposable
{
    private PipeWireLoopHandle?    _loopHandle;
    private unsafe pw_context*     _context;
    private unsafe pw_core*        _core;
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

        _context = Native.pw_context_new(
            Native.pw_thread_loop_get_loop(loop),
            props: null,
            user_data_size: 0);

        if (_context is null)
        {
            _loopHandle.Dispose();
            _loopHandle = null;
            throw new InvalidOperationException("pw_context_new failed.");
        }
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
        if (_started) return Task.CompletedTask;

        StartNative();
        _started = true;
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
            // Connecting touches the loop's objects -> must hold the loop lock.
            Native.pw_thread_loop_lock(loop);
            try
            {
                _core = Native.pw_context_connect(_context, properties: null, user_data_size: 0);
            }
            finally
            {
                Native.pw_thread_loop_unlock(loop);
            }

            if (_core is null)
                throw new InvalidOperationException(
                    "pw_context_connect failed. Ensure the PipeWire daemon is running " +
                    "(pipewire.service / wireplumber.service).");
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new LoopLock(this);
    }

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
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _core; }
    }

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

    internal unsafe void LockRaw()   => Native.pw_thread_loop_lock(_loopHandle!.Loop);
    internal unsafe void UnlockRaw() => Native.pw_thread_loop_unlock(_loopHandle!.Loop);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        DisposeNative();
        return ValueTask.CompletedTask;
    }

    private unsafe void DisposeNative()
    {
        // Stopping the loop joins its thread, so no callback can be in flight afterwards.
        if (_loopHandle is not null && _started)
            Native.pw_thread_loop_stop(_loopHandle.Loop);

        if (_core is not null)
        {
            Native.pw_core_disconnect(_core);
            _core = null;
        }
        if (_context is not null)
        {
            Native.pw_context_destroy(_context);
            _context = null;
        }
        // Releases the loop only once every proxy handle referencing it has gone.
        _loopHandle?.Dispose();
        _loopHandle = null;
    }

    /// <summary>
    /// RAII scope holding the PipeWire thread-loop lock. Created by <see cref="Lock"/>.
    /// </summary>
    public readonly ref struct LoopLock
    {
        private readonly PipeWireContext _ctx;

        internal LoopLock(PipeWireContext ctx)
        {
            _ctx = ctx;
            _ctx.LockRaw();
        }

        /// <summary>Releases the loop lock.</summary>
        public void Dispose() => _ctx.UnlockRaw();
    }
}
