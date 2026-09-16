using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>
/// Owns the native lifecycle shared by every PipeWire stream wrapper: the
/// <c>pw_stream</c>, its event struct + listener hook, the self <see cref="GCHandle"/>,
/// buffer dequeue/queue, thread-loop locking, and disposal.
/// </summary>
/// <remarks>
/// The four public stream classes (video/audio x capture/output) are thin policy
/// layers over this core. They supply direction, properties, the format pod, and a
/// per-buffer handler; the core handles everything native and error-prone.
/// All <c>pw_stream</c> operations run under the context's thread-loop lock; the
/// <c>process</c> callback is invoked by the loop thread with that lock already held.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe partial class PipeWireStreamCore : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Timing snapshot for a processing cycle, from <c>pw_stream_get_time</c>. All on the one
    /// graph clock shared by every stream - the basis for A/V sync and sample-accurate position.
    /// </summary>
    /// <param name="GraphTimeNs">Monotonic graph time (ns) of the cycle. The sync reference.</param>
    /// <param name="StreamPositionNs">Media position (ns) at the cycle, from <c>ticks * rate</c>; -1 if unknown.</param>
    /// <param name="DelayNs">Signal delay/latency (ns) between this stream and the hardware.</param>
    /// <param name="Queued">
    /// Bytes (or frames, for audio) the stream has queued but not yet played or read, summed from
    /// the <c>size</c> each buffer was queued with.
    /// </param>
    /// <param name="Buffered">Extra frames an audio stream's resampler is holding (<c>pw_time.buffered</c>).</param>
    /// <param name="QueuedBuffers">Buffers currently queued.</param>
    /// <param name="AvailableBuffers">Buffers available to dequeue.</param>
    internal readonly record struct StreamClock(
        long GraphTimeNs,
        long StreamPositionNs,
        long DelayNs,
        ulong Queued,
        ulong Buffered,
        uint QueuedBuffers,
        uint AvailableBuffers);

    /// <summary>Invoked from <c>process</c> with the first data plane of a dequeued buffer.</summary>
    /// <param name="data">First data plane of the buffer.</param>
    /// <param name="buffer">The dequeued buffer (for metadata access).</param>
    /// <param name="clock">Timing snapshot for this cycle.</param>
    /// <remarks>The core dequeues before and queues after (even if this throws).</remarks>
    internal delegate void BufferHandler(spa_data* data, pw_buffer* buffer, in StreamClock clock);

    /// <summary>Invoked from <c>state_changed</c>.</summary>
    internal delegate void StateHandler(PipeWireStreamState oldState, PipeWireStreamState newState);

    /// <summary>Invoked from <c>param_changed</c> for the negotiated Format param only.</summary>
    /// <param name="param">
    /// The negotiated format, or <see langword="null"/> when the daemon withdrew it - the stream is
    /// no longer configured and whatever was negotiated before no longer describes anything.
    /// </param>
    internal delegate void FormatHandler(spa_pod* param);

    /// <summary>
    /// Invoked from <c>add_buffer</c>/<c>remove_buffer</c> when PipeWire allocates or frees a buffer in
    /// the negotiated pool. A dmabuf producer uses these to back each buffer's <c>spa_data</c> with its
    /// own dmabuf (in add) and release it (in remove). Both run on the loop thread with the lock held.
    /// </summary>
    internal delegate void BufferPoolHandler(pw_buffer* buffer);

    /// <summary>
    /// Invoked after <see cref="FormatHandler"/> so the stream can declare its buffer/meta
    /// requirements (it now knows the negotiated geometry). Call <c>RequestParamsFromCallback</c>
    /// from here. If not supplied, the core requests just the SPA_META_Header.
    /// </summary>
    internal delegate void PostFormatHandler(PipeWireStreamCore core);

    /// <summary>
    /// Invoked when the stream learns its peer's capabilities (<c>SPA_PARAM_PeerCapability</c>). A stream
    /// connected INACTIVE uses this to announce its real EnumFormats and activate, which is what starts
    /// format negotiation - upstream's video-src-fixate.c and video-play-fixate.c.
    /// </summary>
    /// <remarks>
    /// Always delivered, whatever the peer: when the daemon sends none, pw_stream synthesises one on
    /// the first Latency param (<c>stream.c, emit_dummy_peer_capability</c>), with no capabilities in it.
    /// </remarks>
    /// <param name="core">The stream.</param>
    /// <param name="param">The PeerCapability pod, valid for the duration of the call.</param>
    internal unsafe delegate void PeerConnectedHandler(PipeWireStreamCore core, spa_pod* param);

    private readonly PipeWireContext _ctx;
    private readonly ILogger _logger;

    // The graph hands these out once and updates them in place every cycle, so what is kept is the
    // pointer, not a copy. Written on the loop thread from io_changed, read under the loop lock.
    private unsafe spa_io_position* _ioPosition;
    private unsafe spa_io_rate_match* _ioRateMatch;
    private readonly string _streamName;

    /// <summary>The node this stream asked to be linked to, for reporting a failure against it.</summary>
    private uint _targetNodeId;
    private bool _firstBufferLogged;
    private readonly BufferHandler _onBuffer;
    private readonly StateHandler? _onState;
    private readonly FormatHandler? _onFormat;
    private readonly PostFormatHandler? _onPostFormat;
    private readonly BufferPoolHandler? _onAddBuffer;
    private readonly BufferPoolHandler? _onRemoveBuffer;
    private readonly PeerConnectedHandler? _onPeerConnected;

    private PipeWireStreamHandle? _streamOwner;

    // Completed when the stream first reaches Streaming, or faulted when it reaches Error. Run
    // asynchronously on purpose: the continuation must not run on the loop thread, where anything
    // a caller does after awaiting would deadlock against the lock the callback holds.
    // Controls the daemon has reported, newest report wins. A dictionary rather than a list: the
    // daemon re-reports a control whenever one of its values changes, and appending would grow
    // without bound on a stream whose volume is being moved.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, PipeWireStreamControl>
        _controls = new();

    private readonly TaskCompletionSource _streaming =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Completed with the node id once the daemon has bound this stream's proxy. Upstream's
    // proxy_bound_props (stream.c) assigns node_id and only then moves the stream to PAUSED, so the
    // first Paused - or Streaming, should Paused be skipped - is the point the id is known to be real.
    private readonly TaskCompletionSource<uint> _bound =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string? _lastError;

    private unsafe pw_stream* _stream => _streamOwner is null ? null : _streamOwner.Stream;
    private pw_stream_events* _events;
    // The spa_hook MUST live in unmanaged memory, not as a managed field: pw_stream_add_listener stores this
    // pointer in the stream's listener list, and the GC compacting the heap would move a managed field, leaving
    // PipeWire with a dangling pointer that crashes (spa_list_remove on freed memory) the next time it emits an
    // event. _selfHandle is weak and non-pinning either way, so it does not keep a field address stable.
    private spa_hook*         _hook;
    private GCHandle          _selfHandle;

    // 0 until disposal is claimed. Read from every native callback, so volatile; claimed with an
    // interlocked exchange, so two concurrent disposals cannot both tear the stream down.
    private volatile int      _disposedFlag;

    private bool _disposed => _disposedFlag != 0;

    /// <param name="ctx">A started <see cref="PipeWireContext"/>.</param>
    /// <param name="props">Stream properties (consumed by pw_stream_new).</param>
    /// <param name="streamName">node.name advertised by the stream.</param>
    /// <param name="onBuffer">Per-buffer handler (read for capture / fill for output).</param>
    /// <param name="onState">Optional state-change handler.</param>
    /// <param name="onFormat">Optional format-negotiation handler.</param>
    /// <param name="onPostFormat">Optional hook to declare buffer/meta params after format is set.</param>
    /// <param name="onAddBuffer">Optional hook to back a newly-allocated pool buffer with a dmabuf.</param>
    /// <param name="onRemoveBuffer">Optional hook to release a pool buffer's dmabuf before it is freed.</param>
    /// <param name="onPeerConnected">Optional hook invoked on SPA_PARAM_PeerCapability (a consumer linked).</param>
    internal PipeWireStreamCore(
        PipeWireContext ctx,
        StreamProperties props,
        string streamName,
        BufferHandler onBuffer,
        StateHandler? onState = null,
        FormatHandler? onFormat = null,
        PostFormatHandler? onPostFormat = null,
        BufferPoolHandler? onAddBuffer = null,
        BufferPoolHandler? onRemoveBuffer = null,
        PeerConnectedHandler? onPeerConnected = null)
    {
        _ctx            = ctx;
        _logger         = ctx.LoggerFactory.CreateLogger($"PipeWire.NET.{streamName}");
        _streamName     = streamName;
        _onBuffer       = onBuffer;
        _onState        = onState;
        _onPostFormat   = onPostFormat;
        _onFormat       = onFormat;
        _onAddBuffer    = onAddBuffer;
        _onRemoveBuffer = onRemoveBuffer;
        _onPeerConnected = onPeerConnected;

        // Weak: a strong self-handle roots the stream for the life of the process, so one dropped
        // without disposal leaks the native stream too.
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Weak);

        _hook = (spa_hook*)NativeMemory.AllocZeroed((nuint)sizeof(spa_hook));
        _events = (pw_stream_events*)NativeMemory.AllocZeroed((nuint)sizeof(pw_stream_events));
        _events->version       = NativeConstants.PW_VERSION_STREAM_EVENTS;
        _events->process       = &OnProcess;
        _events->state_changed = &OnStateChanged;
        _events->param_changed = &OnParamChanged;
        _events->control_info  = &OnControlInfo;
        _events->io_changed    = &OnIoChanged;
        _events->drained       = &OnDrained;
        _events->command       = &OnCommandArrived;
        _events->trigger_done  = &OnTriggerDone;
        if (onAddBuffer is not null)    _events->add_buffer    = &OnAddBuffer;
        if (onRemoveBuffer is not null) _events->remove_buffer = &OnRemoveBuffer;

        pw_properties* nativeProps = props.ToNativeProperties();

        ReadOnlySpan<byte> nameUtf8 = System.Text.Encoding.UTF8.GetBytes(streamName + '\0');
        using (_ctx.Lock())
        {
            pw_stream* stream;
            fixed (byte* n = nameUtf8)
                stream = Native.pw_stream_new(_ctx.CoreHandle, (sbyte*)n, nativeProps);

            if (stream is null)
            {
                _selfHandle.Free();
                NativeMemory.Free(_events);
                _events = null;
                NativeMemory.Free(_hook);
                _hook = null;
                throw new InvalidOperationException("pw_stream_new failed.");
            }

            // Owned like every other native object: the handle keeps the core and loop alive for as
            // long as the stream needs them to tear itself down. Until it exists, nothing else
            // knows about this stream, so a throw out of its constructor - the loop or core handle
            // refusing a reference because disposal won the race - would strand it.
            try
            {
                _streamOwner = new PipeWireStreamHandle(stream, _ctx.LoopOwner, _ctx.CoreOwner);
            }
            catch
            {
                Native.pw_stream_destroy(stream);
                _selfHandle.Free();
                NativeMemory.Free(_events);
                _events = null;
                NativeMemory.Free(_hook);
                _hook = null;
                throw;
            }

            // Handed over before the listener is attached, so the free happens after the stream has
            // been destroyed rather than racing its last callbacks.
            _streamOwner.OwnListener(_events, _hook, _selfHandle);

            Native.pw_stream_add_listener(stream, _hook, _events,
                (void*)GCHandle.ToIntPtr(_selfHandle));
        }
    }

    /// <summary>Refuses <c>PW_STREAM_FLAG_RT_PROCESS</c> for every stream this core drives.</summary>
    /// <remarks>
    /// <para>
    /// The stream types built on this core keep their buffer bookkeeping - the video output's free
    /// index stack and plane-layout table, the capture's retained-frame slots - on the premise that
    /// <c>process</c> runs on the same loop thread as <c>add_buffer</c>, <c>remove_buffer</c> and
    /// <c>param_changed</c>. Upstream emits those from the main loop (<c>impl_port_use_buffers</c>
    /// in stream.c), and only <c>RT_PROCESS</c> moves <c>process</c> to the data loop. With it the
    /// premise is gone and the plain collections race the realtime thread.
    /// </para>
    /// <para>
    /// A thrown argument error rather than a comment, so the day someone adds the flag for
    /// latency, the premise is revisited instead of silently broken. <see cref="PipeWire.NET.Graph.PipeWireFilter"/>
    /// is the realtime surface, and is not built on this core.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="flags"/> includes <c>RtProcess</c>.</exception>
    internal static void RequireLoopThreadProcess(PipeWireStreamFlags flags)
    {
        if ((flags & PipeWireStreamFlags.RtProcess) != 0)
            throw new ArgumentException(
                "streams built on PipeWireStreamCore process on the loop thread; RT_PROCESS would race their buffer bookkeeping",
                nameof(flags));
    }

    /// <summary>Connects the stream. <paramref name="formatPod"/> is copied by PipeWire before returning.</summary>
    /// <remarks>
    /// The SPA_META_Header (which carries the presentation timestamp) is NOT requested here -
    /// PipeWire's contract is to declare buffer/meta wants from the <c>param_changed</c> callback
    /// once the format is set, via <c>pw_stream_update_params</c>. The core does that automatically.
    /// </remarks>
    internal void Connect(
        SpaDirection direction,
        uint targetNodeId,
        PipeWireStreamFlags flags,
        ReadOnlySpan<byte> formatPod,
        ReadOnlySpan<byte> fallbackPod = default,
        ReadOnlySpan<byte> capabilityPod = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireLoopThreadProcess(flags);

        // Checked before the loop lock rather than after: taking it can wait on the loop thread,
        // and a caller that has already given up should not join that queue.
        cancellationToken.ThrowIfCancellationRequested();

        using System.Diagnostics.Activity? span =
            PipeWireDiagnostics.Source.StartActivity("pipewire.stream.connect");
        span?.SetTag("pipewire.stream.name", _streamName);
        span?.SetTag("pipewire.stream.direction", direction.ToString());
        span?.SetTag("pipewire.target.node", targetNodeId);
        _targetNodeId = targetNodeId;

        using (_ctx.Lock())
        {
            int rc;
            fixed (byte* fp = formatPod)
            fixed (byte* fb = fallbackPod)
            fixed (byte* cp = capabilityPod)
            {
                // Offered in preference order. A modifier choice is written mandatory, so with one
                // pod a producer that cannot do DMA-BUF has nothing left to agree to and
                // negotiation fails outright; a second pod without modifiers is the host-memory
                // path it can fall back to. A Capability param rides in the same list, as upstream's
                // fixate examples pass it to pw_stream_connect.
                spa_pod** offers = stackalloc spa_pod*[3];
                uint count = 0;
                offers[count++] = (spa_pod*)fp;
                if (!fallbackPod.IsEmpty) offers[count++] = (spa_pod*)fb;
                if (!capabilityPod.IsEmpty) offers[count++] = (spa_pod*)cp;

                rc = Native.pw_stream_connect(_stream, direction, targetNodeId, flags, offers, count);
            }
            if (rc < 0)
                throw new PipeWireInteropException("pw_stream_connect", rc);
        }
    }

    /// <summary>
    /// Declares that delivered buffers should carry a SPA_META_Header (presentation timestamp).
    /// Must be called from the param_changed callback after the format is set - that is the
    /// point at which PipeWire accepts buffer/meta requests via pw_stream_update_params.
    /// </summary>
    private void RequestHeaderMeta()
    {
        Span<byte> metaPod = stackalloc byte[64];
        SpaFormatPod.WriteHeaderMetaParam(metaPod);
        fixed (byte* mp = metaPod)
        {
            spa_pod* p = (spa_pod*)mp;
            Native.pw_stream_update_params(_stream, &p, 1);
        }
    }

    /// <summary>Puts the stream into the error state and tells the daemon why.</summary>
    internal unsafe void SetError(int result, string message, CancellationToken cancellationToken)
    {
        if (_disposed || _stream is null) return;
        cancellationToken.ThrowIfCancellationRequested();

        using (_ctx.Lock())
        {
            pw_stream* stream = _stream;
            if (_disposed || stream is null) return;

            Native.pw_stream_set_error(stream, result, message);
        }
    }

    private Exception? _lastProcessFault;
    private long _processFaults;

    /// <summary>How many times a process callback threw, and the most recent one.</summary>
    /// <remarks>
    /// Reported rather than logged, because the throw happens on the realtime thread where logging
    /// would itself cause an xrun. Each stream type surfaces this as <c>LastProcessError</c> and
    /// <c>ProcessErrorCount</c>, for a host to read from its own non-realtime loop.
    /// </remarks>
    internal (long Count, Exception? Last) ProcessFaults =>
        (Interlocked.Read(ref _processFaults), Volatile.Read(ref _lastProcessFault));

    /// <summary>Disposal here is synchronous; the async form defers to it.</summary>
    public void Dispose() => DisposeCore();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private void DisposeCore()
    {
        // Refused, loudly, rather than crashing. Disposing from inside a stream callback destroys
        // the stream while the frame that dispatched the callback is still on the stack: OnProcess
        // requeues the buffer in its finally, and that requeue lands on freed memory. The context
        // refuses the same thing for the same reason, and this is the stream's half of that rule.
        if (_ctx.IsOnLoopThread)
        {
            throw new InvalidOperationException(
                "A stream cannot be disposed from its own callback: the callback's frame is still "
                + "using the stream, and destroying it here corrupts the loop thread. Signal your "
                + "own code from the handler and dispose from the thread that created the stream.");
        }

        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

        // The drive timer first, and while the stream is still alive. The source belongs to the
        // stream's data loop and holds a pointer to this instance's handle; destroying it after the
        // loop has gone would be a write into freed memory, which is the whole shape of bug this
        // teardown order exists to avoid.
        if (_driveTimer is not null)
        {
            pw_stream* stream = _stream;
            if (stream is not null)
            {
                using (_ctx.Lock())
                {
                    pw_loop* loop = Native.pw_stream_get_data_loop(stream);
                    if (loop is not null && loop->utils is not null)
                        Native.spa_loop_utils_destroy_source(loop->utils, _driveTimer);
                }
            }

            _driveTimer = null;
        }

        // The handle disconnects and destroys under the loop lock, holding the core and loop open
        // for exactly as long as that takes - so this works whichever order the caller disposed in.
        // The listener's memory belongs to the handle, which frees it after pw_stream_destroy has
        // actually run - disposal only destroys once nothing else holds a reference.
        _streamOwner?.Dispose();
        _streamOwner = null;
        _events = null;
        _hook = null;
    }

    // - Native callbacks (invoked by the loop thread with the lock held) -

    /// <summary>Resolves the instance a native callback belongs to, or null if it is gone.</summary>
    /// <remarks>
    /// Contained on purpose. The handle is weak, and a freed one throws out of
    /// <see cref="GCHandle.FromIntPtr"/>; these are native frames, so an exception escaping the
    /// lookup aborts the process instead of unwinding into anything that could handle it.
    /// </remarks>
    private static PipeWireStreamCore? FromData(void* data)
    {
        try
        {
            return (PipeWireStreamCore?)GCHandle.FromIntPtr((nint)data).Target;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProcess(void* data)
    {
        PipeWireStreamCore? self = FromData(data);
        if (self is null || self._disposed) return;

        // Snapshotted once. Every call in this callback, including the queue in the finally, must
        // use the same pointer: re-reading the field would let a disposal between the dequeue and
        // the requeue hand the second call a different one.
        pw_stream* stream = self._stream;
        if (stream is null) return;

        pw_buffer* buf = Native.pw_stream_dequeue_buffer(stream);
        if (buf is null)
        {
            // No buffer queued for this cycle: the producer hasn't filled one yet (start-up) or is
            // underrunning. Common and benign at start, so Trace.
            self.LogDequeueEmpty();
            return;
        }
        try
        {
            spa_buffer* spaBuf = buf->buffer;

            // The count and the array are separate fields of a struct this process does not own, so
            // a non-zero count with no array behind it is a shape the daemon can present.
            if (spaBuf is null || spaBuf->datas is null || spaBuf->n_datas == 0) return;

            if (!self._firstBufferLogged)
            {
                self._firstBufferLogged = true;
                spa_data* d0 = &spaBuf->datas[0];
                self.LogFirstBuffer(spaBuf->n_datas, d0->type, d0->chunk is null ? 0u : d0->chunk->size, d0->maxsize);
            }

            // Graph clock for this cycle - the common monotonic reference across all streams,
            // plus media position (ticks*rate) and latency (delay*rate) per PipeWire's timing model.
            StreamClock clock = new(-1, -1, 0, 0, 0, 0, 0);
            pw_time t;
            if (Native.pw_stream_get_time_n(stream, &t, (nuint)sizeof(pw_time)) == 0)
            {
                // Integer, not double. A tick count past 2^53 loses resolution in a double, and
                // the product with 1e9 gets there far sooner: at 48 kHz the media clock drifts off
                // the sample grid within a few days of continuous playback, which is exactly the
                // kind of session this is meant to keep in sync. 128-bit intermediates cannot
                // overflow for any rate a sound card has. Ticks and rates are non-negative by
                // construction, so the media product is unsigned; the delay below stays signed
                // because negative latency compensation exists.
                long num = t.rate.num, denom = t.rate.denom;     // seconds per tick = num/denom
                long mediaNs = denom != 0
                    ? (long)((UInt128)t.ticks * (UInt128)num * 1_000_000_000 / (UInt128)denom)
                    : -1;
                long delayNs = denom != 0
                    ? (long)((Int128)(long)t.delay * num * 1_000_000_000 / denom)
                    : 0;
                // Occupancy as well as time. These are what a rate controller measures its error
                // against: module-rtp reads its own ring buffer because it owns one, but a stream
                // consumer's queue is the stream's, and this is where its depth is reported.
                clock = new StreamClock(
                    (long)t.now, mediaNs, delayNs,
                    t.queued, t.buffered, t.queued_buffers, t.avail_buffers);
            }

            spa_data* d = &spaBuf->datas[0];
            self._onBuffer(d, buf, in clock);
        }
        catch (Exception ex)
        {
            // Recorded, not logged: this is the realtime path, and logging from it is itself a
            // realtime violation. A silent swallow would hide a handler that throws every cycle,
            // so the fault is kept for a non-realtime reader to surface.
            // The exception is published before the count that advertises it, and both ends use
            // volatile access. A plain write ordered after the increment lets a reader that sees
            // the new count read the previous exception, or none at all, on a weak memory model.
            Volatile.Write(ref self._lastProcessFault, ex);
            Interlocked.Increment(ref self._processFaults);
        }
        finally
        {
            // Returned rather than queued when the handler said it did not use the buffer.
            // Returning makes it immediately available to dequeue again without counting as
            // consumed, which is what a consumer skipping a frame means; queueing it would report
            // data the consumer never took and skew the queue depth a rate controller reads.
            if (Volatile.Read(ref self._skipCurrent))
            {
                Volatile.Write(ref self._skipCurrent, false);
                Native.pw_stream_return_buffer(stream, buf);
            }
            else
            {
                Native.pw_stream_queue_buffer(stream, buf);
            }
        }
    }

    // Set by a handler, read once by the finally above. Only ever touched on the loop thread, but
    // volatile so the write inside the handler cannot be sunk past the read.
    private bool _skipCurrent;

    /// <summary>
    /// From inside a buffer handler: return this cycle's buffer unused instead of queueing it.
    /// </summary>
    /// <remarks>
    /// For a consumer that has decided to drop the frame. It applies to the buffer the handler is
    /// currently holding, and resets each cycle, so calling it outside a handler does nothing
    /// beyond skipping whatever arrives next.
    /// </remarks>
    internal void SkipCurrentBuffer() => Volatile.Write(ref _skipCurrent, true);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAddBuffer(void* data, pw_buffer* buffer)
    {
        PipeWireStreamCore? self = FromData(data);
        if (self is null || self._disposed) return;
        // The producer backs this buffer with its own dmabuf here. An escaping throw would abort the
        // process, and a silent swallow hides a handler that fails on every buffer, so the fault is
        // recorded for a non-realtime reader.
        try
        {
            self._onAddBuffer?.Invoke(buffer);
        }
        catch (Exception ex)
        {
            // The exception is published before the count that advertises it, and both ends use
            // volatile access. A plain write ordered after the increment lets a reader that sees
            // the new count read the previous exception, or none at all, on a weak memory model.
            Volatile.Write(ref self._lastProcessFault, ex);
            Interlocked.Increment(ref self._processFaults);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRemoveBuffer(void* data, pw_buffer* buffer)
    {
        PipeWireStreamCore? self = FromData(data);
        if (self is null) return;
        try
        {
            self._onRemoveBuffer?.Invoke(buffer);
        }
        catch (Exception ex)
        {
            // The exception is published before the count that advertises it, and both ends use
            // volatile access. A plain write ordered after the increment lets a reader that sees
            // the new count read the previous exception, or none at all, on a weak memory model.
            Volatile.Write(ref self._lastProcessFault, ex);
            Interlocked.Increment(ref self._processFaults);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStateChanged(void* data, PipeWireStreamState old, PipeWireStreamState state, sbyte* error)
    {
        PipeWireStreamCore? self = FromData(data);
        if (self is null) return;

        // The whole body, not just the handler. Reading the daemon's error string is a marshal over
        // a pointer this process did not allocate, and it is as capable of throwing out of a native
        // frame as the user code below it.
        try
        {
            if (error is not null)
            {
                string reason = DaemonText.String(error) ?? "(null)";
                self._lastError = reason;
                self.LogStreamError(reason);
            }

            self.LogStateChanged((PipeWireStreamState)(int)old, (PipeWireStreamState)(int)state);

            self._lastState = (int)state;
            self.SettleStreaming((PipeWireStreamState)(int)state);

            // Arm on streaming, disarm on anything else. Upstream's driver example does the same
            // from its state handler: a timer left running across a pause keeps triggering cycles
            // on a stream that is not scheduled to process them.
            self.ApplyDriveTimer((PipeWireStreamState)(int)state);

            self._onState?.Invoke((PipeWireStreamState)(int)old, (PipeWireStreamState)(int)state);
        }
        catch (Exception ex)
        {
            self.LogStateHandlerThrew(ex);
        }
    }

    /// <summary>Resolves the streaming completion once, on whichever terminal state arrives first.</summary>
    /// <remarks>
    /// Both calls are Try-: the daemon can report Error after Streaming, or the same state twice,
    /// and a second attempt on a settled source throws rather than being ignored.
    /// </remarks>
    private void SettleStreaming(PipeWireStreamState state)
    {
        if (state is PipeWireStreamState.Paused or PipeWireStreamState.Streaming
            && NodeId is var id && id != NativeConstants.PW_ID_ANY)
        {
            _bound.TrySetResult(id);
        }

        switch (state)
        {
            case PipeWireStreamState.Streaming:
                _streaming.TrySetResult();
                break;

            case PipeWireStreamState.Error:
                var refused = new PipeWireRequestRefusedException(
                    "pw_stream_connect", 0, _targetNodeId,
                    _lastError ?? $"stream '{_streamName}' reported no reason");
                _streaming.TrySetException(refused);
                _bound.TrySetException(refused);
                break;

            default:
                break;
        }
    }

    /// <summary>Waits until the stream is streaming, or throws if it fails or the wait is abandoned.</summary>
    /// <remarks>
    /// Negotiation is several round trips and the daemon drives it, so connecting is a request
    /// rather than an outcome. Cancelling abandons the wait only: the stream stays connected and
    /// keeps negotiating, because there is nothing to recall.
    /// </remarks>
    internal Task WaitForStreamingAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _streaming.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Waits until the daemon has assigned this stream a node id, and returns it.</summary>
    /// <remarks>
    /// The id is not known when connect returns: the stream is a proxy until the daemon binds it,
    /// and pw_stream_get_node_id answers SPA_ID_INVALID until then. Reading it straight after
    /// connecting is a race that usually loses. Upstream's examples read it in their PAUSED handler,
    /// which is what this awaits.
    /// </remarks>
    internal Task<uint> WaitForNodeIdAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _bound.Task.WaitAsync(cancellationToken);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnControlInfo(void* data, uint id, pw_stream_control* control)
    {
        PipeWireStreamCore? self = FromData(data);
        if (self is null) return;

        // Contained in full: this reads a struct and a string the daemon owns, and it is a native
        // frame where anything escaping aborts the process.
        try
        {
            if (control is null)
            {
                self._controls.TryRemove(id, out _);
                return;
            }

            // n_values is the daemon's word for how long the array is. Capped before a span is
            // built over it, the same way every other length off the wire is.
            uint count = control->n_values;
            if (count > MaxControlValues) count = MaxControlValues;

            var values = ImmutableArray.CreateBuilder<float>((int)count);
            if (control->values is not null)
            {
                for (uint i = 0; i < count; i++) values.Add(control->values[i]);
            }

            self._controls[id] = new PipeWireStreamControl(
                id,
                DaemonText.String(control->name) ?? string.Empty,
                control->def,
                control->min,
                control->max,
                values.ToImmutable(),
                control->max_values);
        }
        catch (Exception ex)
        {
            self.LogControlInfoThrew(ex);
        }
    }

    /// <summary>
    /// A ceiling on a control's value count, so a wrong <c>n_values</c> cannot walk off the array.
    /// </summary>
    /// <remarks>
    /// A control carries one value or one per channel, and no channel map is anywhere near this
    /// large. The number exists so a daemon reporting a count unrelated to its allocation is
    /// truncated rather than read past.
    /// </remarks>
    private const uint MaxControlValues = 1024;

    /// <summary>Every control the daemon has reported, by id.</summary>
    internal ImmutableArray<PipeWireStreamControl> Controls => [.. _controls.Values];

    /// <summary>One control by id, or null when the stream has not reported it.</summary>
    internal PipeWireStreamControl? GetControl(uint id) =>
        _controls.TryGetValue(id, out PipeWireStreamControl? control) ? control : null;

    /// <summary>Sets a control's values.</summary>
    /// <remarks>
    /// Built as a <c>Props</c> object and sent through <c>pw_stream_set_param</c> rather than
    /// through <c>pw_stream_set_control</c>. That function is variadic, which does not bind safely,
    /// and it does nothing else: it builds exactly this object from its arguments and calls
    /// <c>stream_set_param</c> with it (<c>pipewire/stream.c:2347-2404</c>). Going straight to the
    /// pod skips the calling-convention hazard and loses nothing.
    /// <para>
    /// The container the daemon expects depends on the control: a single value goes as a bare Float
    /// and several go as an Array of Float, which is the distinction its own builder makes.
    /// </para>
    /// </remarks>
    internal void SetControl(uint id, ReadOnlySpan<float> values, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        SpaValue value = values.Length == 1
            ? new SpaFloat(values[0])
            : new SpaArray(SpaType.Float, [.. values.ToArray().Select(static v => (SpaValue)new SpaFloat(v))]);

        byte[] pod = SpaPod.ToBytes(new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
            [new SpaPodProperty(id, 0, value)]));

        using (_ctx.Lock())
        {
            pw_stream* stream = _stream;
            if (stream is null) throw new ObjectDisposedException(nameof(PipeWireStreamCore));

            fixed (byte* p = pod)
            {
                int rc = Native.pw_stream_set_param(stream, (uint)SpaParamType.Props, (spa_pod*)p);
                if (rc < 0) throw new PipeWireInteropException("pw_stream_set_param", rc);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnParamChanged(void* data, uint id, spa_pod* param)
    {
        PipeWireStreamCore? self;
        try
        {
            self = (PipeWireStreamCore?)GCHandle.FromIntPtr((nint)data).Target;
        }
        catch (Exception)
        {
            // A freed handle throws out of FromIntPtr, and this is a native frame: an escaping
            // exception aborts the process rather than unwinding to anyone.
            return;
        }

        if (self is null || self._disposed) return;

        // Null means the parameter was withdrawn, not that it is unchanged. For the Format that is
        // the daemon saying the stream is no longer configured, and keeping the last one delivers
        // frames described by geometry that is no longer negotiated. The wrapper is told so it can
        // reset; every other param is genuinely nothing to do.
        if (param is null)
        {
            if ((SpaParamType)id != SpaParamType.Format) return;

        using System.Diagnostics.Activity? span =
            PipeWireDiagnostics.Source.StartActivity("pipewire.stream.negotiate");
        span?.SetTag("pipewire.stream.name", self._streamName);

            try { self._onFormat?.Invoke(null); }
            catch (Exception ex) { self.LogFormatHandlerThrew(ex); }
            return;
        }

        self.LogParamChanged(id);

        // SPA_PARAM_PeerCapability: where an INACTIVE stream announces its formats and activates
        // (video-src-fixate.c, video-play-fixate.c). A null one is ignored, as both examples ignore it.
        if (self._onPeerConnected is not null && id == (uint)SpaParamType.PeerCapability)
        {
            try
            {
                self._onPeerConnected.Invoke(self, param);
            }
            catch (Exception ex)
            {
                // Logged and contained: this is a native frame, and an escaping exception aborts
                // the process. The stream stays inactive, which the log explains.
                self.LogPeerHandlerThrew(ex);
            }

            return;
        }

        if ((SpaParamType)id != SpaParamType.Format) return;

        // This runs in an unmanaged callback, so contain it: an escaping exception would abort the process.
        try
        {
            self._onFormat?.Invoke(param);
            // Format is set -> declare buffer/meta requirements via pw_stream_update_params
            // (documented point; the loop lock is already held in this callback).
            if (self._onPostFormat is not null)
                self._onPostFormat(self);
            else
                self.RequestHeaderMeta();
        }
        catch (Exception ex)
        {
            // Reported to the graph, not just to the log. A handler that threw could not take the
            // format it was offered, and a stream that stays silent about that leaves its peer
            // waiting on a negotiation that will never finish. This is what gstreamer's
            // pipewiresrc does in the same place: pw_stream_set_error with EINVAL for a format it
            // cannot handle. The loop lock is already held in this callback.
            self.LogFormatHandlerThrew(ex);

            if (!self._disposed && self._stream is not null)
                Native.pw_stream_set_error(self._stream, -NativeLibc.EINVAL, $"format handler failed: {ex.Message}");
        }
    }

    /// <summary>The graph node id of this stream once connected (0 if not yet assigned).</summary>
    internal uint NodeId
    {
        get
        {
            if (_disposed || _stream is null)
            {
                return NativeConstants.PW_ID_ANY;
            }

            using (_ctx.Lock())
            {
                return Native.pw_stream_get_node_id(_stream);
            }
        }
    }

    /// <summary>
    /// Activates or deactivates the stream (<c>pw_stream_set_active</c>), taking the loop lock.
    /// A dmabuf DRIVER connects INACTIVE and is activated once its format and buffers are negotiated.
    /// </summary>
    internal void SetActive(bool active)
    {
        if (_disposed || _stream is null) return;

        using (_ctx.Lock())
        {
            SetActiveFromCallback(active);
        }
    }

    /// <summary>
    /// Same as <see cref="SetActive"/>, for callers already on the loop thread inside a stream
    /// callback, where the loop lock is held. Taking it again would work, the lock is recursive,
    /// but the name is the contract: a caller that is not in a callback wants the other method.
    /// </summary>
    internal void SetActiveFromCallback(bool active)
    {
        if (_disposed || _stream is null) return;

        Native.pw_stream_set_active(_stream, active);
    }

    // A drain finishing and a trigger cycle finishing are both one-shot notifications from the
    // loop thread. Kept as TaskCompletionSources so a caller can await them rather than poll.
    private TaskCompletionSource? _drained;
    private TaskCompletionSource? _triggerDone;

    /// <summary>Invoked on the loop thread when the daemon sends the node a command.</summary>
    internal delegate void CommandHandler(SpaNodeCommand command);

    private CommandHandler? _onCommand;

    /// <summary>Sets the command hook. Not an event: one owner, set during construction.</summary>
    internal CommandHandler? OnCommand
    {
        get => _onCommand;
        set => _onCommand = value;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnDrained(void* data)
    {
        PipeWireStreamCore? self;
        try { self = (PipeWireStreamCore?)GCHandle.FromIntPtr((IntPtr)data).Target; }
        catch (Exception) { return; }
        self?._drained?.TrySetResult();
    }

    /// <summary>
    /// A node command from the daemon, after <c>pw_stream</c> has acted on it.
    /// </summary>
    /// <remarks>
    /// The stream handles Pause and Start itself and then passes every command on, so what reaches
    /// here includes Suspend (the node's format and device are being dropped), Flush, Drain, and
    /// RequestProcess, which is how lazy scheduling asks for a cycle. A sender that wants to know
    /// the graph has parked it has no other way to find out: the state change that accompanies a
    /// suspend says the stream is no longer streaming, not why.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnCommandArrived(void* data, spa_command* command)
    {
        PipeWireStreamCore? self;
        try { self = (PipeWireStreamCore?)GCHandle.FromIntPtr((IntPtr)data).Target; }
        catch (Exception) { return; }
        if (self is null || command is null) return;

        // SPA_COMMAND_ID: the id only means a node command when the body says it is one, and a
        // command of another type reuses the same numbers for different things.
        if (command->body.body.type != (uint)SpaType.CommandNode) return;

        CommandHandler? handler = self._onCommand;
        if (handler is null) return;

        // A native callback frame: an escaping exception aborts the process.
        try { handler((SpaNodeCommand)command->body.body.id); }
        catch (Exception ex) { self.LogCommandHandlerThrew(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnTriggerDone(void* data)
    {
        PipeWireStreamCore? self;
        try { self = (PipeWireStreamCore?)GCHandle.FromIntPtr((IntPtr)data).Target; }
        catch (Exception) { return; }
        if (self is null) return;
        self.LogTriggerDone(self._triggerDone is { Task.IsCompleted: false });
        self._triggerDone?.TrySetResult();
    }

    /// <summary>Drains what is queued and waits for the daemon to say it has played out.</summary>
    internal Task DrainAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _stream is null) return Task.CompletedTask;

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _drained = done;
        FlushDraining();
        return StreamSignal.AwaitAsync(done, cancellationToken);
    }

    /// <summary>Runs one cycle and waits for it to finish. Only meaningful while driving.</summary>
    internal Task TriggerAndWaitAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _stream is null) return Task.CompletedTask;

        // stream.c emits trigger_done only for a driving stream (driver_end && using_trigger), so a
        // follower's wait would never end. Refused up front rather than left to the caller's timeout.
        if (!IsDriving)
            return Task.FromException(new InvalidOperationException(
                "the stream is not the graph's driver, so no triggered cycle will report completion; "
                + "connect with driver: true and wait for IsDriving"));

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _triggerDone = done;
        TriggerProcess();
        return StreamSignal.AwaitAsync(done, cancellationToken);
    }

    // Split out because the class is unsafe and an await cannot live in an unsafe context.
    private unsafe void FlushDraining()
    {
        using (_ctx.Lock())
        {
            pw_stream* stream = _stream;
            if (_disposed || stream is null) return;
            Native.pw_stream_flush(stream, drain: true);
        }
    }

    // SPA_IO_Position (7) and SPA_IO_RateMatch (8) from spa/node/io.h.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnIoChanged(void* data, uint id, void* area, uint size)
    {
        PipeWireStreamCore? self;
        try { self = (PipeWireStreamCore?)GCHandle.FromIntPtr((IntPtr)data).Target; }
        catch (Exception) { return; }
        if (self is null || self._disposed) return;

        // A null area means the graph took it away, which happens on disconnect; keeping the old
        // pointer would read freed memory on the next look.
        switch (id)
        {
            case (uint)SpaIoType.Position:
                self._ioPosition = size >= (uint)sizeof(spa_io_position) ? (spa_io_position*)area : null;
                break;
            case (uint)SpaIoType.RateMatch:
                self._ioRateMatch = size >= (uint)sizeof(spa_io_rate_match) ? (spa_io_rate_match*)area : null;
                break;
        }
    }

    /// <summary>The graph clock as of the last cycle, or null if the graph has not offered it.</summary>
    internal unsafe PipeWireGraphClock? GraphClock
    {
        get
        {
            if (_disposed) return null;

            using (_ctx.Lock())
            {
                spa_io_position* p = _ioPosition;
                if (_disposed || p is null) return null;

                return new PipeWireGraphClock(
                    p->clock.nsec, p->clock.position, p->clock.duration,
                    p->clock.rate.num, p->clock.rate.denom,
                    p->clock.delay, p->clock.rate_diff, p->clock.next_nsec);
            }
        }
    }

    /// <summary>What the resampler is doing, or null when nothing is resampling this stream.</summary>
    internal unsafe PipeWireRateMatch? RateMatch
    {
        get
        {
            if (_disposed) return null;

            using (_ctx.Lock())
            {
                spa_io_rate_match* r = _ioRateMatch;
                if (_disposed || r is null) return null;

                return new PipeWireRateMatch(r->delay, r->size, r->rate, r->flags);
            }
        }
    }

    /// <summary>Applies a rate correction, 1.0 being none.</summary>
    internal unsafe void SetRate(double rate, CancellationToken cancellationToken)
    {
        if (_disposed || _stream is null) return;
        cancellationToken.ThrowIfCancellationRequested();

        using (_ctx.Lock())
        {
            pw_stream* stream = _stream;
            if (_disposed || stream is null) return;
            Native.pw_stream_set_rate(stream, rate);
        }
    }

    /// <summary>
    /// Announces this stream's own latency to the graph.
    /// </summary>
    /// <remarks>
    /// A stream that adds delay - a transport with a queue, an encoder - has to say so, or nothing
    /// downstream can compensate and audio and video drift apart by exactly the amount nobody was
    /// told about. PipeWire's own rtp and tunnel modules announce both of these together, which is
    /// why both are taken here rather than one.
    /// </remarks>
    internal unsafe void AnnounceLatency(PipeWireLatency latency, PipeWireProcessLatency? process, CancellationToken cancellationToken)
    {
        if (_disposed || _stream is null) return;
        cancellationToken.ThrowIfCancellationRequested();

        byte[] latencyPod = SpaPod.ToBytes(latency.ToParameter());
        byte[]? processPod = process is null ? null : SpaPod.ToBytes(process.ToParameter());

        using (_ctx.Lock())
        {
            pw_stream* stream = _stream;
            if (_disposed || stream is null) return;

            fixed (byte* lp = latencyPod)
            fixed (byte* pp = processPod)
            {
                spa_pod** pods = stackalloc spa_pod*[2];
                uint n = 0;
                pods[n++] = (spa_pod*)lp;
                if (processPod is not null) pods[n++] = (spa_pod*)pp;

                Native.pw_stream_update_params(stream, pods, n);
            }
        }
    }

    /// <summary>Whether the daemon has made this stream the graph's driver.</summary>
    // The driver's timer: a source on the stream's own data loop, so the callback lands on the
    // thread that processes, not on the main loop.
    private unsafe spa_source* _driveTimer;

    // What DriveAt was last asked for. The timer only runs while the stream is streaming, so this
    // is what gets re-armed when it starts again rather than making the caller ask twice.
    private TimeSpan _driveInterval;

    // The last state the daemon reported, as an int because volatile cannot be applied to an enum.
    // Read by DriveAt when it is called before the stream starts, which is the normal case.
    private volatile int _lastState;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnDriveTimer(void* data, ulong expirations)
    {
        PipeWireStreamCore? self;
        try { self = (PipeWireStreamCore?)GCHandle.FromIntPtr((IntPtr)data).Target; }
        catch (Exception) { return; }
        if (self is null || self._disposed) return;

        pw_stream* stream = self._stream;
        if (stream is null) return;

        // Already on the data loop, and trigger_process does its own hop, so no lock is taken:
        // taking the main loop lock from the data thread is the deadlock this path exists to avoid.
        // The clock is published first, on this same thread, as upstream's pipewiresink does.
        if (Native.pw_stream_is_driving(stream)) self.PublishDriverClock(stream);
        Native.pw_stream_trigger_process(stream);
    }

    /// <summary>
    /// Writes the graph clock for a cycle this stream is about to drive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A driver publishes the clock every node in its group is stamped from. A driver <em>node</em>
    /// does it itself - <c>support/node-driver.c</c> and <c>null-audio-sink.c</c> write their
    /// <c>SPA_IO_Clock</c> area each cycle. A <em>stream</em> that drives gets no such help:
    /// <c>pw_stream_trigger_process</c> runs the cycle and writes nothing, and the daemon only
    /// initialises the clock while the driver is not yet running (<c>context.c</c>). Left alone,
    /// every consumer of a graph this library drives sees <c>pw_time.now</c> frozen at that value -
    /// which is what a capture of a stream-driven group reports.
    /// </para>
    /// <para>
    /// Upstream's own answer is in GStreamer's pipewiresink, whose <c>update_time()</c> writes
    /// <c>nsec</c>, <c>position</c>, <c>duration</c>, <c>rate</c> and <c>next_nsec</c> into the
    /// stream's position area on the data loop immediately before each trigger. This does the same,
    /// without its rate-correction loop: the quantum and rate are the ones the daemon asked this
    /// driver for (<c>target_duration</c>, <c>target_rate</c>), so there is no second clock to
    /// correct against.
    /// </para>
    /// <para>
    /// Only ever called for a stream the daemon reports as driving. Writing a position area this
    /// stream does not drive would overwrite the real driver's clock for every node in the group.
    /// Must run on the data loop, or under its lock, because the processing thread reads the area.
    /// </para>
    /// </remarks>
    private unsafe void PublishDriverClock(pw_stream* stream)
    {
        spa_io_position* p = _ioPosition;
        if (p is null) return;

        ulong duration = p->clock.target_duration;
        spa_fraction rate = p->clock.target_rate;
        if (duration == 0 || rate.denom == 0) return;

        ulong now = Native.pw_stream_get_nsec(stream);

        // Position counts in the clock's own units, so it advances by the duration of the cycle that
        // just ended - the same running sample position pipewiresink keeps.
        p->clock.position += p->clock.duration;
        p->clock.nsec = now;
        p->clock.duration = duration;
        p->clock.rate = rate;
        p->clock.next_nsec = now + (ulong)((UInt128)duration * rate.num * 1_000_000_000UL / rate.denom);
        p->clock.rate_diff = 1.0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int DoPublishDriverClock(spa_loop* loop, bool async, uint seq, void* data, nuint size, void* userData)
    {
        // Runs under the data loop's lock, called from C: nothing may escape.
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)userData).Target is PipeWireStreamCore self
                && !self._disposed && self._stream is not null)
            {
                self.PublishDriverClock(self._stream);
            }
        }
        catch (Exception)
        {
            // Deliberately not logged: this is a realtime-adjacent native frame, and a missed clock
            // write costs one cycle's timestamp while a throw here would abort the process.
        }

        return 0;
    }

    /// <summary>The current time on the clock the graph is stamped from (<c>pw_stream_get_nsec</c>).</summary>
    /// <remarks>
    /// CLOCK_MONOTONIC, the same clock driver nodes publish the graph clock in. Upstream's
    /// video-src stamps each frame's presentation time with it. Safe from the data loop.
    /// </remarks>
    internal unsafe long NowNs()
    {
        pw_stream* stream = _stream;
        return stream is null ? -1 : (long)Native.pw_stream_get_nsec(stream);
    }

    /// <summary>Writes a presentation time into a buffer's <c>SPA_META_Header</c>, if it has one.</summary>
    /// <remarks>
    /// Shared by every producer so audio and video stamp the same way. The header only exists when
    /// the buffers were allocated with it, which the core arranges by requesting the meta once the
    /// format is set; a buffer without one is left alone. The meta count belongs to the pool, so it is
    /// bounded rather than trusted.
    /// </remarks>
    internal static unsafe void StampPresentationTime(pw_buffer* buf, long pts)
    {
        if (pts < 0 || buf is null) return;

        spa_buffer* sb = buf->buffer;
        if (sb is null || sb->metas is null) return;

        uint metas = Math.Min(sb->n_metas, 64u);
        for (uint i = 0; i < metas; i++)
        {
            spa_meta* m = &sb->metas[i];
            if (m->type != (uint)SpaMetaType.Header
                || m->data is null
                || m->size < (uint)sizeof(spa_meta_header))
            {
                continue;
            }

            ((spa_meta_header*)m->data)->pts = pts;
            return;
        }
    }

    /// <summary>
    /// Drives a cycle every <paramref name="interval"/> from the stream's own data loop.
    /// </summary>
    /// <param name="interval">The period. <see cref="TimeSpan.Zero"/> disarms the timer.</param>
    /// <returns>True when the timer was armed or disarmed as asked.</returns>
    /// <remarks>
    /// <para>
    /// For a DRIVER producer that has to pace the graph itself. The alternative a caller has today
    /// is to call the trigger from a thread of its own, which works but paces against that thread's
    /// scheduling rather than the loop's, and pays a hop on every cycle.
    /// </para>
    /// <para>
    /// This dispatches through a hand-written copy of the loop's method table, so it refuses to run
    /// against a table whose version it does not recognise - see
    /// <c>spa_loop_utils_methods</c>. A false return means the loop declined, not that the
    /// interval was wrong.
    /// </para>
    /// </remarks>
    internal unsafe bool DriveAt(TimeSpan interval)
    {
        if (_disposed || _stream is null) return false;

        _driveInterval = interval > TimeSpan.Zero ? interval : TimeSpan.Zero;

        // Only actually armed while streaming. Asking before the stream starts is normal - a caller
        // sets the pace up front - so this records the request and the state handler applies it.
        return ApplyDriveTimer((PipeWireStreamState)_lastState);
    }

    /// <summary>Arms or disarms the drive timer to match the stream's state.</summary>
    private unsafe bool ApplyDriveTimer(PipeWireStreamState state)
    {
        if (_disposed) return false;

        bool shouldRun = state == PipeWireStreamState.Streaming && _driveInterval > TimeSpan.Zero;
        if (!shouldRun && _driveTimer is null) return true;

        using (_ctx.Lock())
        {
            pw_stream* stream = _stream;
            if (_disposed || stream is null) return false;

            pw_loop* loop = Native.pw_stream_get_data_loop(stream);
            if (loop is null || loop->utils is null) return false;

            if (_driveTimer is null)
            {
                if (!shouldRun) return true;

                _driveTimer = Native.spa_loop_utils_add_timer(
                    loop->utils, &OnDriveTimer, (void*)GCHandle.ToIntPtr(_selfHandle));

                if (_driveTimer is null) return false;
            }

            PosixTimespec value = default;
            PosixTimespec period = default;

            if (shouldRun)
            {
                long ns = (long)(_driveInterval.TotalMilliseconds * 1_000_000.0);
                if (ns <= 0) ns = 1;
                value.tv_sec = (nint)(ns / 1_000_000_000);
                value.tv_nsec = (nint)(ns % 1_000_000_000);
                period = value;
            }

            // Both zero disarms, which is what a null pair means to the loop as well.
            return Native.spa_loop_utils_update_timer(
                loop->utils, _driveTimer, &value, &period, absolute: false) == 0;
        }
    }

    /// <summary>
    /// Offers a new set of formats on a running stream, asking the peer to renegotiate.
    /// </summary>
    /// <param name="enumFormatPod">A <c>SPA_PARAM_EnumFormat</c> pod.</param>
    /// <returns>0 or better on success, a negative errno otherwise.</returns>
    /// <remarks>
    /// Unlike the param helpers used during negotiation, this is meant to be called from outside
    /// the format callback - which is how upstream's renegotiation examples drive it, from a timer
    /// rather than from `param_changed`. The peer answers by running the format exchange again, so
    /// the caller sees a fresh format arrive through the usual path rather than a return value.
    /// </remarks>
    internal unsafe int RequestFormats(ReadOnlySpan<byte> enumFormatPod)
    {
        if (_disposed || _stream is null || enumFormatPod.IsEmpty) return -NativeLibc.EINVAL;

        using (_ctx.Lock())
        {
            pw_stream* stream = _stream;
            if (_disposed || stream is null) return -NativeLibc.EINVAL;

            fixed (byte* p = enumFormatPod)
            {
                spa_pod** arr = stackalloc spa_pod*[1];
                arr[0] = (spa_pod*)p;
                return Native.pw_stream_update_params(stream, arr, 1);
            }
        }
    }

    /// <summary>The stream's queue depth, or null if it cannot be read.</summary>
    internal unsafe PipeWireStreamQueue? Queue
    {
        get
        {
            if (_disposed || _stream is null) return null;

            using (_ctx.Lock())
            {
                pw_stream* stream = _stream;
                if (_disposed || stream is null) return null;

                pw_time t;
                if (Native.pw_stream_get_time_n(stream, &t, (nuint)sizeof(pw_time)) != 0)
                    return null;

                return new PipeWireStreamQueue(t.queued, t.buffered, t.queued_buffers, t.avail_buffers);
            }
        }
    }

    /// <summary>
    /// Whether the daemon has put this stream in lazy scheduling, where cycles happen on request
    /// rather than on a timer.
    /// </summary>
    /// <remarks>
    /// Worth knowing before driving: in lazy mode a producer is expected to answer RequestProcess
    /// rather than pace itself, so a driver that also runs its own timer produces twice.
    /// </remarks>
    internal unsafe bool IsLazy
    {
        get
        {
            if (_disposed || _stream is null) return false;

            using (_ctx.Lock())
            {
                pw_stream* stream = _stream;
                if (_disposed || stream is null) return false;
                return Native.pw_stream_is_lazy(stream);
            }
        }
    }

    /// <summary>Adds or replaces properties on a live stream.</summary>
    /// <param name="properties">The keys to set. An empty value removes a key.</param>
    /// <returns>The number of properties actually changed.</returns>
    /// <remarks>
    /// Retagging without reconnecting. The alternative is tearing the stream down and building it
    /// again, which drops the link and everything buffered behind it - a heavy price for renaming
    /// what a sender is currently sending.
    /// </remarks>
    internal unsafe int UpdateProperties(IReadOnlyDictionary<string, string> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (_disposed || _stream is null || properties.Count == 0) return 0;

        // Sized from the input rather than a fixed scratch: a caller retagging with a long
        // media.name would otherwise silently lose the tail. UTF-8 is at most 4 bytes per char,
        // plus a terminator for each key and value.
        int bytes = 0;
        foreach (KeyValuePair<string, string> kv in properties)
            bytes += ((kv.Key.Length + kv.Value.Length) * 4) + 2;

        byte[] scratch = new byte[bytes];
        spa_dict_item[] items = new spa_dict_item[properties.Count];

        var builder = new SpaDictBuilder(scratch, items);
        foreach (KeyValuePair<string, string> kv in properties)
            builder.Add(kv.Key, kv.Value);

        spa_dict native = builder.Build();

        using (_ctx.Lock())
        {
            pw_stream* stream = _stream;
            if (_disposed || stream is null) return 0;
            return Native.pw_stream_update_properties(stream, &native);
        }
    }

    internal unsafe bool IsDriving
    {
        get
        {
            if (_disposed || _stream is null) return false;

            using (_ctx.Lock())
            {
                pw_stream* stream = _stream;
                if (_disposed || stream is null) return false;
                return Native.pw_stream_is_driving(stream);
            }
        }
    }

    /// <summary>
    /// Drives one processing cycle (<c>pw_stream_trigger_process</c>), which is how a DRIVER
    /// producer paces output when no other node drives the graph clock. No-op if the stream is gone.
    /// </summary>
    internal void TriggerProcess()
    {
        if (_disposed || _stream is null) return;

        using (_ctx.Lock())
        {
            // Re-read and re-checked under the lock. The check above is against a field a
            // concurrent disposal clears, and taking the lock is exactly the window in which that
            // happens, so a pointer read after it can be null where the one before it was not.
            pw_stream* stream = _stream;
            if (_disposed || stream is null) return;

            // Only a driving stream may trigger. Upstream routes the request to whichever node is
            // actually driving the graph, and asking a node that does not implement RequestProcess
            // - an audio adapter, typically - produces an error per call, so a caller pacing at
            // frame rate turns into an error per frame in the daemon's log for no effect. Whether
            // this stream drives is the daemon's answer, not ours: it depends on the graph.
            if (!Native.pw_stream_is_driving(stream)) return;

            // The clock is written under the data loop's lock rather than from here directly: the
            // processing thread reads that area, and this is the main thread. Synchronous, so the
            // handle it is given cannot outlive the call.
            _ = Native.pw_loop_locked(
                Native.pw_stream_get_data_loop(stream),
                &DoPublishDriverClock,
                (void*)GCHandle.ToIntPtr(_selfHandle));

            int rc = Native.pw_stream_trigger_process(stream);
            LogTriggered(rc);
        }
    }

    /// <summary>
    /// Sends any number of params, laid out end to end in one buffer.
    /// </summary>
    /// <remarks>
    /// The fixed-arity overload runs out at six, and a video consumer already wants seven. Pods are
    /// self-describing - each carries its own size - so one buffer of them can be walked rather than
    /// passed a parameter at a time.
    /// </remarks>
    /// <param name="pods">Concatenated pods, each starting on an 8-byte boundary as SPA requires.</param>
    /// <param name="count">How many pods are in the buffer.</param>
    internal int RequestParamsFromCallback(ReadOnlySpan<byte> pods, int count)
    {
        if (pods.IsEmpty || count <= 0) return -NativeLibc.EINVAL;

        pw_stream* stream = _stream;
        if (_disposed || stream is null) return -NativeLibc.EINVAL;

        fixed (byte* start = pods)
        {
            spa_pod** arr = stackalloc spa_pod*[count];
            byte* p = start;
            byte* end = start + pods.Length;

            for (int i = 0; i < count; i++)
            {
                if (p + sizeof(spa_pod) > end) return -NativeLibc.EINVAL;

                arr[i] = (spa_pod*)p;

                // A pod is its header plus its body, rounded up to 8 - the same walk SPA does.
                nuint advance = (nuint)sizeof(spa_pod) + ((spa_pod*)p)->size;
                advance = (advance + 7) & ~(nuint)7;
                p += advance;

                if (p > end && i + 1 < count) return -NativeLibc.EINVAL;
            }

            return Native.pw_stream_update_params(stream, arr, (uint)count);
        }
    }

    /// <summary>
    /// Sends up to two param pods via pw_stream_update_params. Call only from the param_changed
    /// callback (where the loop lock is held), e.g. from a <see cref="PostFormatHandler"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately does not take the lock. Every caller is a format or peer callback dispatched by
    /// the loop thread, which holds it already; taking it here again is harmless today because the
    /// mutex is recursive, but the name records the contract so it does not get "fixed" into a call
    /// that also runs from a caller thread.
    /// </remarks>
    /// <returns>
    /// The daemon's result, negative on failure, or <c>-EINVAL</c> when there was nothing to send.
    /// </returns>
    internal int RequestParamsFromCallback(
        ReadOnlySpan<byte> pod0, ReadOnlySpan<byte> pod1 = default, ReadOnlySpan<byte> pod2 = default,
        ReadOnlySpan<byte> pod3 = default, ReadOnlySpan<byte> pod4 = default, ReadOnlySpan<byte> pod5 = default)
    {
        // An empty span fixes to a null pointer, and handing the daemon an array of one null pod
        // with a count of one is a dereference on its side, not ours. Snapshotted once for the same
        // reason OnProcess does: the field can be cleared by a disposal between the two reads.
        if (pod0.IsEmpty) return -NativeLibc.EINVAL;

        // A gap would put a null in the middle of the array, which is the same dereference one
        // position along. Refused rather than compacted: a caller passing the third and not the
        // second has miscounted, and silently sending two pods hides that.
        if (pod1.IsEmpty && !pod2.IsEmpty) return -NativeLibc.EINVAL;
        if (pod2.IsEmpty && !pod3.IsEmpty) return -NativeLibc.EINVAL;
        if (pod3.IsEmpty && !pod4.IsEmpty) return -NativeLibc.EINVAL;
        if (pod4.IsEmpty && !pod5.IsEmpty) return -NativeLibc.EINVAL;

        pw_stream* stream = _stream;
        if (_disposed || stream is null) return -NativeLibc.EINVAL;

        fixed (byte* p0 = pod0)
        fixed (byte* p1 = pod1)
        fixed (byte* p2 = pod2)
        fixed (byte* p3 = pod3)
        fixed (byte* p4 = pod4)
        fixed (byte* p5 = pod5)
        {
            spa_pod** arr = stackalloc spa_pod*[6];
            int count = 0;
            arr[count++] = (spa_pod*)p0;
            if (!pod1.IsEmpty) arr[count++] = (spa_pod*)p1;
            if (!pod2.IsEmpty) arr[count++] = (spa_pod*)p2;
            if (!pod3.IsEmpty) arr[count++] = (spa_pod*)p3;
            if (!pod4.IsEmpty) arr[count++] = (spa_pod*)p4;
            if (!pod5.IsEmpty) arr[count++] = (spa_pod*)p5;

            return Native.pw_stream_update_params(stream, arr, (uint)count);
        }
    }

    // Diagnostics (source-generated, level-gated). Enable at Debug/Trace via the host's logger
    // factory passed to PipeWireContext. The stream name is the logger category, so each
    // stream's lifecycle is filterable on its own.

    [LoggerMessage(Level = LogLevel.Debug, Message = "state {Old} -> {New}")]
    private partial void LogStateChanged(PipeWireStreamState old, PipeWireStreamState @new);

    [LoggerMessage(Level = LogLevel.Error, Message = "stream error: {Error}")]
    private partial void LogStreamError(string error);

    [LoggerMessage(Level = LogLevel.Trace, Message = "param_changed id={Id}")]
    private partial void LogParamChanged(uint id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "first buffer: n_datas={Blocks} type={DataType} size={Size} maxsize={MaxSize}")]
    private partial void LogFirstBuffer(uint blocks, uint dataType, uint size, uint maxSize);

    [LoggerMessage(Level = LogLevel.Trace, Message = "process: no buffer dequeued (producer underrun or not yet started)")]
    private partial void LogDequeueEmpty();

    [LoggerMessage(Level = LogLevel.Error, Message = "a node command handler threw")]
    private partial void LogCommandHandlerThrew(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "a stream state handler threw")]
    private partial void LogStateHandlerThrew(Exception ex);

    [LoggerMessage(EventId = 34990, Level = LogLevel.Error,
        Message = "a control_info callback threw")]
    private partial void LogControlInfoThrew(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "a format handler threw; negotiation continued with defaults")]
    private partial void LogFormatHandlerThrew(Exception ex);

    [LoggerMessage(EventId = 34992, Level = LogLevel.Trace, Message = "trigger_process -> {Result}")]
    private partial void LogTriggered(int result);

    [LoggerMessage(EventId = 34993, Level = LogLevel.Trace, Message = "trigger_done (a wait was pending: {Pending})")]
    private partial void LogTriggerDone(bool pending);

    [LoggerMessage(EventId = 34991, Level = LogLevel.Error,
        Message = "the peer-capability handler threw; an INACTIVE stream stays inactive")]
    private partial void LogPeerHandlerThrew(Exception ex);
}
