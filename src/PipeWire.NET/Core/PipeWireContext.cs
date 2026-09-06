using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
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
    // Lifecycle admission, in one order everywhere: the gate first, the native loop mutex
    // second, never nested the other way. Admission under the gate hands
    // out a lease; disposal marks stopping, releases the gate, waits the leases out, and only
    // then tears the loop down. Teardown never runs while a scope is alive, and a scope's lease
    // keeps teardown waiting, so a scope never outlives the loop it locked.
    //
    // A Lock scope held across Dispose is a contract violation, and the drain is what keeps that
    // violation a hang at the call site rather than an unlock against a destroyed loop. Do not
    // hold a scope across Dispose.
    private enum LifecycleState { Created, Starting, Running, Stopping, Disposed }
    private readonly Lock _lifecycle = new();
    private volatile LifecycleState _state;
    private int _activeLeases;
    private readonly ManualResetEventSlim _startSettled = new(false);
    private readonly ManualResetEventSlim _leasesDrained = new(true);

    // Fired when the context is disposed, so anything waiting on the daemon can be released. A
    // round-trip has no other way to end: its completion arrives as a "done" event on a loop that
    // disposal is about to stop, so a caller waiting on one with no cancellation of its own would
    // otherwise wait for a reply that can never come.
    private readonly CancellationTokenSource _shutdown = new();
    private PipeWireLoopHandle?    _loopHandle;
    private PipeWireContextHandle? _contextHandle;
    private PipeWireCoreHandle?   _coreHandle;
    private readonly string _name;
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
        _name = name;
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _shutdownToken = _shutdown.Token;
        InitializeNative(name);
    }

    /// <summary>Runs <c>pw_init</c> exactly once for the process.</summary>
    /// <remarks>
    /// <para>
    /// <c>pw_init</c> is reference counted and paired with <c>pw_deinit</c>, which at zero unloads
    /// the SPA plugin handles and zeroes the global support struct. Calling it per context without
    /// the matching release leaves the count at the number of contexts ever created.
    /// </para>
    /// <para>
    /// Initialising once and never releasing is the other half of that trade, and the safer half.
    /// The alternative is a <c>pw_deinit</c> on the last dispose, which would tear the plugin
    /// registry down under any handle whose finalizer has not run yet, and under any other library
    /// in the process that is also using PipeWire. What is kept is the plugin registry and the log,
    /// which an application still talking to PipeWire would hold anyway.
    /// </para>
    /// </remarks>
    private static readonly Lazy<bool> ProcessInit = new(
        InitOnce, LazyThreadSafetyMode.ExecutionAndPublication);

    private static unsafe bool InitOnce()
    {
        Native.pw_init(null, null);
        return true;
    }

    private unsafe void InitializeNative(string name)
    {
        _ = ProcessInit.Value;

        pw_thread_loop* loop;
        ReadOnlySpan<byte> nameUtf8 = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* n = nameUtf8)
            loop = Native.pw_thread_loop_new((sbyte*)n, null);

        if (loop is null)
            throw new PipeWireException("pw_thread_loop_new", -12);     // ENOMEM

        _loopHandle = new PipeWireLoopHandle(loop);

        pw_context* context = Native.pw_context_new(
            Native.pw_thread_loop_get_loop(loop),
            props: null,
            user_data_size: 0);

        if (context is null)
        {
            _loopHandle.Dispose();
            _loopHandle = null;
            throw new PipeWireException("pw_context_new", -12);         // ENOMEM
        }

        _contextHandle = new PipeWireContextHandle(context, _loopHandle);
    }

    // Under _lifecycle. Hands a lease to an operation entering, or refuses once stopping began.
    // The count itself is interlocked: releases run outside the gate, so a plain increment can
    // lose an update against a concurrent decrement and wedge the drain on a count that never
    // reaches zero.
    private bool Admit()
    {
        if (_state is LifecycleState.Stopping or LifecycleState.Disposed) return false;
        Interlocked.Increment(ref _activeLeases);
        _leasesDrained.Reset();
        return true;
    }

    private void ReleaseLease()
    {
        if (Interlocked.Decrement(ref _activeLeases) == 0)
            _leasesDrained.Set();
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

        return StartAsyncCore(handle: null, rawFd: -1, cancellationToken);
    }

    /// <summary>
    /// Starts the loop thread and connects to the daemon over an already-connected socket fd.
    /// </summary>
    /// <param name="fd">
    /// A descriptor already connected to a PipeWire daemon, as handed out by the
    /// xdg-desktop-portal ScreenCast <c>OpenPipeWireRemote</c> request. Borrowed only: this call
    /// duplicates it, and the caller's handle stays fully usable afterwards.
    /// </param>
    /// <param name="cancellationToken">Cancellation of the wait for another start already in flight.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fd"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fd"/> is invalid.</exception>
    /// <exception cref="PipeWireException">
    /// The fd is not a connected PipeWire socket, or the daemon refuses the handshake on it.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The descriptor is borrowed, not taken. Before the connection is attempted the library
    /// duplicates it (with the close-on-exec flag, see <see cref="FdInterop.DuplicateWithCloseOnExec"/>)
    /// and it is the duplicate that <c>pw_context_connect_fd</c> takes ownership of. Exactly one
    /// owner exists from that point on: the library closes the duplicate when the start fails,
    /// PipeWire closes it when a connected session is torn down - and <paramref name="fd"/> is
    /// never touched by any of those paths.
    /// </para>
    /// <para>
    /// The fd form is admission-controlled by the same single-starter gate as
    /// <see cref="StartAsync(CancellationToken)"/>, and a failed attempt returns the context to a
    /// startable state.
    /// </para>
    /// </remarks>
    public Task StartAsync(SafeFileHandle fd, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(fd);
        if (fd.IsInvalid)
            throw new ArgumentException("the handle does not carry a valid descriptor", nameof(fd));

        return StartAsyncCore(fd, rawFd: -1, cancellationToken);
    }

    /// <summary>
    /// Starts the loop thread and connects to the daemon over an already-connected socket fd
    /// handed over as a raw descriptor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Low-level counterpart of <see cref="StartAsync(SafeFileHandle, CancellationToken)"/> for
    /// callers that hold the descriptor itself and manage its lifetime explicitly. Ownership
    /// transfers on success: from a successful connect on, PipeWire owns the descriptor and
    /// closes it when the connection is torn down. On a failed start the descriptor is left
    /// open - closing it stays the caller's decision.
    /// </para>
    /// <para>
    /// This is deliberately not public: callers without an explicit ownership story should pass
    /// a <see cref="SafeFileHandle"/> and let the library borrow instead.
    /// </para>
    /// </remarks>
    /// <param name="fd">
    /// A caller-owned descriptor already connected to a PipeWire daemon. Ownership transfers on
    /// success - PipeWire closes it when the connection is torn down; on a failed start it stays
    /// the caller's. See the remarks.
    /// </param>
    /// <param name="cancellationToken">Cancellation of the wait for another start already in flight.</param>
    /// <exception cref="PipeWireException">The daemon refuses the handshake on the fd.</exception>
    internal Task StartAsync(int fd, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(fd);

        return StartAsyncCore(handle: null, rawFd: fd, cancellationToken);
    }

    /// <summary>
    /// The one shared path through the single-starter gate. The three start forms differ only
    /// in how their native connect produces the core: the daemon socket resolved by PipeWire,
    /// or an fd handed in from outside.
    /// </summary>
    /// <remarks>
    /// The gate/admission/settle/lease logic is copied verbatim from the pre-fd form and must
    /// stay identical across overloads: it is the order the whole context is reasoned about -
    /// admission under the gate, the native loop lock after, never nested the other way.
    /// </remarks>
    private Task StartAsyncCore(SafeFileHandle? handle, int rawFd, CancellationToken cancellationToken)
    {
        // Under the same gate as disposal, and only for the state transition. Two threads both
        // seeing Created would both call pw_thread_loop_start; the second fails because the loop
        // is already running, and its error path then stops the loop the first is connecting on.
        // The native lock is taken after the gate is released, never under it: TryLock admits
        // the same way, so both orders agree and a scope held across this call cannot wedge it.
        bool starter = false;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(
                _disposed || _state is LifecycleState.Stopping or LifecycleState.Disposed, this);
            if (_state == LifecycleState.Running) return Task.CompletedTask;
            if (_state == LifecycleState.Created)
            {
                _state = LifecycleState.Starting;
                Admit();
                starter = true;
            }
        }

        if (!starter)
        {
            // Another thread is connecting. Wait for its attempt to settle without holding the
            // gate: disposal marks stopping and tears down, which this must observe rather than
            // block. The event is one-way, so the state is always re-verified after it fires: a
            // failed attempt falls back to Created, and the woken waiter becomes the starter.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _startSettled.Wait(cancellationToken);
                lock (_lifecycle)
                {
                    if (_state == LifecycleState.Running) return Task.CompletedTask;
                    if (_state is LifecycleState.Stopping or LifecycleState.Disposed)
                        throw new ObjectDisposedException(nameof(PipeWireContext));
                    if (_state == LifecycleState.Created && Admit())
                    {
                        _state = LifecycleState.Starting;
                        starter = true;
                        break;
                    }
                }
            }
        }

        try
        {
            StartNative(handle, rawFd);
        }
        catch
        {
            lock (_lifecycle)
            {
                // Back to Created so a later start can retry: StartNative stops the loop thread
                // it started, so nothing is left running. The settle event wakes any waiter,
                // which re-verifies the state and takes over.
                if (_state == LifecycleState.Starting) _state = LifecycleState.Created;
            }
            _startSettled.Set();
            ReleaseLease();
            throw;
        }

        bool stopping;
        lock (_lifecycle)
        {
            stopping = _state is LifecycleState.Stopping or LifecycleState.Disposed;
            if (!stopping)
            {
                _state = LifecycleState.Running;
                _started = true;
            }
        }
        _startSettled.Set();
        ReleaseLease();

        // Disposal began mid-connect and is waiting on this lease. It proceeds once every
        // starter is out, so report rather than return success for a start that is being torn
        // down underneath.
        if (stopping)
            throw new ObjectDisposedException(nameof(PipeWireContext));

        return Task.CompletedTask;
    }

    /// <param name="handle">A borrowed <see cref="SafeFileHandle"/> to connect over, or null.</param>
    /// <param name="rawFd">A caller-owned raw descriptor to connect over, or -1 when <paramref name="handle"/> is set.</param>
    private unsafe void StartNative(SafeFileHandle? handle, int rawFd)
    {
        pw_thread_loop* loop = LoopHandle;
        if (Native.pw_thread_loop_start(loop) < 0)
            throw new PipeWireException("pw_thread_loop_start", -11);   // EAGAIN

        // The loop thread is live from here, but _started is only set once this returns and disposal
        // gates pw_thread_loop_stop on it - so a throw below would strand the thread.
        //
        // The descriptor the connect runs over, and whether it is a duplicate this method owns.
        // The duplication lives inside this try like every other step - a failure anywhere stops
        // the loop thread it started - and the catch below closes exactly what is still owned,
        // never the caller's own descriptor: a SafeFileHandle is borrowed (read once, duplicated),
        // a raw descriptor is handed over untouched and stays the caller's on every failure path.
        int fd = rawFd;
        bool ownsDuplicate = false;

        // Which native connect this start performs. A raw descriptor also goes through the fd
        // form: pw_context_connect_fd with the caller's number, no duplication on this side.
        bool overFd = handle is not null || rawFd >= 0;
        try
        {
            if (handle is not null)
            {
                // A reference, not a validity check, in the TryLock style: reading the descriptor
                // out of the handle and using it are two steps, and disposal in between would
                // hand fcntl a number that is already closed. The reference pins the handle for
                // exactly the duplication call; the borrow lasts no longer.
                bool referenced = false;
                try
                {
                    handle.DangerousAddRef(ref referenced);
                    fd = FdInterop.DuplicateWithCloseOnExec((int)handle.DangerousGetHandle());
                    ownsDuplicate = true;
                }
                finally
                {
                    if (referenced) handle.DangerousRelease();
                }
            }

            pw_core* core;

            // Connecting touches the loop's objects -> must hold the loop lock.
            Native.pw_thread_loop_lock(loop);
            try
            {
                // Named, so the daemon, session managers and tools see which application this
                // connection belongs to. pw_properties_new_dict copies the dict, and the core
                // takes the properties on success and frees them on failure, so nothing here
                // is freed on either path.
                Span<byte> scratch = stackalloc byte[512];
                Span<spa_dict_item> items = stackalloc spa_dict_item[2];
                var builder = new SpaDictBuilder(scratch, items);
                builder.Add("application.name", _name);
                spa_dict native = builder.Build();

                pw_properties* props = Native.pw_properties_new_dict(&native);
                if (props is null)
                    throw new PipeWireException("pw_properties_new_dict", -12);   // ENOMEM

                core = !overFd
                    ? Native.pw_context_connect(_contextHandle!.Context, props, user_data_size: 0)
                    : Native.pw_context_connect_fd(_contextHandle!.Context, fd, props, user_data_size: 0);
            }
            finally
            {
                Native.pw_thread_loop_unlock(loop);
            }

            if (core is null)
            {
                // pw_context_connect_fd fails before its io source exists, so a duplicated
                // descriptor is still open and this method is its last owner. A raw descriptor
                // stays the caller's; the catch below closes only what ownsDuplicate marks.
                throw !overFd
                    ? new PipeWireException(
                        "pw_context_connect", -2,   // ENOENT
                        objectId: null,
                        "ensure the PipeWire daemon is running (pipewire.service / wireplumber.service)")
                    : new PipeWireException(
                        "pw_context_connect_fd", -2,   // ENOENT
                        objectId: null,
                        "the fd must be a connected PipeWire socket (as returned by a portal "
                        + "OpenPipeWireRemote request), not a plain file, and the daemon must be "
                        + "reachable on it");
            }

            // A successful connect hands the descriptor to PipeWire: pw_context_connect_fd puts
            // it into an io source created with close-on-destroy, so the connection closes it
            // when it is torn down. Ownership leaves this method here - which is why the raw
            // form is for callers who understand that handover, and the borrowed form exists
            // for everyone else.
            ownsDuplicate = false;

            _coreHandle = new PipeWireCoreHandle(core, _loopHandle!, _contextHandle!);
        }
        catch
        {
            // Exactly one owner at every instant: before a successful connect it is this method
            // (for a duplicated descriptor only), after it, the connection. The caller's handle
            // and the raw descriptor are on no close path here.
            if (ownsDuplicate)
                _ = FdInterop.TryClose(fd);

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
        // null.
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

        // Admitting under the gate and locking after it is the one
        // order used everywhere, so a callback holding the native lock and calling back in can
        // never wedge against a thread holding the gate and waiting for that lock. The lease is
        // what keeps disposal's teardown waiting, so the loop cannot be destroyed under a scope
        // admitted here, and the recheck after locking observes a disposal that began in between
        // without taking the gate while the native lock is held.
        PipeWireLoopHandle? loop = _loopHandle;
        if (loop is null) return false;

        // A reference, not a validity check. Testing the handle and then reading its pointer is two
        // steps, and disposal between them hands pw_thread_loop_lock a null pointer - which is a
        // segmentation fault, not an exception. The reference is what makes the loop outlive the
        // lock that is about to be taken on it, and the scope owns it from here: every exit below
        // either hands it to the scope or releases it.
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

        lock (_lifecycle)
        {
            if (!Admit())
            {
                loop.DangerousRelease();
                return false;
            }
        }

        var candidate = new LoopLock(this, loop);
        if (_state is LifecycleState.Stopping or LifecycleState.Disposed)
        {
            // Disposal won the race and its teardown is waiting on this lease, so the lock is
            // handed straight back. The scope's own dispose releases the lease exactly once;
            // a second release here would drive the count negative and corrupt the drain.
            candidate.Dispose();
            return false;
        }

        scope = candidate;
        return true;
    }

    /// <summary>Cancelled when the context is disposed.</summary>
    /// <remarks>
    /// Read from the cached copy. A <see cref="CancellationTokenSource"/> throws
    /// <see cref="ObjectDisposedException"/> from its <c>Token</c> property once disposed, so a
    /// round trip racing teardown would get that instead of the clean cancellation this exists to
    /// deliver. A token outlives its source and stays observable.
    /// </remarks>
    internal CancellationToken Shutdown => _shutdownToken;

    private readonly CancellationToken _shutdownToken;

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

    /// <summary>True when the caller is the loop thread, i.e. inside a callback.</summary>
    internal unsafe bool IsOnLoopThread
    {
        get
        {
            PipeWireLoopHandle? loop = _loopHandle;
            if (loop is null || loop.IsInvalid) return false;

            bool referenced = false;
            try
            {
                loop.DangerousAddRef(ref referenced);
                return referenced && Native.pw_thread_loop_in_thread(loop.Loop);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                if (referenced) loop.DangerousRelease();
            }
        }
    }

    /// <summary>The context, for objects this client implements rather than binds.</summary>
    internal unsafe pw_context* ContextHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _contextHandle is null ? null : _contextHandle.Context;
        }
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
        _loopHandle ?? throw new ObjectDisposedException(nameof(PipeWireContext));

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
        // Checked before anything is marked or released. Stopping the loop joins its thread, so
        // asking for it from inside a callback is the thread waiting for itself: the process stops
        // with nothing in the log and no timeout to break it. There is no way to satisfy the
        // request, and refusing after the disposed flag was set would leave a context that is
        // neither usable nor torn down, so this happens first and changes nothing.
        if (IsOnLoopThread && _started)
        {
            throw new InvalidOperationException(
                "A PipeWire context cannot be disposed from its own loop thread: stopping the loop "
                + "joins that thread, so it would wait for itself. Dispose from the thread that "
                + "created it, or hand the disposal to another thread from the callback.");
        }

        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _state = LifecycleState.Stopping;
        }

        // Wakes any thread waiting on an in-flight start so it observes the stopping state.
        _startSettled.Set();

        // Released before the loop is torn down, and outside the gate: the continuations run here,
        // and one of them taking the gate would deadlock against this method still holding it.
        _shutdown.Cancel();

        // Wait out admitted operations before tearing the loop down. Every in-library scope is
        // method-local and short, a round trip holds no scope while it waits, and the shutdown
        // above releases every waiter - so this ends as soon as in-flight work does.
        while (Volatile.Read(ref _activeLeases) > 0)
            _leasesDrained.Wait();

        DisposeNative();
        _shutdown.Dispose();

        lock (_lifecycle)
        {
            _state = LifecycleState.Disposed;
        }
    }

    private unsafe void DisposeNative()
    {
        // Stopping the loop joins its thread, so no callback can be in flight afterwards. That is
        // also why this cannot be done from the loop thread: the join would be the thread waiting
        // for itself, which hangs with nothing in the log to say why. There is no version of this
        // that works, because the caller is asking a thread to stop while standing on it, so it is
        // reported rather than deferred: deferring would return from Dispose with the loop still
        // running and no way for the caller to learn when it stopped.
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
    /// <remarks>
    /// Copying shares the lease, so disposing twice - or disposing two copies of one scope -
    /// unlocks and releases exactly once. A default scope holds nothing and disposes to nothing.
    /// </remarks>
    public readonly unsafe ref struct LoopLock
    {
        private readonly LoopLease? _lease;

        internal LoopLock(PipeWireContext context, PipeWireLoopHandle handle)
        {
            // The caller holds a reference for this scope; the lease owns it from here, and the
            // raw pointer beside it. Releasing reads neither the context nor the handle's field,
            // both of which disposal may already have cleared - only the pointer captured here,
            // which the reference keeps valid.
            _lease = new LoopLease(context, handle, handle.Loop);
            Native.pw_thread_loop_lock(_lease.Loop);
        }

        /// <summary>Releases the loop lock, and the reference taken on the loop.</summary>
        public void Dispose() => _lease?.Release();
    }

    /// <summary>One-shot native lock ownership shared by every copy of a <see cref="LoopLock"/>.</summary>
    private sealed unsafe class LoopLease
    {
        private readonly PipeWireContext _context;
        private readonly PipeWireLoopHandle _handle;
        private readonly pw_thread_loop* _loop;
        private int _released;

        internal LoopLease(PipeWireContext context, PipeWireLoopHandle handle, pw_thread_loop* loop)
        {
            _context = context;
            _handle = handle;
            _loop = loop;
        }

        internal pw_thread_loop* Loop => _loop;

        internal void Release()
        {
            // Exactly one unlock, one reference release and one lease release per acquisition,
            // however many copies were made and however often Dispose runs. An unbalanced unlock
            // corrupts the native mutex the loop - and every scope after it - depends on, and a
            // missing lease release wedges disposal's drain: it waits for a scope that is gone.
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            try
            {
                Native.pw_thread_loop_unlock(_loop);
                _handle.DangerousRelease();
            }
            finally
            {
                _context.ReleaseLease();
            }
        }
    }
}
