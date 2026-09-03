using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media.Streams;

/// <summary>
/// Publishes video frames TO PipeWire as a virtual camera. Two modes:
/// <list type="bullet">
/// <item>Host memory (default): PipeWire pulls frames by invoking <see cref="FillFrame"/>; write your
/// pixels into the supplied span.</item>
/// <item>Zero-copy dmabuf: call <see cref="ConnectDmaBuf"/> with the DRM modifiers your GPU can export.
/// The library negotiates dmabuf buffers and asks you (via <see cref="AllocateDmaBuf"/>) to back each
/// pool buffer with a dmabuf you own; you render into it and publish from <see cref="FillDmaBuf"/>. No
/// pixel copy ever touches the CPU. <see cref="ConnectDmaBufSync"/> adds explicit timeline
/// synchronization on top of the same transport.</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Dispose it; do not let it fall out of scope.</b> The callbacks the daemon holds refer back
/// here through a weak handle, so an instance the application drops is collected and simply stops
/// delivering, with no error and no final state change. That is the deliberate half of the trade:
/// a strong handle would keep every one ever made alive for the life of the process. What it costs
/// is that the garbage collector cannot be the thing that closes one, because by the time it runs
/// there is nothing left to close it from.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireVideoOutput : IAsyncDisposable
{
    /// <summary>Return <see langword="true"/> to publish the frame.</summary>
    public delegate bool FillFrameHandler(
        PipeWireVideoOutput sender, Span<byte> pixels, int stride, int width, int height, PixelFormat format);

    /// <summary>
    /// Asks the app to back output pool buffer <paramref name="bufferIndex"/> with a dmabuf it owns: fill
    /// one <see cref="VideoPlane"/> per plane into <paramref name="planes"/> and return the plane count
    /// (0 to decline). Called once per pool buffer, on the loop thread, after dmabuf format negotiation -
    /// allocate your GPU surface (e.g. a Vulkan image exported to a dmabuf fd) for <paramref name="modifier"/> here.
    /// </summary>
    public delegate int AllocateDmaBufHandler(
        PipeWireVideoOutput sender, int bufferIndex, int width, int height, ulong modifier, Span<VideoPlane> planes);

    /// <summary>
    /// Asks the app to render the current frame into pool buffer <paramref name="bufferIndex"/>'s dmabuf and
    /// return <see langword="true"/> to publish it (false to emit an empty frame). Called on the loop thread.
    /// </summary>
    public delegate bool FillDmaBufHandler(PipeWireVideoOutput sender, int bufferIndex);

    /// <summary>
    /// Backs a pool buffer with an app-owned dmabuf plus explicit-sync timelines, for
    /// <see cref="ConnectDmaBufSync"/>. Like <see cref="AllocateDmaBufHandler"/>, plus the two
    /// timeline descriptors: set <paramref name="acquireFd"/> and <paramref name="releaseFd"/> to
    /// the app's own timeline descriptors, or leave either at -1 for a library eventfd.
    /// </summary>
    /// <remarks>
    /// An app descriptor is borrowed: it must stay valid until <see cref="ReleaseDmaBuf"/> for the
    /// buffer, and the app closes it. A -1 becomes a library eventfd, closed automatically when
    /// the buffer goes. Either way the descriptors order the buffer, they never carry pixels.
    /// </remarks>
    public delegate int AllocateDmaBufSyncHandler(
        PipeWireVideoOutput sender, int bufferIndex, int width, int height, ulong modifier,
        Span<VideoPlane> planes, out long acquireFd, out long releaseFd);

    /// <summary>Notifies the app that pool buffer <paramref name="bufferIndex"/>'s dmabuf can be released.</summary>
    public delegate void ReleaseDmaBufHandler(PipeWireVideoOutput sender, int bufferIndex);

    /// <summary>Invoked on the loop thread when a host-memory buffer is ready to fill.</summary>
    public event FillFrameHandler? FillFrame;

    /// <summary>Invoked (dmabuf mode) to back a pool buffer with an app-owned dmabuf.</summary>
    public event AllocateDmaBufHandler? AllocateDmaBuf;

    /// <summary>Invoked (dmabuf mode) to render and publish the current frame.</summary>
    public event FillDmaBufHandler? FillDmaBuf;

    /// <summary>Invoked (explicit-sync mode) to back a pool buffer with a dmabuf and timelines.</summary>
    public event AllocateDmaBufSyncHandler? AllocateDmaBufSync;

    /// <summary>Invoked (dmabuf mode) when a pool buffer's dmabuf can be released.</summary>
    public event ReleaseDmaBufHandler? ReleaseDmaBuf;

    /// <summary>Raised on the loop thread when the connection state changes.</summary>
    public event Action<PipeWireVideoOutput, PipeWireStreamState, PipeWireStreamState>? StateChanged;

    private readonly PipeWireContext _ctx;
    private readonly string _name;
    private readonly int _width, _height, _frameRate;
    private readonly PixelFormat _format;
    private readonly ILogger _logger;
    private PipeWireStreamCore? _core;

    // dmabuf-mode state. Set once at ConnectDmaBuf; the modifier is fixated during negotiation. We never
    // retain the offered modifier list (see VideoFormatInfo for why) - fixation re-offers _fmt.Modifier.
    private bool _dmaBufMode;
    private bool _modifierFixated;
    private bool _announcedToAPeer;
    private long[] _modifiers = [];
    private sealed class NegotiatedFormat(SpaFormatPod.VideoFormatInfo info)
    {
        public SpaFormatPod.VideoFormatInfo Info { get; } = info;
    }

    // Swapped whole rather than mutated, for the same reason as the capture side: a multi-field
    // struct written on the loop thread and read elsewhere has no atomic assignment.
    private NegotiatedFormat _fmtCell;

    private SpaFormatPod.VideoFormatInfo Format => Volatile.Read(ref _fmtCell).Info;

    private int _planeCount;
    private int _nextBufferIndex;

    // Explicit-sync state. Armed by ConnectDmaBufSync; untouched otherwise, in which case the
    // dmabuf path below behaves exactly as before.
    private bool _explicitSync;
    private ulong _syncSeq; // loop thread only: default points when the app stamps none
    private PendingSyncPoints?[] _pendingSync = new PendingSyncPoints[MaxPoolBuffers];

    /// <summary>Points the app stamped for a buffer's next publish, taken once.</summary>
    private sealed class PendingSyncPoints(ulong acquire, ulong release)
    {
        public ulong Acquire { get; } = acquire;
        public ulong Release { get; } = release;
    }

    // Per-buffer timeline descriptors and who closes them. Touched only on add, remove and
    // dispose - never on the process path, which reads descriptors from the buffer itself - so an
    // ordinary lock is correct here and never crosses into realtime work.
    private readonly Lock _syncGate = new();
    private readonly int[] _syncAcquireFds = ClosedFds();
    private readonly int[] _syncReleaseFds = ClosedFds();
    private readonly bool[] _syncAcquireOwned = new bool[MaxPoolBuffers];
    private readonly bool[] _syncReleaseOwned = new bool[MaxPoolBuffers];

    private static int[] ClosedFds()
    {
        var fds = new int[MaxPoolBuffers];
        Array.Fill(fds, -1);
        return fds;
    }

    /// <summary>Sync timeline descriptors per buffer: acquire first, release second.</summary>
    private const int SyncDataBlocks = SpaFormatPod.SyncTimelineDataBlocks;

    /// <summary>Set by the producer each cycle; cleared by a consumer promising release.</summary>
    private const uint SyncUnscheduledRelease = 1u << 0;

    /// <summary>Bytes a format pod needs to carry that many DRM modifiers.</summary>
    /// <remarks>
    /// The fixed part is the media type, subtype, format, size and framerate properties with their
    /// headers; the variable part is the modifier choice, which repeats its default and so writes
    /// one more value than it was given.
    /// </remarks>
    private static int ModifierPodBytes(int modifiers) => 512 + ((modifiers + 1) * 8);

    /// <summary>The most planes a single buffer can be backed with.</summary>
    /// <remarks>
    /// The stack buffer handed to the allocator is this long, and the layout table below matches it.
    /// Four is already more than any format here needs; eight leaves room for a modifier that
    /// carries auxiliary planes.
    /// </remarks>
    private const int MaxPlanes = 8;

    /// <summary>Per-plane offset and stride as the app declared them, per pool buffer.</summary>
    /// <remarks>
    /// Kept so the layout can be reasserted on every publish: the chunk is shared memory a consumer
    /// may write to, so what add_buffer put there is not necessarily what is there a hundred frames
    /// later. Keyed by buffer index, because the pool's buffers do not have to share a layout - a
    /// per-buffer GPU allocation is free to differ in stride, and one table for all of them
    /// publishes every buffer with whichever was added last.
    /// <para>
    /// A fixed table, never a resizable collection: the process callback reads its slot with
    /// acquire ordering and takes whatever is there or nothing, so no lock crosses into it and no
    /// hash table is ever traversed while another thread publishes. Each slot holds a complete
    /// array published once, never mutated afterwards - a reader racing a removal sees the old
    /// layout or none, both safe, never a torn one. A removal therefore cannot strand an in-flight
    /// process callback: the worst case publishes nothing for that cycle.
    /// </para>
    /// </remarks>
    private readonly (uint Offset, int Stride)[]?[] _planeLayouts =
        new (uint Offset, int Stride)[]?[MaxPoolBuffers];

    /// <summary>Most buffers one stream pool ever holds; bounds the publication table above.</summary>
    private const int MaxPoolBuffers = 64;

    // Indices freed by remove_buffer, for reuse. PipeWire tears buffers down and builds them again
    // on every renegotiation, so handing out a fresh index each time walks past the end of a
    // consumer's pool - which is sized for the buffer count, not the renegotiation count.
    // Touched only by add_buffer and remove_buffer, which are both stream-event callbacks on the
    // loop thread; the process callback never touches it (it reads the publication table above),
    // so no lock crosses into the realtime path for index bookkeeping either.
    private readonly Stack<int> _freeBufferIndices = new();

    /// <param name="context">A started <see cref="PipeWireContext"/>.</param>
    /// <param name="nodeName">Name visible to consumers.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="format">Pixel format to publish.</param>
    /// <param name="frameRate">Target frame rate (Hz).</param>
    public PipeWireVideoOutput(PipeWireContext context, string nodeName,
        int width, int height, PixelFormat format = PixelFormat.Bgra, int frameRate = 30)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(nodeName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        _ctx = context; _name = nodeName;
        _width = width; _height = height; _format = format; _frameRate = frameRate;
        _fmtCell = new NegotiatedFormat(
            new SpaFormatPod.VideoFormatInfo(format, width, height, VideoColorInfo.Unknown));
        _logger = context.LoggerFactory.CreateLogger($"PipeWire.NET.{nodeName}");
    }

    /// <summary>Any node - let the session manager choose where this stream is routed.</summary>
    public const uint AnyNode = Native.PW_ID_ANY;

    /// <summary>Starts publishing host-memory frames and registers the node in the graph.</summary>
    /// <param name="targetNodeId">
    /// The node to route into, or <see cref="AnyNode"/> to let the session manager decide.
    /// </param>
    /// <param name="targetObjectName">
    /// Optional <c>target.object</c> - bind to a specific node by name or serial regardless of
    /// the session manager's default-device routing.
    /// </param>
    /// <param name="autoConnect">
    /// When true the session manager routes this stream automatically. Pass
    /// <see langword="false"/> with an explicit target to publish the node and link it
    /// deliberately, independent of session-manager policy: a targeted link does not need a
    /// default device to exist. A test or a transport usually wants that; a camera app does not.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the wait for the loop lock. The connect request itself is issued
    /// synchronously once that is held, so there is nothing to recall after it.
    /// </param>
    public unsafe void Connect(
        uint targetNodeId = AnyNode,
        string? targetObjectName = null,
        bool autoConnect = true,
        CancellationToken cancellationToken = default)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");

        var props = new StreamProperties(StreamMediaType.Video, StreamCategory.Playback)
            .WithRole("Camera")
            .WithNodeName(_name);
        if (targetObjectName is not null) props.WithTargetObject(targetObjectName);

        // OnPostFormatHostMem declares the buffer requirements once the format is set. This is mandatory for a
        // video producer: unlike audio (whose buffer size PipeWire derives from the graph clock), a video node
        // must advertise the exact image size/stride, or the daemon cannot size the shared-memory buffers -
        // negotiation then drives pw_impl_port_set_param into a bad dereference (a hard crash) and the consumer
        // only ever dequeues empty (size-0) buffers. PipeWire's own video-src.c declares Buffers the same way.
        // Built locally and only published once the connect succeeded. Assigning the field first
        // leaves a failed connect behind a stream that reports itself already connected and can
        // never be retried.
        var core = new PipeWireStreamCore(
            _ctx, props, _name, OnBuffer, OnState, OnFormat, OnPostFormatHostMem);

        Span<byte> pod = stackalloc byte[512];
        int len = SpaFormatPod.WriteVideoFormat(pod,
            stackalloc[] { _format }, (uint)_width, (uint)_height, (uint)_frameRate, fixedSize: true);

        PipeWireStreamFlags flags = PipeWireStreamFlags.MapBuffers;
        if (autoConnect) flags |= PipeWireStreamFlags.Autoconnect;

        try
        {
            core.Connect(SpaDirection.Output, targetNodeId, flags,
            pod[..len],
            cancellationToken: cancellationToken);
            _core = core;
        }
        catch
        {
            core.Dispose();
            throw;
        }

    }

    // Host-memory buffer requirements: one contiguous MemPtr block holding the whole image (the OnBuffer path
    // fills datas[0] with stride*height bytes), plus the SPA_META_Header so frames carry a PTS.
    private void OnPostFormatHostMem(PipeWireStreamCore core)
    {
        // Sized from what was negotiated, not from what was asked for. The offer is fixated, so the
        // two normally agree, but declaring buffers from the constructor arguments risks a chunk size
        // and stride describing an image the buffer does not hold when the daemon adjusts the format,
        // and the consumer reads the difference as pixels.
        SpaFormatPod.VideoFormatInfo fmt = Format;
        if (fmt.Format == PixelFormat.Unknown || fmt.Width <= 0 || fmt.Height <= 0) return;

        int stride = SpaFormatPod.VideoStride(fmt.Format, fmt.Width);
        int size = SpaFormatPod.VideoImageSize(fmt.Format, fmt.Width, fmt.Height);
        Span<byte> buffers = stackalloc byte[256];

        // One block, planes contiguous inside it. SPA allows either shape for a planar format, and
        // this producer writes the whole image into datas[0] - which is what FillFrame is handed.
        int bl = SpaFormatPod.WriteVideoBuffersParam(buffers, size, stride,
            dataTypes: 1 << (int)SpaDataType.MemPtr, blocks: 1);

        Span<byte> meta = stackalloc byte[64];
        int ml = SpaFormatPod.WriteHeaderMetaParam(meta);
        core.RequestParamsFromCallback(buffers[..bl], meta[..ml]);
    }

    /// <summary>
    /// Starts publishing zero-copy dmabuf frames with explicit synchronization: every buffer
    /// carries <c>SPA_META_SyncTimeline</c> points plus acquire and release timeline descriptors,
    /// instead of relying on implicit fences.
    /// </summary>
    /// <param name="modifiers">
    /// The DRM format modifiers this producer can export, in priority order.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the wait for the loop lock. The connect request itself is issued
    /// synchronously once that is held, so there is nothing to recall after it.
    /// </param>
    /// <remarks>
    /// <para>
    /// Wire <see cref="AllocateDmaBufSync"/> to provide per-buffer timelines, or leave it unset
    /// and back buffers with <see cref="AllocateDmaBuf"/> while the library raises eventfd
    /// timelines for them. Either way stamp per-frame points with <see cref="StampSyncPoints"/>;
    /// unstamped frames carry a running sequence instead.
    /// </para>
    /// <para>
    /// A consumer that agrees stops attaching implicit fences, so a peer ignoring the points
    /// races the GPU. Use a dedicated context: the release wait blocks the loop thread when a
    /// consumer promised to signal and has not yet, which stalls every stream on a shared one.
    /// </para>
    /// </remarks>
    public unsafe void ConnectDmaBufSync(
        ReadOnlySpan<long> modifiers, CancellationToken cancellationToken = default)
    {
        _explicitSync = true;
        try
        {
            ConnectDmaBuf(modifiers, cancellationToken);
        }
        catch
        {
            _explicitSync = false;
            throw;
        }
    }

    /// <summary>
    /// Stamps the acquire and release points for a buffer's next published frame, from any thread.
    /// </summary>
    /// <param name="bufferIndex">The buffer, as handed to the fill and allocate handlers.</param>
    /// <param name="acquirePoint">The timeline point at which the frame may be read.</param>
    /// <param name="releasePoint">The point the consumer signals when it is done with it.</param>
    /// <remarks>
    /// Taken once, by the next publish of that buffer; a frame published without a stamp carries
    /// the running sequence instead. Stamping from outside the fill handler is the point: an app
    /// whose GPU work completes on its own queue stamps the point its submission will signal.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferIndex"/> is not a pool buffer.</exception>
    public void StampSyncPoints(int bufferIndex, ulong acquirePoint, ulong releasePoint)
    {
        if ((uint)bufferIndex >= (uint)MaxPoolBuffers)
            throw new ArgumentOutOfRangeException(nameof(bufferIndex),
                $"buffer indices run 0 to {MaxPoolBuffers - 1}.");

        Volatile.Write(
            ref _pendingSync[bufferIndex], new PendingSyncPoints(acquirePoint, releasePoint));
    }

    /// <summary>
    /// Starts publishing zero-copy dmabuf frames, offering the given DRM format modifiers (the set your
    /// GPU can export for the configured <see cref="PixelFormat"/>). Wire <see cref="AllocateDmaBuf"/>,
    /// <see cref="FillDmaBuf"/> and (optionally) <see cref="ReleaseDmaBuf"/> before calling.
    /// </summary>
    /// <param name="modifiers">
    /// The DRM format modifiers this producer can export, in priority order.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the wait for the loop lock. The connect request itself is issued
    /// synchronously once that is held, so there is nothing to recall after it.
    /// </param>
    public unsafe void ConnectDmaBuf(
        ReadOnlySpan<long> modifiers, CancellationToken cancellationToken = default)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");
        if (modifiers.IsEmpty) throw new ArgumentException("At least one DRM modifier must be offered.", nameof(modifiers));

        _dmaBufMode = true;
        _modifierFixated = false;
        _planeCount = SpaFormatPod.PlaneCount(_format);

        var props = new StreamProperties(StreamMediaType.Video, StreamCategory.Playback)
            .WithRole("Camera")
            .WithNodeName(_name);

        // add_buffer/remove_buffer let us back each pool buffer with an app-owned dmabuf; the producer
        // supplies the memory, so we use ALLOC_BUFFERS (and NOT MAP_BUFFERS - there is nothing to mmap).
        _modifiers = modifiers.ToArray();
        // Built locally and only published once the connect succeeded. Assigning the field first
        // leaves a failed connect behind a stream that reports itself already connected and can
        // never be retried.
        // Offer modifiers with DONT_FIXATE so a GL consumer's EGL selects an importable modifier (radeonsi only
        // imports the tiled AMD modifiers, not LINEAR). Connect INACTIVE|ALLOC_BUFFERS (NOT a DRIVER - the
        // consumer drives the graph clock; a DRIVER would have to pace itself via trigger_process, which is
        // unsafe to call from another thread and crashes libpipewire). On a consumer link the daemon delivers
        // SPA_PARAM_PeerCapability, where OnPeerConnected re-announces the EnumFormat and activates the stream,
        // kicking the (now-correct) modifier fixation; the consumer then drives FillDmaBuf.
        // Sized from the offer, not from a constant. A Choice(Enum) of N modifiers writes
        // (N+1) * 8 bytes of values, and a modern driver exports dozens per format, so a fixed
        // 512-byte pod runs out on exactly the hardware zero-copy exists for.
        byte[] pod = new byte[ModifierPodBytes(modifiers.Length)];
        int len = SpaFormatPod.WriteVideoFormat(pod,
            stackalloc[] { _format }, (uint)_width, (uint)_height, (uint)_frameRate, fixedSize: true,
            modifiers: modifiers);

        // Built before the core exists, so a pod that cannot be written does not leave a native
        // stream behind: the failure path below only runs once there is something to dispose.
        var core = new PipeWireStreamCore(_ctx, props, _name, OnBuffer, OnState, OnFormat, OnPostFormat,
            OnAddBuffer, OnRemoveBuffer, OnPeerConnected);

        try
        {
            // Deliberately not PW_STREAM_FLAG_DRIVER. The consumer drives the graph clock, and
            // claiming the driver role stops frames reaching the consumer entirely.
            core.Connect(SpaDirection.Output, Native.PW_ID_ANY,
            PipeWireStreamFlags.Inactive | PipeWireStreamFlags.AllocBuffers,
            pod[..len],
            cancellationToken: cancellationToken);
            _core = core;
        }
        catch
        {
            core.Dispose();
            throw;
        }

    }

    // A consumer linked (SPA_PARAM_PeerCapability): re-announce the EnumFormat so the daemon negotiates a
    // format with the peer, then activate the INACTIVE stream - the video-src-fixate.c producer flow. Loop
    // lock is held here.
    private unsafe void OnPeerConnected(PipeWireStreamCore core)
    {
        // Once, not once per peer. The daemon reports PeerCapability for every consumer that links,
        // and re-announcing the EnumFormat restarts negotiation, so a second consumer joining would
        // renegotiate the format underneath the first one mid-stream. The announce exists to get an
        // INACTIVE producer going; after that there is nothing to do.
        if (_announcedToAPeer) return;
        _announcedToAPeer = true;

        byte[] pod = new byte[ModifierPodBytes(_modifiers.Length)];
        ReadOnlySpan<PixelFormat> fmt = [_format];
        int len = SpaFormatPod.WriteVideoFormat(pod, fmt,
            (uint)_width, (uint)_height, (uint)_frameRate, fixedSize: true, modifiers: _modifiers);
        core.RequestParamsFromCallback(pod[..len]);
        core.SetActiveFromCallback(true);
    }

    /// <summary>
    /// Asks for one publish cycle (<c>pw_stream_trigger_process</c>), which fires
    /// <see cref="FillDmaBuf"/> on the loop thread. Call after staging a new frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request, not a command, and only meaningful once a consumer is driving. The gate is
    /// "connected", not "streaming": a call before the stream reaches Streaming reaches the daemon
    /// and does nothing, which is upstream's documented behaviour for a non-driver node rather than
    /// an error. Calling it from a foreign thread is the supported shape; calling it from the loop
    /// thread drives the cycle directly.
    /// </para>
    /// <para>
    /// No-op before <see cref="Connect(uint, string?, bool, CancellationToken)"/> or <see cref="ConnectDmaBuf"/>, and after disposal.
    /// </para>
    /// </remarks>
    public void TriggerFrame() => _core?.TriggerProcess();

    /// <summary>
    /// This stream's own node in the graph, or <see langword="null"/> until it is connected.
    /// </summary>
    /// <remarks>
    /// A stream is a node like any other, so this is the handle for routing it:
    /// <c>graph.GetPortsForNode(stream.NodeId!.Value)</c> finds its ports, which can then be linked.
    /// </remarks>
    public uint? NodeId
    {
        get
        {
            uint id = _core?.NodeId ?? Native.PW_ID_ANY;
            return id == Native.PW_ID_ANY ? null : id;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // Library eventfds first: the pool may go without remove_buffer for every buffer, in which
        // case nothing else would close them. App descriptors are borrowed and stay the app's.
        // Slots are cleared, so a late remove_buffer finds nothing to close twice. Under the loop
        // lock when it can be taken, so no process callback is mid-use of a descriptor being
        // closed; without it (teardown already past the point of callbacks) the sweep is safe
        // because nothing is left to race it.
        bool locked = _ctx.TryLock(out PipeWireContext.LoopLock scope);
        try
        {
            lock (_syncGate)
            {
                for (int i = 0; i < MaxPoolBuffers; i++)
                {
                    if (_syncAcquireOwned[i]) Descriptors.CloseEventfd(_syncAcquireFds[i]);
                    if (_syncReleaseOwned[i]) Descriptors.CloseEventfd(_syncReleaseFds[i]);
                    _syncAcquireFds[i] = -1;
                    _syncReleaseFds[i] = -1;
                    _syncAcquireOwned[i] = false;
                    _syncReleaseOwned[i] = false;
                }
            }
        }
        finally
        {
            if (locked) scope.Dispose();
        }

        return _core?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private unsafe void OnBuffer(spa_data* d, pw_buffer* buf, in PipeWireStreamCore.StreamClock clock)
    {
        if (_dmaBufMode) { FillDmaBufBuffer(buf, in clock); return; }

        if (d->data is null || d->chunk is null) return;

        SpaFormatPod.VideoFormatInfo fmt = Format;
        if (fmt.Format == PixelFormat.Unknown || fmt.Width <= 0 || fmt.Height <= 0) return;

        // Not stride * height: for planar formats (NV12, YUV420) that is the luma plane alone, and
        // publishing it truncates every frame by a third with the chroma planes missing.
        int stride  = SpaFormatPod.VideoStride(fmt.Format, fmt.Width);
        int byteLen = SpaFormatPod.VideoImageSize(fmt.Format, fmt.Width, fmt.Height);
        if ((uint)byteLen > d->maxsize) byteLen = (int)d->maxsize;

        // Written before the handler runs. The core queues the buffer in a finally even when the
        // handler throws, and a chunk left holding the previous cycle's size republishes that many
        // bytes of whatever the buffer now contains as though it were a fresh frame.
        d->chunk->offset = 0;
        d->chunk->stride = stride;
        d->chunk->size   = 0;

        var pixels = new Span<byte>(d->data, byteLen);
        bool publish = FillFrame?.Invoke(this, pixels, stride, fmt.Width, fmt.Height, fmt.Format) ?? false;

        if (!publish) return;

        d->chunk->size = (uint)byteLen;
        WritePresentationTime(buf, in clock);
    }

    /// <summary>Stamps the buffer's header meta with this cycle's graph time.</summary>
    /// <remarks>
    /// Both producer paths request SPA_META_Header, so a frame published without one carries no
    /// presentation time and every consumer reading it gets -1. The graph clock is the same
    /// reference the capture side reports, which is what makes the two comparable for sync.
    /// </remarks>
    private static unsafe void WritePresentationTime(pw_buffer* buf, in PipeWireStreamCore.StreamClock clock)
    {
        if (clock.CaptureClockNs < 0) return;

        spa_buffer* sb = buf->buffer;
        if (sb is null || sb->metas is null) return;

        // Bounded for the same reason the capture side bounds it: the count belongs to the pool.
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

            ((spa_meta_header*)m->data)->pts = clock.CaptureClockNs;
            return;
        }
    }

    // Producer process for a dmabuf buffer: the dmabuf layout (offset/stride) was fixed in add_buffer, so
    // here we only ask the app to render the frame and then mark each plane's chunk size to publish it (or
    // 0 to emit an empty frame). The fd/plane geometry never changes, hence no per-frame copy.
    private unsafe void FillDmaBufBuffer(pw_buffer* buf, in PipeWireStreamCore.StreamClock clock)
    {
        spa_buffer* sb = buf->buffer;
        if (sb is null) return;
        int index = (int)(nint)buf->user_data - 1; // we store index+1 so 0 means "unassigned"
        if (index < 0 || (uint)index >= (uint)MaxPoolBuffers) return;

        // Cleared before the handler, for the same reason as the host-memory path: a throw must
        // publish nothing rather than republish the previous frame's sizes.
        for (uint i = 0; i < sb->n_datas; i++)
        {
            spa_chunk* c = sb->datas[i].chunk;
            if (c is not null) c->size = 0;
        }

        // Explicit sync, before the app renders: when the consumer cleared UNSCHEDULED_RELEASE it
        // promised to signal the release point, so a nonzero one is waited for here, exactly like
        // upstream's producer. A set flag means no promise - publish without waiting. This blocks
        // the loop thread, which is why explicit-sync streams want a dedicated context.
        spa_meta_sync_timeline* sync = _explicitSync ? FindSyncTimeline(sb) : null;
        int waitAcquireFd = -1, waitReleaseFd = -1;
        bool syncReady = sync is not null
            && SpaFormatPod.TryFindSyncDataFds(sb, out waitAcquireFd, out waitReleaseFd)
            && waitAcquireFd >= 0 && waitReleaseFd >= 0;
        ulong acquirePoint, releasePoint;
        PendingSyncPoints? stamped =
            Interlocked.Exchange(ref _pendingSync[index], null);
        if (stamped is not null)
        {
            acquirePoint = stamped.Acquire;
            releasePoint = stamped.Release;
        }
        else
        {
            acquirePoint = releasePoint = ++_syncSeq;
        }

        // Explicit sync, before the app renders: the wait decision reads the meta's current
        // state, left by the previous cycle and the consumer - not the points about to be
        // stamped below. A nonzero release point with UNSCHEDULED_RELEASE cleared means the
        // consumer promised to signal and hasn't yet, so rendering into the buffer would
        // overwrite a frame still being read. A set flag (or a zero point, as on a fresh pool)
        // means no promise: render without waiting. Upstream's producer reads it the same way.
        if (syncReady && (*sync).release_point != 0
            && (((*sync).flags & SyncUnscheduledRelease) == 0))
            Descriptors.WaitEventfd(waitReleaseFd);

        bool publish = FillDmaBuf?.Invoke(this, index) ?? false;

        if (syncReady)
        {
            // Fresh every cycle: the flag re-arms the promise protocol, the points are this
            // frame's, and the acquire signal releases the consumer's wait. Stamped even for a
            // declined frame - the timeline must keep moving, and an unsignalled acquire would
            // wedge a consumer waiting on it.
            (*sync).flags = SyncUnscheduledRelease;
            (*sync).acquire_point = acquirePoint;
            (*sync).release_point = releasePoint;
            Descriptors.SignalEventfd(waitAcquireFd);
        }

        if (!publish) return;

        // Lock-free by construction: a whole array or nothing, so a removal racing this read
        // degrades to an unpublished cycle rather than a torn layout.
        (uint Offset, int Stride)[]? layout = Volatile.Read(ref _planeLayouts[index]);
        if (layout is null) return;

        for (uint i = 0; i < sb->n_datas; i++)
        {
            spa_chunk* c = sb->datas[i].chunk;
            if (c is null) continue;

            // The plane layout was fixed in add_buffer and is reasserted here rather than assumed
            // to have survived: a consumer or filter that recycled the buffer is free to have
            // rewritten offset and stride, and a frame queued with someone else's crop reads as
            // a shifted image with no error anywhere.
            if (i < (uint)layout.Length)
            {
                c->offset = layout[i].Offset;
                c->stride = layout[i].Stride;
            }

            c->size = sb->datas[i].maxsize;
        }

        WritePresentationTime(buf, in clock);
    }

    /// <summary>Finds the sync timeline meta of a pool buffer, when the peer agreed to carry one.</summary>
    private static unsafe spa_meta_sync_timeline* FindSyncTimeline(spa_buffer* sb)
    {
        if (sb is null || sb->metas is null) return null;

        uint count = Math.Min(sb->n_metas, 64u);
        for (uint i = 0; i < count; i++)
        {
            spa_meta* m = &sb->metas[i];
            if (m->type != (uint)SpaMetaType.SyncTimeline || m->data is null) continue;
            if (m->size < (uint)sizeof(spa_meta_sync_timeline)) continue;
            return (spa_meta_sync_timeline*)m->data;
        }

        return null;
    }

    private unsafe void OnFormat(spa_pod* param)
    {
        if (param is null)
        {
            Volatile.Write(ref _fmtCell,
                new NegotiatedFormat(
                    new SpaFormatPod.VideoFormatInfo(PixelFormat.Unknown, 0, 0, VideoColorInfo.Unknown)));
            _modifierFixated = false;
            return;
        }

        SpaFormatPod.VideoFormatInfo parsed = SpaFormatPod.ParseVideoFormat(param, Format);
        Volatile.Write(ref _fmtCell, new NegotiatedFormat(parsed));
        LogOnFormat(parsed.Modifier, parsed.ModifierNeedsFixation);
    }

    private void OnPostFormat(PipeWireStreamCore core)
    {
        SpaFormatPod.VideoFormatInfo negotiated = Format;
        LogOnPostFormat(_modifierFixated, negotiated.ModifierNeedsFixation, _planeCount);

        // Mirror the consumer's two-step modifier fixation on the producer side: when the peer honoured
        // DONT_FIXATE we re-offer our preferred returned modifier alone (DONT_FIXATE cleared) to settle it.
        if (!_modifierFixated && negotiated.ModifierNeedsFixation)
        {
            Span<byte> fixate = stackalloc byte[512];
            ReadOnlySpan<PixelFormat> fmt = [_format];
            ReadOnlySpan<long> chosen = [(long)negotiated.Modifier];
            int fl = SpaFormatPod.WriteVideoFormat(fixate, fmt,
                (uint)_width, (uint)_height, (uint)_frameRate, fixedSize: true,
                modifiers: chosen, fixateModifier: true);

            // Marked done only if the daemon took it, so a refusal is retried on the next
            // negotiation instead of leaving the stream unfixated and silent forever.
            if (core.RequestParamsFromCallback(fixate[..fl]) >= 0)
            {
                _modifierFixated = true;
                return; // a fresh param_changed will arrive with the fixated modifier
            }
        }

        // Declare dmabuf buffers: one block per plane, sized for the negotiated geometry. dataType is
        // DMA-BUF only - we are committing to hand over GPU buffers, not host memory.
        //
        // Sized from the negotiated format, matching the host-memory path. Sizing from the
        // constructor arguments describes an image the buffers do not hold whenever the daemon
        // settles on anything else.
        if (negotiated.Format == PixelFormat.Unknown || negotiated.Width <= 0 || negotiated.Height <= 0)
            return;

        int stride = SpaFormatPod.VideoStride(negotiated.Format, negotiated.Width);
        // Per block, not per image: with one block per plane this is the largest plane.
        int size = SpaFormatPod.VideoBlockSize(negotiated.Format, negotiated.Width, negotiated.Height);
        Span<byte> buffers = stackalloc byte[256];
        int bl = SpaFormatPod.WriteVideoBuffersParam(buffers, size, stride,
            dataTypes: 1 << (int)SpaDataType.DmaBuf, blocks: _planeCount,
            syncDataBlocks: _explicitSync ? SyncDataBlocks : 0);

        Span<byte> meta = stackalloc byte[64];
        int ml = SpaFormatPod.WriteHeaderMetaParam(meta);

        // The timeline meta rides a second pod: like the header it only takes effect when the peer
        // agrees, and the pool layout above already carries its two descriptors either way.
        if (!_explicitSync)
        {
            core.RequestParamsFromCallback(buffers[..bl], meta[..ml]);
            return;
        }

        Span<byte> syncMeta = stackalloc byte[64];
        int sml = SpaFormatPod.WriteSyncTimelineMetaParam(syncMeta);
        core.RequestParamsFromCallback(buffers[..bl], meta[..ml], syncMeta[..sml]);
    }

    // PipeWire allocated an (empty) buffer with _planeCount data blocks; back each block with one plane of
    // an app-owned dmabuf. We hand the app a stable per-buffer index (stored in pw_buffer.user_data) so it
    // can pair the buffer with a GPU surface it keeps for the buffer's lifetime.
    private unsafe void OnAddBuffer(pw_buffer* buf)
    {
        spa_buffer* sb = buf->buffer;
        if (sb is null) return;

        int index = _freeBufferIndices.Count > 0 ? _freeBufferIndices.Pop() : _nextBufferIndex++;

        // Bounded before anything is allocated for it: the table above is fixed, and an index past
        // it is a broken pool, not a bigger one. The buffer stays unbacked and PipeWire will not
        // use it, the same outcome as every other refusal below.
        if ((uint)index >= (uint)MaxPoolBuffers)
        {
            LogBufferIndexOutOfRange(index);
            return;
        }

        Span<VideoPlane> planes = stackalloc VideoPlane[MaxPlanes];
        int n;
        long acquireFd = -1, releaseFd = -1;
        try
        {
            // The sync variant backs planes and timelines together; without it the planes come
            // from the plain handler and any -1 below becomes a library eventfd further down.
            if (_explicitSync && AllocateDmaBufSync is { } allocateSync)
                n = allocateSync(
                    this, index, _width, _height, Format.Modifier, planes,
                    out acquireFd, out releaseFd);
            else
                n = AllocateDmaBuf?.Invoke(this, index, _width, _height, Format.Modifier, planes) ?? 0;

            // The handler's return value indexes the span above, and it is the application's
            // number rather than this library's. A larger one is a caller mistake, not a bigger
            // buffer.
            if (n > MaxPlanes) n = MaxPlanes;
        }
        catch
        {
            // The index goes back rather than being burned by a handler that failed. It is not
            // assigned to the buffer yet, so remove_buffer will never come to reclaim it.
            _freeBufferIndices.Push(index);
            throw;
        }

        // In explicit-sync mode the pool carries two extra datas past the planes, so the plane
        // requirement counts planes, not datas - and a pool shaped any other way does not match
        // the negotiated contract at all.
        if (_explicitSync && sb->n_datas != (uint)_planeCount + SyncDataBlocks)
        {
            LogPartialAllocation(index, 0, sb->n_datas);
            _freeBufferIndices.Push(index);
            return; // buffer stays unbacked and PipeWire will not use it
        }

        uint planeTotal = _explicitSync ? (uint)_planeCount : sb->n_datas;

        // Every block or none. A partial answer leaves the tail spa_data with no fd, and the
        // consumer then imports a descriptor of -1 and fails inside its driver with nothing here to
        // name.
        if (n <= 0 || (uint)n < planeTotal)
        {
            _freeBufferIndices.Push(index);
            if (n > 0) LogPartialAllocation(index, n, sb->n_datas);
            return; // buffer stays unbacked and PipeWire will not use it
        }

        // Assigned only now that the buffer really is backed: user_data is what marks it as ours,
        // and setting it before the allocation succeeded published an index for a buffer with no
        // memory behind it.
        buf->user_data = (void*)(nint)(index + 1); // +1 so 0 distinguishes "unassigned"

        var layout = new (uint Offset, int Stride)[planeTotal];

        for (uint i = 0; i < planeTotal; i++)
        {
            VideoPlane p = planes[(int)i];

            // The allocator's descriptor, unvalidated until here. A negative or out-of-range one
            // reaches the consumer's importer as a deep EINVAL with nothing to say where it came
            // from, and the capture side already refuses the same shape on the way in.
            if (p.Fd < 0 || p.Fd > int.MaxValue)
            {
                LogInvalidPlaneDescriptor(index, i, p.Fd);
                _freeBufferIndices.Push(index);
                buf->user_data = null;
                return;
            }

            spa_data* dd = &sb->datas[i];
            dd->type      = (uint)SpaDataType.DmaBuf;
            dd->flags     = SpaDataFlag.Readable;
            dd->fd        = (nint)p.Fd;
            dd->mapoffset = 0;
            dd->maxsize   = p.Size;
            dd->data      = null;       // dmabuf: consumer imports via fd, never a host pointer
            if (dd->chunk is not null)
            {
                dd->chunk->offset = p.Offset;
                dd->chunk->stride = p.Stride;
                dd->chunk->size   = p.Size;
            }

            layout[i] = (p.Offset, p.Stride);
        }

        // Sync timelines last: the planes above must already be valid, because a failure here
        // unwinds the whole buffer and the planes with it.
        if (_explicitSync && !AttachSyncTimelines(sb, index, ref acquireFd, ref releaseFd))
        {
            _freeBufferIndices.Push(index);
            buf->user_data = null;
            return;
        }

        // Published whole, after the last entry lands: a reader racing this sees the previous
        // array or this complete one, never a half-filled one.
        Volatile.Write(ref _planeLayouts[index], layout);
    }

    /// <summary>Attaches the acquire/release timeline descriptors to a backed buffer.</summary>
    /// <remarks>
    /// A -1 from the app becomes a library eventfd, closed automatically when the buffer goes;
    /// anything else is borrowed and must stay valid until <see cref="ReleaseDmaBuf"/> for the
    /// buffer, closed by the app. Either way the descriptors order the buffer, they never carry
    /// pixels. False only when the descriptors cannot be provided, and the buffer is declined.
    /// </remarks>
    private unsafe bool AttachSyncTimelines(spa_buffer* sb, int index, ref long acquireFd, ref long releaseFd)
    {
        bool acquireOwned = false, releaseOwned = false;
        try
        {
            if (acquireFd < 0) { acquireFd = Descriptors.CreateEventfd(); acquireOwned = true; }
            if (releaseFd < 0) { releaseFd = Descriptors.CreateEventfd(); releaseOwned = true; }
        }
        catch (IOException ex)
        {
            LogSyncFdFailed(index, ex.Message);
            if (acquireOwned) Descriptors.CloseEventfd((int)acquireFd);
            return false;
        }

        // An eventfd is a small int; anything else did not come from this process's table.
        if (!SyncFdUsable(acquireFd) || !SyncFdUsable(releaseFd))
        {
            LogInvalidSyncDescriptor(index, acquireFd, releaseFd);
            if (acquireOwned) Descriptors.CloseEventfd((int)acquireFd);
            if (releaseOwned) Descriptors.CloseEventfd((int)releaseFd);
            return false;
        }

        spa_data* acquire = &sb->datas[_planeCount];
        acquire->type = (uint)SpaDataType.SyncObj;
        acquire->fd = (nint)acquireFd;
        acquire->flags = 0;
        acquire->data = null;
        acquire->chunk = null;

        spa_data* release = &sb->datas[_planeCount + 1];
        release->type = (uint)SpaDataType.SyncObj;
        release->fd = (nint)releaseFd;
        release->flags = 0;
        release->data = null;
        release->chunk = null;

        lock (_syncGate)
        {
            _syncAcquireFds[index] = (int)acquireFd;
            _syncReleaseFds[index] = (int)releaseFd;
            _syncAcquireOwned[index] = acquireOwned;
            _syncReleaseOwned[index] = releaseOwned;
        }

        return true;
    }

    private static bool SyncFdUsable(long fd) => fd >= 0 && fd <= int.MaxValue;

    /// <summary>Closes library-owned timeline descriptors for a buffer going away.</summary>
    private void CloseSyncTimelines(int index)
    {
        lock (_syncGate)
        {
            if ((uint)index >= (uint)MaxPoolBuffers) return;
            if (_syncAcquireOwned[index]) Descriptors.CloseEventfd(_syncAcquireFds[index]);
            if (_syncReleaseOwned[index]) Descriptors.CloseEventfd(_syncReleaseFds[index]);
            _syncAcquireFds[index] = -1;
            _syncReleaseFds[index] = -1;
            _syncAcquireOwned[index] = false;
            _syncReleaseOwned[index] = false;
        }
    }

    private unsafe void OnRemoveBuffer(pw_buffer* buf)
    {
        int index = (int)(nint)buf->user_data - 1;
        if (index < 0) return;

        ReleaseDmaBuf?.Invoke(this, index);

        // App descriptors are borrowed and close here, by the app, inside its handler above;
        // library eventfds close here too, so nothing outlives the buffer either way.
        if (_explicitSync) CloseSyncTimelines(index);

        // Withdrawn before the index is recycled: a process callback already past the read keeps
        // the array it holds (safe - arrays are never mutated), and one arriving after sees none
        // and publishes nothing for the cycle.
        if ((uint)index < (uint)MaxPoolBuffers)
            Volatile.Write(ref _planeLayouts[index], null);

        // Returned to the pool so the next add_buffer reuses it rather than growing past the
        // consumer's allocation on every renegotiation.
        _freeBufferIndices.Push(index);
        buf->user_data = null;
    }

    private void OnState(PipeWireStreamState oldState, PipeWireStreamState newState) =>
        StateChanged?.Invoke(this, oldState, newState);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OnFormat modifier=0x{Modifier:x} needsFixation={NeedsFixation}")]
    private partial void LogOnFormat(ulong modifier, bool needsFixation);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OnPostFormat fixated={Fixated} needsFixation={NeedsFixation} planeCount={PlaneCount}")]
    private partial void LogOnPostFormat(bool fixated, bool needsFixation, int planeCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "buffer {Index} declined: the allocator backed {Backed} of {Needed} planes")]
    private partial void LogPartialAllocation(int index, int backed, uint needed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "buffer {Index} declined: plane {Plane} carries descriptor {Fd}")]
    private partial void LogInvalidPlaneDescriptor(int index, uint plane, long fd);

    [LoggerMessage(Level = LogLevel.Warning, Message = "buffer index {Index} is past the pool table; buffer stays unbacked")]
    private partial void LogBufferIndexOutOfRange(int index);

    [LoggerMessage(Level = LogLevel.Warning, Message = "buffer {Index} declined: timeline descriptors unavailable ({Reason})")]
    private partial void LogSyncFdFailed(int index, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "buffer {Index} declined: timeline descriptors {AcquireFd}/{ReleaseFd} are not usable")]
    private partial void LogInvalidSyncDescriptor(int index, long acquireFd, long releaseFd);

    /// <summary>Waits until the stream is negotiated and running.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// <c>Connect</c> issues a request; the daemon then negotiates a format over several round
    /// trips, and only then does the stream start. Without this a caller has to subscribe to
    /// <c>StateChanged</c> and drive its own completion, which is the same code every time.
    /// <para>
    /// Cancelling abandons the wait, not the stream: the connection stays up and keeps negotiating,
    /// because there is nothing to recall. Dispose it to stop it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not connected yet.</exception>
    /// <exception cref="PipeWireException">The stream reached its error state instead.</exception>
    public Task WaitForStreamingAsync(CancellationToken cancellationToken = default)
    {
        PipeWireStreamCore core = _core
            ?? throw new InvalidOperationException("Connect before waiting for the stream to start.");

        return core.WaitForStreamingAsync(cancellationToken);
    }

    /// <summary>Every control this stream exposes, as the daemon last reported them.</summary>
    /// <remarks>
    /// Empty until the stream is connected and the daemon has reported them, which happens during
    /// negotiation. A snapshot: the daemon re-reports a control whenever one of its values changes.
    /// </remarks>
    public ImmutableArray<PipeWireStreamControl> Controls =>
        _core?.Controls ?? [];

    /// <summary>One control by SPA property id, or null when the stream has not reported it.</summary>
    public PipeWireStreamControl? GetControl(uint id) => _core?.GetControl(id);

    /// <summary>Sets a control's values.</summary>
    /// <param name="id">The SPA property id, as carried by <see cref="PipeWireStreamControl.Id"/>.</param>
    /// <param name="values">
    /// One value for a scalar control, or one per channel. More than
    /// <see cref="PipeWireStreamControl.MaximumValues"/> is the daemon's to refuse, not this
    /// library's to guess at.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait for the loop lock.</param>
    /// <remarks>
    /// Sent as a <c>Props</c> object. The daemon applies it when it next runs the node, so this
    /// returning does not mean the value is in effect; read it back from <see cref="Controls"/> if
    /// that matters.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not connected yet.</exception>
    /// <exception cref="ArgumentException"><paramref name="values"/> is empty.</exception>
    public void SetControl(uint id, ReadOnlySpan<float> values, CancellationToken cancellationToken = default)
    {
        if (values.IsEmpty)
            throw new ArgumentException("a control needs at least one value.", nameof(values));

        PipeWireStreamCore core = _core
            ?? throw new InvalidOperationException("Connect before setting a control.");

        core.SetControl(id, values, cancellationToken);
    }
}
