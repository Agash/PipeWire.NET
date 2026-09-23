using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>
/// Publishes video frames TO PipeWire as a virtual camera. Two modes:
/// <list type="bullet">
/// <item>Host memory (default): PipeWire pulls frames by invoking <see cref="FillFrame"/>; write your
/// pixels into the supplied span.</item>
/// <item>Zero-copy dmabuf: call <see cref="ConnectDmaBuf(ReadOnlySpan{long}, CancellationToken)"/> with the DRM modifiers your GPU can export.
/// The library negotiates dmabuf buffers and asks you (via <see cref="AllocateDmaBuf"/>) to back each
/// pool buffer with a dmabuf you own; you render into it and publish from <see cref="FillDmaBuf"/>. No
/// pixel copy ever touches the CPU. <see cref="ConnectDmaBufSync(ReadOnlySpan{long}, CancellationToken)"/> adds explicit timeline
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
public sealed partial class PipeWireVideoOutput : IDisposable, IAsyncDisposable
{
    /// <summary>Return <see langword="true"/> to publish the frame.</summary>
    public delegate bool FillFrameHandler(
        PipeWireVideoOutput sender, Span<byte> pixels, int stride, int width, int height, PixelFormat format);

    /// <summary>
    /// Asks the app to back output pool buffer <paramref name="bufferIndex"/> with a dmabuf it owns: fill
    /// one <see cref="VideoPlane"/> per plane into <paramref name="planes"/> and return the plane count.
    /// Called once per pool buffer, on the loop thread, after dmabuf format negotiation -
    /// allocate your GPU surface (e.g. a Vulkan image exported to a dmabuf fd) for <paramref name="modifier"/> here.
    /// </summary>
    /// <param name="sender">The stream.</param>
    /// <param name="bufferIndex">The pool buffer, stable for its lifetime.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="modifier">The negotiated DRM format modifier.</param>
    /// <param name="device">
    /// The device the format was negotiated for - allocate on it - or null when the peer did not take
    /// part in device-ID negotiation, or the stream was connected with modifiers alone. Null is
    /// upstream's "device undefined": allocate where you would have without negotiation, which for a
    /// stream connected with device offers is the first one.
    /// </param>
    /// <param name="planes">Where to describe the buffer, one entry per plane.</param>
    /// <returns>How many planes were filled.</returns>
    /// <remarks>
    /// Back every buffer the pool asks for. The pool is sized by the consumer within the range this
    /// producer offers, 2 to 16 buffers, and a buffer left unbacked (returning 0, or fewer planes
    /// than the format has) is not skipped: the daemon rejects it and fails the allocation of the
    /// whole pool, so the stream never starts.
    /// </remarks>
    public delegate int AllocateDmaBufHandler(
        PipeWireVideoOutput sender, int bufferIndex, int width, int height, ulong modifier, DrmDevice? device,
        Span<VideoPlane> planes);

    /// <summary>
    /// Asks the app to render the current frame into pool buffer <paramref name="bufferIndex"/>'s dmabuf and
    /// return <see langword="true"/> to publish it (false to emit an empty frame). Called on the loop thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the frame counts as written depends on who orders access to the buffer:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Implicit sync (<see cref="ConnectDmaBuf(ReadOnlySpan{long}, CancellationToken)"/>): the kernel orders access through
    /// the fences attached to the DMA-BUF, so GPU work submitted here may still be running on return,
    /// as long as it was submitted against the buffer.</description></item>
    /// <item><description>Explicit sync with library-owned timelines (<see cref="ConnectDmaBufSync(ReadOnlySpan{long}, CancellationToken)"/>,
    /// timelines left at -1): the library signals the acquire point from the CPU as soon as this
    /// returns, so the frame must be completely written by then. Wait for your GPU work before
    /// returning, or use your own timelines.</description></item>
    /// <item><description>Explicit sync with your own timelines (<see cref="AllocateDmaBufSync"/>):
    /// the library never signals them. Stamp the point your submission will signal with
    /// <see cref="StampSyncPoints"/> and have the GPU signal it; this may return before the GPU
    /// has finished.</description></item>
    /// </list>
    /// </remarks>
    public delegate bool FillDmaBufHandler(PipeWireVideoOutput sender, int bufferIndex);

    /// <summary>
    /// Backs a pool buffer with an app-owned dmabuf plus explicit-sync timelines, for
    /// <see cref="ConnectDmaBufSync(ReadOnlySpan{long}, CancellationToken)"/>. Like <see cref="AllocateDmaBufHandler"/>, plus the two
    /// timeline descriptors: set <paramref name="acquireFd"/> and <paramref name="releaseFd"/> to
    /// the app's own DRM syncobj timeline descriptors, or leave either at -1 for a library-created one.
    /// </summary>
    /// <param name="sender">The stream.</param>
    /// <param name="bufferIndex">The pool buffer, stable for its lifetime.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    /// <param name="modifier">The negotiated DRM format modifier.</param>
    /// <param name="device">The negotiated device, or null; as for <see cref="AllocateDmaBufHandler"/>.</param>
    /// <param name="planes">Where to describe the buffer, one entry per plane.</param>
    /// <param name="acquireFd">The acquire timeline's descriptor, or -1 for a library-created one.</param>
    /// <param name="releaseFd">The release timeline's descriptor, or -1 for a library-created one.</param>
    /// <returns>How many planes were filled.</returns>
    /// <remarks>
    /// <para>
    /// An app descriptor is borrowed: it must stay valid until <see cref="ReleaseDmaBuf"/> for the
    /// buffer, and the app closes it. It must be a DRM syncobj - what <c>SPA_DATA_SyncObj</c> means -
    /// or an eventfd, the stand-in upstream's video-src-sync example uses; anything else declines
    /// the buffer. A -1 becomes a library-created syncobj timeline, destroyed automatically when the
    /// buffer goes. Either way the descriptors order the buffer, they never carry pixels.
    /// </para>
    /// <para>
    /// Who signals the acquire point follows from who made the timeline: the library signals the
    /// ones it created, after <see cref="FillDmaBuf"/> returns; yours it never touches. See
    /// <see cref="FillDmaBufHandler"/>.
    /// </para>
    /// </remarks>
    public delegate int AllocateDmaBufSyncHandler(
        PipeWireVideoOutput sender, int bufferIndex, int width, int height, ulong modifier, DrmDevice? device,
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

    /// <summary>Handles a connection state change on the loop thread.</summary>
    public delegate void StateChangedHandler(
        PipeWireVideoOutput sender, PipeWireStreamState oldState, PipeWireStreamState newState);

    /// <summary>Raised on the loop thread when the connection state changes.</summary>
    public event StateChangedHandler? StateChanged;

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

    // The devices offered through the device-ID overloads, in preference order; empty when the stream
    // was connected with modifiers alone, which also means no Capability param was sent.
    private DmaBufDeviceOffer[] _deviceOffers = [];
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

    // DRM syncobj handles for each buffer's timelines: created with the timeline when the library
    // owns it, imported from the app's descriptor otherwise. 0 means the descriptor is not a
    // syncobj but an app eventfd (upstream video-src-sync's stand-in), confirmed when the buffer was
    // backed; a descriptor of neither kind never gets this far.
    private readonly uint[] _syncAcquireHandles = new uint[MaxPoolBuffers];
    private readonly uint[] _syncReleaseHandles = new uint[MaxPoolBuffers];

    // How long a consumer's promised release is waited for before the cycle publishes nothing.
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(1);

    private static int[] ClosedFds()
    {
        var fds = new int[MaxPoolBuffers];
        Array.Fill(fds, -1);
        return fds;
    }

    /// <summary>Sync timeline descriptors per buffer: acquire first, release second.</summary>
    private const int SyncDataBlocks = SpaFormatPod.SyncTimelineDataBlocks;

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
    public const uint AnyNode = NativeConstants.PW_ID_ANY;

    /// <summary>Connects, publishing into a node you already hold.</summary>
    /// <param name="target">The consumer to publish into, from a graph snapshot.</param>
    /// <param name="autoConnect">Let the session manager route this stream.</param>
    /// <param name="driver">
    /// Take the driver role, so cycles happen when this stream asks for them. The daemon still
    /// decides: <see cref="IsDriving"/> says whether it did.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait for the loop lock.</param>
    /// <remarks>
    /// The same as the id overload, for a caller holding the node rather than its id - which is
    /// what a graph snapshot hands out. Both captures have had this shape all along.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    public void Connect(
        PipeWireNode target,
        bool autoConnect = true,
        bool driver = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        Connect(target.NodeId, autoConnect: autoConnect, driver: driver,
            cancellationToken: cancellationToken);
    }

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
    /// <param name="driver">
    /// Ask to be the graph's driver (<c>PW_STREAM_FLAG_DRIVER</c>), so cycles happen when this stream
    /// triggers them rather than on another node's clock - upstream's video-src and audio-src connect
    /// this way to pace their own output. Pair with <see cref="DriveAt"/> or
    /// <see cref="TriggerProcess"/>. The daemon still decides: <see cref="IsDriving"/> says whether it
    /// did. Without it the stream is only ever a follower, and a trigger just asks the real driver
    /// for a cycle.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the wait for the loop lock. The connect request itself is issued
    /// synchronously once that is held, so there is nothing to recall after it.
    /// </param>
    public unsafe void Connect(
        uint targetNodeId = AnyNode,
        string? targetObjectName = null,
        bool autoConnect = true,
        bool driver = false,
        CancellationToken cancellationToken = default)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");

        var props = new StreamProperties(StreamMediaType.Video, StreamCategory.Playback)
            .WithRole("Camera")
            .WithNodeName(_name);

        // After the stream's own defaults, so an explicit media.role or node.description from the
        // caller wins rather than being silently ignored. An explicit method argument still beats
        // both, which is why target.object is applied below this.
        if (ExtraProperties is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> kv in ExtraProperties)
                props.With(kv.Key, kv.Value);
        }

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
        if (driver) flags |= PipeWireStreamFlags.Driver;

        try
        {
            core.Connect(SpaDirection.Output, targetNodeId, flags,
            pod[..len],
            cancellationToken: cancellationToken);
            _core = core;

            // Set here, not when a handler subscribes: a caller that subscribed before connecting
            // would otherwise never be hooked up, and the events it was waiting for would pass silently.
            core.OnCommand = RaiseCommand;
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
    /// and back buffers with <see cref="AllocateDmaBuf"/> while the library creates DRM syncobj
    /// timelines for them. Either way stamp per-frame points with <see cref="StampSyncPoints"/>;
    /// unstamped frames carry a running sequence instead. Who signals the acquire point, and so
    /// when a frame must be finished, is set out on <see cref="FillDmaBufHandler"/>.
    /// </para>
    /// <para>
    /// A consumer that agrees stops attaching implicit fences, so a peer ignoring the points
    /// races the GPU. Use a dedicated context: the release wait blocks the loop thread when a
    /// consumer promised to signal and has not yet, which stalls every stream on a shared one.
    /// </para>
    /// </remarks>
    public void ConnectDmaBufSync(
        ReadOnlySpan<long> modifiers, CancellationToken cancellationToken = default)
    {
        RequireSyncTimelines();

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
    /// <see cref="ConnectDmaBufSync(ReadOnlySpan{long}, CancellationToken)"/>, negotiating which device
    /// the buffers are allocated on.
    /// </summary>
    /// <param name="offers">The devices this producer can allocate on, each with its modifiers, in priority order.</param>
    /// <param name="cancellationToken">Abandons the wait for the loop lock.</param>
    /// <remarks>
    /// Device negotiation as in <see cref="ConnectDmaBuf(ReadOnlySpan{DmaBufDeviceOffer}, CancellationToken)"/>;
    /// explicit sync as in the modifier overload.
    /// </remarks>
    public void ConnectDmaBufSync(
        ReadOnlySpan<DmaBufDeviceOffer> offers, CancellationToken cancellationToken = default)
    {
        RequireSyncTimelines();

        _explicitSync = true;
        try
        {
            ConnectDmaBuf(offers, cancellationToken);
        }
        catch
        {
            _explicitSync = false;
            throw;
        }
    }

    /// <summary>Explicit sync is DRM syncobj timelines, and those need a DRM device.</summary>
    /// <remarks>
    /// Checked before connecting: without one every buffer would be declined at allocation, after
    /// negotiation had already settled on it.
    /// </remarks>
    private void RequireSyncTimelines()
    {
        if (!DrmSyncobj.IsAvailable && AllocateDmaBufSync is null)
            throw new InvalidOperationException(
                "explicit sync needs a DRM render node (/dev/dri/renderD*) to create syncobj timelines, "
                + "and none could be opened; supply timelines through AllocateDmaBufSync or use ConnectDmaBuf");
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
    public void ConnectDmaBuf(
        ReadOnlySpan<long> modifiers, CancellationToken cancellationToken = default)
    {
        if (modifiers.IsEmpty) throw new ArgumentException("At least one DRM modifier must be offered.", nameof(modifiers));

        ConnectDmaBufCore(modifiers, [], cancellationToken);
    }

    /// <summary>
    /// Starts publishing zero-copy dmabuf frames, negotiating which device the buffers are allocated on
    /// as well as the modifier.
    /// </summary>
    /// <param name="offers">
    /// The devices this producer can allocate on, each with the modifiers it can export there, in
    /// priority order.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the wait for the loop lock. The connect request itself is issued
    /// synchronously once that is held, so there is nothing to recall after it.
    /// </param>
    /// <remarks>
    /// <para>
    /// PipeWire's DMA-BUF device-ID negotiation (<c>PW_CAPABILITY_DEVICE_ID_NEGOTIATION</c>), as
    /// upstream's video-src-fixate does it: the stream connects inactive with a Capability param naming
    /// these devices, and once the peer's capabilities arrive it offers one format per device and
    /// activates. A consumer that negotiates too settles on one of them;
    /// <see cref="NegotiatedDevice"/> says which, and <see cref="AllocateDmaBuf"/> is handed it.
    /// </para>
    /// <para>
    /// A consumer that does not negotiate - every one that predates the protocol - is offered the first
    /// device's modifiers without a device, and the stream behaves exactly as the modifier overload
    /// would; <see cref="NegotiatedDevice"/> stays null.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// No offers, an offer without modifiers, or a device offered twice.
    /// </exception>
    public void ConnectDmaBuf(
        ReadOnlySpan<DmaBufDeviceOffer> offers, CancellationToken cancellationToken = default)
    {
        DeviceIdNegotiation.Validate(offers, nameof(offers));

        ConnectDmaBufCore(offers[0].Modifiers.AsSpan(), offers, cancellationToken);
    }

    private unsafe void ConnectDmaBufCore(
        ReadOnlySpan<long> modifiers, ReadOnlySpan<DmaBufDeviceOffer> offers, CancellationToken cancellationToken)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");

        _dmaBufMode = true;
        _modifierFixated = false;
        _planeCount = SpaFormatPod.PlaneCount(_format);

        var props = new StreamProperties(StreamMediaType.Video, StreamCategory.Playback)
            .WithRole("Camera")
            .WithNodeName(_name);

        // After the stream's own defaults, so an explicit media.role or node.description from the
        // caller wins rather than being silently ignored. An explicit method argument still beats
        // both, which is why target.object is applied below this.
        if (ExtraProperties is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> kv in ExtraProperties)
                props.With(kv.Key, kv.Value);
        }

        // add_buffer/remove_buffer let us back each pool buffer with an app-owned dmabuf; the producer
        // supplies the memory, so we use ALLOC_BUFFERS (and NOT MAP_BUFFERS - there is nothing to mmap).
        _modifiers = modifiers.ToArray();
        _deviceOffers = offers.ToArray();
        _announcedToAPeer = false;
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

        // Device negotiation is announced, not assumed: the Capability param names the devices this
        // producer can allocate on, as video-src-fixate's SUPPORT_DEVICE_IDS_LIST build does.
        byte[] capability = [];
        if (!offers.IsEmpty)
        {
            Span<DrmDevice> devices = new DrmDevice[offers.Length];
            for (int i = 0; i < offers.Length; i++) devices[i] = offers[i].Device;
            capability = DeviceIdNegotiation.CapabilityParam(devices);
        }

        // Built before the core exists, so a pod that cannot be written does not leave a native
        // stream behind: the failure path below only runs once there is something to dispose.
        var core = new PipeWireStreamCore(_ctx, props, _name, OnBuffer, OnState, OnFormat, OnPostFormat,
            OnAddBuffer, OnRemoveBuffer, OnPeerConnected);

        try
        {
            // Deliberately not PW_STREAM_FLAG_DRIVER. The consumer drives the graph clock, and
            // claiming the driver role stops frames reaching the consumer entirely.
            core.Connect(SpaDirection.Output, NativeConstants.PW_ID_ANY,
            PipeWireStreamFlags.Inactive | PipeWireStreamFlags.AllocBuffers,
            pod[..len],
            capabilityPod: capability,
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
    private unsafe void OnPeerConnected(PipeWireStreamCore core, spa_pod* param)
    {
        // Once, not once per peer. The daemon reports PeerCapability for every consumer that links,
        // and re-announcing the EnumFormat restarts negotiation, so a second consumer joining would
        // renegotiate the format underneath the first one mid-stream. The announce exists to get an
        // INACTIVE producer going; after that there is nothing to do.
        if (_announcedToAPeer) return;
        _announcedToAPeer = true;

        int rc;
        if (_deviceOffers.Length > 0)
        {
            // video-src-fixate's on_stream_peer_capability_changed: one format per device when the
            // peer negotiates, the first device's modifiers without a device when it does not. No
            // host-memory format: this producer only ever hands over DMA-BUFs.
            PeerCapabilities peer = DeviceIdNegotiation.Parse(param);
            byte[] pods = DeviceIdNegotiation.WriteDeviceFormats(peer, _deviceOffers,
                _format, (uint)_width, (uint)_height, (uint)_frameRate, fixedSize: true,
                hostMemoryFallback: false, out int count, out int deviceFormats);
            LogDeviceOffers(peer.NegotiatesDeviceIds, deviceFormats, _deviceOffers.Length);
            rc = core.RequestParamsFromCallback(pods, count);
        }
        else
        {
            byte[] pod = new byte[ModifierPodBytes(_modifiers.Length)];
            ReadOnlySpan<PixelFormat> fmt = [_format];
            int len = SpaFormatPod.WriteVideoFormat(pod, fmt,
                (uint)_width, (uint)_height, (uint)_frameRate, fixedSize: true, modifiers: _modifiers);
            rc = core.RequestParamsFromCallback(pod[..len]);
        }

        if (rc < 0) LogAnnounceRefused(rc);
        core.SetActiveFromCallback(true);
    }

    /// <summary>
    /// The DRM device the buffers were negotiated for, or null when there was no device negotiation.
    /// </summary>
    /// <remarks>
    /// Null until a format is settled; null for a stream connected with modifiers alone; and null when
    /// the consumer does not take part in device-ID negotiation, which is upstream's "device
    /// undefined". Read on any thread; <see cref="AllocateDmaBuf"/> is handed the same value.
    /// </remarks>
    public DrmDevice? NegotiatedDevice => DeviceIdNegotiation.Resolve(Format.DeviceId, _deviceOffers);

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
    /// No-op before <see cref="Connect(uint, string?, bool, bool, CancellationToken)"/> or <see cref="ConnectDmaBuf(ReadOnlySpan{long}, CancellationToken)"/>, and after disposal.
    /// </para>
    /// </remarks>
    public void TriggerProcess() => _core?.TriggerProcess();

    /// <summary>
    /// Offers the consumer a different format mid-stream, and lets it pick.
    /// </summary>
    /// <param name="formats">The pixel formats to offer, in priority order.</param>
    /// <param name="width">The width to offer.</param>
    /// <param name="height">The height to offer.</param>
    /// <param name="frameRate">The frame rate to offer, in frames per second.</param>
    /// <param name="fixedSize">
    /// <see langword="true"/> to demand exactly this geometry, <see langword="false"/> to offer a
    /// range around it and let the consumer fixate within it. A producer whose source has genuinely
    /// changed size demands; one that can scale offers.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the stream is not in a state to renegotiate, or the daemon
    /// refused the offer outright. A <see langword="true"/> means the offer went out - whether the
    /// consumer accepts it is the consumer's business, and the answer arrives as a format change.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The producer half of renegotiation, matching upstream's <c>video-src-reneg</c> and
    /// <c>video-src-fixate</c>. A capture source whose window is resized, or a camera that switched
    /// mode, has to be able to say so without tearing the stream down and losing its consumer.
    /// </para>
    /// <para>
    /// The new geometry is not applied locally. This publishes an offer; the pool is rebuilt only
    /// once the peer settles on a format, so a caller must keep serving the old size until it sees
    /// the change rather than assuming this took effect.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width"/> or <paramref name="height"/> is not positive, or
    /// <paramref name="frameRate"/> is negative.
    /// </exception>
    public bool RequestFormat(
        ReadOnlySpan<PixelFormat> formats, int width, int height, int frameRate = 30,
        bool fixedSize = false)
    {
        if (_core is null || formats.IsEmpty) return false;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(frameRate);

        Span<byte> pod = stackalloc byte[1024];
        int len = SpaFormatPod.WriteVideoFormat(
            pod, formats, (uint)width, (uint)height, (uint)frameRate, fixedSize);

        return _core.RequestFormats(pod[..len]) >= 0;
    }

    /// <summary>Whether this stream is driving the graph.</summary>
    /// <remarks>
    /// True only when the stream was connected as a driver and the daemon chose it as the one.
    /// <see cref="TriggerProcess"/> does nothing unless this is true - upstream routes the request
    /// to whichever node actually drives, and a node that does not implement it answers with an
    /// error per call - so a producer that paces its own output should ask before assuming it can.
    /// Whether a stream drives is the daemon's answer and depends on the rest of the graph, so it
    /// can change after connecting.
    /// </remarks>
    public bool IsDriving => _core?.IsDriving ?? false;

    /// <summary>The last exception a callback of this stream threw, or null if none has.</summary>
    /// <remarks>
    /// A callback cannot let an exception reach its native caller, so one is recorded here rather
    /// than thrown: a handler that throws otherwise looks exactly like a handler that did nothing.
    /// It is not logged either, because these run on the realtime thread where logging is itself an
    /// xrun - read it from your own non-realtime loop. <see cref="ProcessErrorCount"/> says how many
    /// there have been, which separates "threw once" from "throws every cycle".
    /// </remarks>
    public Exception? LastProcessError => _core?.ProcessFaults.Last;

    /// <summary>How many times a callback of this stream has thrown.</summary>
    public long ProcessErrorCount => _core?.ProcessFaults.Count ?? 0;

    /// <summary>Puts this stream into the error state and tells the daemon why.</summary>
    /// <param name="result">A negative errno describing the failure.</param>
    /// <param name="message">What went wrong, for logs and for the peer.</param>
    /// <param name="cancellationToken">Abandons the wait for the loop lock.</param>
    /// <remarks>
    /// What to call when a callback cannot do what the graph asked - a format that cannot be
    /// carried, a buffer that cannot be filled. A stream that fails and stays quiet leaves its peer
    /// waiting on a cycle that will not come, and nothing in the graph says why; this library does
    /// the same thing itself when a format handler throws.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public void SetError(int result, string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _core?.SetError(result, message, cancellationToken);
    }

    /// <summary>
    /// This stream's own node in the graph, or <see langword="null"/> until it is connected.
    /// </summary>
    /// <remarks>
    /// A stream is a node like any other, so this is the handle for routing it:
    /// <c>graph.GetPortsForNode(await stream.WaitForNodeIdAsync())</c> finds its ports, which can then
    /// be linked. Awaited rather than read, because the id only exists once the daemon has bound the
    /// stream, which is after <c>Connect</c> returns.
    /// </remarks>
    public uint? NodeId
    {
        get
        {
            uint id = _core?.NodeId ?? NativeConstants.PW_ID_ANY;
            return id == NativeConstants.PW_ID_ANY ? null : id;
        }
    }


    /// <summary>
    /// Paces the graph from the stream's own data loop, driving one cycle per interval.
    /// </summary>
    /// <param name="interval">The period. <see cref="TimeSpan.Zero"/> stops the pacing.</param>
    /// <returns>True when the timer was armed or disarmed as asked.</returns>
    /// <remarks>
    /// Only meaningful for a stream the daemon has made the driver - see <see cref="IsDriving"/>.
    /// The timer fires on the data loop, so the cycle it triggers costs no thread hop; driving from
    /// a caller's own thread works too, but paces against that thread's scheduling instead.
    /// </remarks>
    public bool DriveAt(TimeSpan interval) => _core?.DriveAt(interval) ?? false;

    /// <summary>
    /// Extra node properties to set when connecting, on top of the ones this stream sets itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Session managers, routing rules and anything showing the user a list of streams read these,
    /// so the tags are how a stream explains itself to the rest of the system. <c>pw-cat</c> sets
    /// <c>media.title</c>, <c>media.artist</c> and the rest for exactly that reason.
    /// </para>
    /// <para>
    /// Applied at connect, and a key set here wins over this stream's own default for that key -
    /// which is the point, since overriding <c>media.role</c> or <c>node.description</c> is the
    /// usual reason to reach for this. To change a tag on a stream that is already running, use
    /// <c>UpdateProperties</c> instead; some keys, <c>target.object</c> among them, are only read
    /// when the connection is made.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string>? ExtraProperties { get; set; }

    /// <summary>How much this stream currently holds, or null when it cannot be read.</summary>
    /// <remarks>
    /// The error term for a rate controller. Pair it with <see cref="PipeWireRateController"/> to
    /// close the loop the way upstream's tunnels do.
    /// </remarks>
    public PipeWireStreamQueue? Queue => _core?.Queue;

    /// <summary>Whether the daemon has put this stream in lazy scheduling.</summary>
    public bool IsLazy => _core?.IsLazy ?? false;

    /// <summary>Adds or replaces properties on the live stream.</summary>
    /// <param name="properties">The keys to set. An empty value removes a key.</param>
    /// <returns>The number of properties actually changed.</returns>
    public int UpdateProperties(IReadOnlyDictionary<string, string> properties) =>
        _core?.UpdateProperties(properties) ?? 0;

    /// <summary>Raised on the loop thread when the daemon sends this stream's node a command.</summary>
    /// <remarks>
    /// Suspend, Flush, Drain and RequestProcess all arrive here. A state change tells you the
    /// stream stopped streaming; this tells you why.
    /// </remarks>
    public event Action<SpaNodeCommand>? CommandReceived
    {
        add => _commandReceived += value;
        remove => _commandReceived -= value;
    }

    private Action<SpaNodeCommand>? _commandReceived;

    private void RaiseCommand(SpaNodeCommand command) => _commandReceived?.Invoke(command);

    /// <inheritdoc/>
    /// <remarks>
    /// Disposal here does no awaiting, so this and <see cref="DisposeAsync"/> do the same work.
    /// Both exist so that a caller is not forced into one idiom by which type they happen to hold.
    /// </remarks>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // Library timelines first: the pool may go without remove_buffer for every buffer, in which
        // case nothing else would release them. App descriptors are borrowed and stay the app's.
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
                    ClearSyncSlot(i);
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

    /// <summary>
    /// The presentation time to stamp the next published frame with, in nanoseconds on
    /// CLOCK_MONOTONIC, instead of the current stream time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For republishing media that was timed somewhere else. A frame that arrived over a network
    /// transport already has a presentation time, and stamping it with the local cycle time throws
    /// that away - audio and video then get re-timed independently at whatever cycle each happens
    /// to land in, which is precisely the alignment the sender went to the trouble of carrying.
    /// </para>
    /// <para>
    /// Set it from inside the fill callback, for the frame being written, and it applies to that
    /// frame only: it is cleared once stamped, so a cycle that does not set one falls back to the
    /// current stream time (<c>pw_stream_get_nsec</c>), which is what upstream's video-src stamps.
    /// A capture source publishing its own frames wants that fallback and should leave this alone.
    /// </para>
    /// <para>
    /// The value has to be on CLOCK_MONOTONIC to line up with anything else in the graph: that is
    /// the clock <c>pw_stream_get_nsec</c> reads and driver nodes publish the graph clock in, and on
    /// Linux <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> reads it in nanoseconds. Not
    /// <see cref="GraphClock"/>: a video graph driven by a stream only has an advancing graph clock
    /// if that stream writes one, which GStreamer's pipewiresink, for instance, does not do for
    /// video. Stamp the matching audio with <see cref="PipeWireAudioOutput.NextPresentationTimestampNs"/> so a
    /// consumer can align the two on the header timestamps.
    /// </para>
    /// </remarks>
    public long? NextPresentationTimestampNs { get; set; }

    /// <summary>Stamps the buffer's header meta with the frame's presentation time.</summary>
    /// <remarks>
    /// Both producer paths request SPA_META_Header, so a frame published without one carries no
    /// presentation time and every consumer reading it gets -1. The default is
    /// <c>pw_stream_get_nsec()</c>, the current CLOCK_MONOTONIC time, which is what upstream's
    /// video-src stamps. It is not the graph clock: when this stream drives, the graph clock only
    /// advances because this library writes it before each trigger, and a video graph driven by
    /// anything that does not - GStreamer's pipewiresink, for one - leaves it frozen. A caller with
    /// media timestamps of its own supplies them through <see cref="NextPresentationTimestampNs"/>.
    /// </remarks>
    private unsafe void WritePresentationTime(pw_buffer* buf, in PipeWireStreamCore.StreamClock clock)
    {
        _ = clock;

        // Taken, not just read: a supplied time applies to one frame, so the next cycle falls back
        // to the stream clock unless the caller sets it again.
        long? supplied = NextPresentationTimestampNs;
        if (supplied is not null) NextPresentationTimestampNs = null;

        PipeWireStreamCore.StampPresentationTime(buf, supplied ?? _core?.NowNs() ?? -1);
    }

    /// <summary>Waits for the release point a consumer promised, on a syncobj or on the eventfd stand-in.</summary>
    /// <remarks>
    /// The kind was settled when the buffer was backed: a handle means a syncobj, and a buffer whose
    /// descriptor was neither kind was declined then, so no handle here means a confirmed eventfd.
    /// Either way the wait has a deadline and a failure is a failure - never a release.
    /// </remarks>
    private SyncWait WaitRelease(int index, int releaseFd, ulong point)
    {
        uint handle = (uint)index < (uint)MaxPoolBuffers ? Volatile.Read(ref _syncReleaseHandles[index]) : 0;
        if (handle != 0) return DrmSyncobj.Wait(handle, point, ReleaseTimeout);

        return Descriptors.WaitEventfd(releaseFd, ReleaseTimeout);
    }

    /// <summary>Marks a frame ready on its acquire timeline, when the library owns that timeline.</summary>
    /// <remarks>
    /// Only the library's own timelines are signalled here, and only after the frame has been filled,
    /// which is why <see cref="FillDmaBufHandler"/> must have finished writing when it returns in that
    /// mode. A timeline the app supplied, syncobj or eventfd, is the app's to signal: it may be
    /// rendering on the GPU, and a signal from here would declare the frame ready before the GPU had
    /// written it.
    /// </remarks>
    private void SignalAcquire(int index, ulong point)
    {
        if ((uint)index >= (uint)MaxPoolBuffers || !_syncAcquireOwned[index]) return;

        uint handle = Volatile.Read(ref _syncAcquireHandles[index]);
        if (!DrmSyncobj.Signal(handle, point)) LogAcquireSignalFailed(index, point);
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
            && (((SpaMetaSyncTimelineFlags)(*sync).flags & SpaMetaSyncTimelineFlags.UnscheduledRelease) == 0)
            && WaitRelease(index, waitReleaseFd, (*sync).release_point) is { Reached: false } missed)
        {
            // Promised and not delivered. Rendering now would overwrite a frame still being read,
            // and waiting on would wedge every stream on this context; so this cycle publishes
            // nothing, and the buffer is waited for again the next time it comes round. The meta is
            // left as the consumer left it, which is what makes that retry correct.
            if (missed.Outcome == SyncWaitOutcome.TimedOut)
                LogReleaseTimedOut(index, (*sync).release_point, ReleaseTimeout.TotalMilliseconds);
            else
                LogReleaseFailed(index, (*sync).release_point, missed.Errno);
            return;
        }

        bool publish = FillDmaBuf?.Invoke(this, index) ?? false;

        if (syncReady)
        {
            // Fresh every cycle: the flag re-arms the promise protocol, the points are this
            // frame's, and the acquire signal releases the consumer's wait. Stamped even for a
            // declined frame - the timeline must keep moving, and an unsignalled acquire would
            // wedge a consumer waiting on it.
            (*sync).flags = (uint)SpaMetaSyncTimelineFlags.UnscheduledRelease;
            (*sync).acquire_point = acquirePoint;
            (*sync).release_point = releasePoint;
            SignalAcquire(index, acquirePoint);
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
            // The device goes with it: every format a negotiating peer offers names one, mandatory,
            // so a fixation without it would match nothing (video-src-fixate's fixate_format).
            int fl = SpaFormatPod.WriteVideoFormat(fixate, fmt,
                (uint)_width, (uint)_height, (uint)_frameRate, fixedSize: true,
                modifiers: chosen, fixateModifier: true, deviceId: negotiated.DeviceId);

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
        // it is a broken pool, not a bigger one. The buffer stays unbacked, the same outcome as every
        // other refusal below - and an unbacked buffer is not skipped: its datas keep the dataType
        // mask as their type, which client-node.c (do_port_use_buffers) rejects as an invalid memory
        // type, failing the allocation of the whole pool ("Buffer allocation failed").
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
            // from the plain handler and any -1 below becomes a library-created syncobj timeline.
            DrmDevice? device = NegotiatedDevice;
            if (_explicitSync && AllocateDmaBufSync is { } allocateSync)
                n = allocateSync(
                    this, index, _width, _height, Format.Modifier, device, planes,
                    out acquireFd, out releaseFd);
            else
                n = AllocateDmaBuf?.Invoke(this, index, _width, _height, Format.Modifier, device, planes) ?? 0;

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
            return; // unbacked: the whole pool fails, see the bound above
        }

        uint planeTotal = _explicitSync ? (uint)_planeCount : sb->n_datas;

        // Every block or none. A partial answer leaves the tail spa_data with no fd, and the
        // consumer then imports a descriptor of -1 and fails inside its driver with nothing here to
        // name.
        if (n <= 0 || (uint)n < planeTotal)
        {
            _freeBufferIndices.Push(index);
            if (n > 0) LogPartialAllocation(index, n, sb->n_datas);
            else LogBufferDeclined(index);
            return; // unbacked: the whole pool fails, see the bound above
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
            dd->flags     = (uint)SpaDataFlags.Readable;
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
    /// A -1 from the app becomes a library-created DRM syncobj timeline, released automatically when
    /// the buffer goes; anything else is borrowed and must stay valid until
    /// <see cref="ReleaseDmaBuf"/> for the buffer, closed by the app. Either way the descriptors order
    /// the buffer, they never carry pixels. False only when the timelines cannot be provided, and the
    /// buffer is declined.
    /// </remarks>
    private unsafe bool AttachSyncTimelines(spa_buffer* sb, int index, ref long acquireFd, ref long releaseFd)
    {
        bool acquireOwned = false, releaseOwned = false;
        uint acquireHandle, releaseHandle;

        if (acquireFd < 0)
        {
            (acquireHandle, int fd) = DrmSyncobj.Create();
            acquireFd = fd;
            acquireOwned = true;
        }
        else
        {
            acquireHandle = AppTimeline(acquireFd);
        }

        if (releaseFd < 0)
        {
            (releaseHandle, int fd) = DrmSyncobj.Create();
            releaseFd = fd;
            releaseOwned = true;
        }
        else
        {
            releaseHandle = AppTimeline(releaseFd);
        }

        // An app descriptor must be a timeline this process can wait on or leave for the app to
        // signal: a syncobj that imports, or a confirmed eventfd. Anything else is refused here,
        // with the buffer, rather than discovered as a failed wait on every cycle.
        bool acquireUsable = SyncFdUsable(acquireFd) && (acquireHandle != 0 || (!acquireOwned && IsAppEventfd(acquireFd)));
        bool releaseUsable = SyncFdUsable(releaseFd) && (releaseHandle != 0 || (!releaseOwned && IsAppEventfd(releaseFd)));

        if (!acquireUsable || !releaseUsable)
        {
            LogInvalidSyncDescriptor(index, acquireFd, releaseFd);
            DrmSyncobj.Destroy(acquireHandle);
            DrmSyncobj.Destroy(releaseHandle);
            if (acquireOwned) Descriptors.CloseDescriptor((int)acquireFd);
            if (releaseOwned) Descriptors.CloseDescriptor((int)releaseFd);
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
            _syncAcquireHandles[index] = acquireHandle;
            _syncReleaseHandles[index] = releaseHandle;
            _syncAcquireOwned[index] = acquireOwned;
            _syncReleaseOwned[index] = releaseOwned;
        }

        return true;
    }

    private static bool SyncFdUsable(long fd) => fd >= 0 && fd <= int.MaxValue;

    /// <summary>Imports an app's timeline descriptor when it is a syncobj; 0 otherwise.</summary>
    private static uint AppTimeline(long fd) => SyncFdUsable(fd) ? DrmSyncobj.Import((int)fd) : 0;

    private static bool IsAppEventfd(long fd) => SyncFdUsable(fd) && Descriptors.IsEventfd((int)fd);

    /// <summary>Releases a buffer's timelines when it goes away.</summary>
    private void CloseSyncTimelines(int index)
    {
        lock (_syncGate)
        {
            if ((uint)index >= (uint)MaxPoolBuffers) return;
            ClearSyncSlot(index);
        }
    }

    /// <summary>
    /// Destroys a slot's syncobj handles and closes the descriptors the library owns. Under
    /// <c>_syncGate</c>. Every handle is this process's to destroy, imported or created; a descriptor
    /// the app supplied stays the app's.
    /// </summary>
    private void ClearSyncSlot(int i)
    {
        DrmSyncobj.Destroy(_syncAcquireHandles[i]);
        DrmSyncobj.Destroy(_syncReleaseHandles[i]);
        if (_syncAcquireOwned[i]) Descriptors.CloseDescriptor(_syncAcquireFds[i]);
        if (_syncReleaseOwned[i]) Descriptors.CloseDescriptor(_syncReleaseFds[i]);
        _syncAcquireHandles[i] = 0;
        _syncReleaseHandles[i] = 0;
        _syncAcquireFds[i] = -1;
        _syncReleaseFds[i] = -1;
        _syncAcquireOwned[i] = false;
        _syncReleaseOwned[i] = false;
    }

    private unsafe void OnRemoveBuffer(pw_buffer* buf)
    {
        int index = (int)(nint)buf->user_data - 1;
        if (index < 0) return;

        ReleaseDmaBuf?.Invoke(this, index);

        // App descriptors are borrowed and close here, by the app, inside its handler above;
        // library timelines are released here too, so nothing outlives the buffer either way.
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "buffer {Index}: signalling acquire point {Point} on the library's timeline was refused; a consumer waiting on it times out")]
    private partial void LogAcquireSignalFailed(int index, ulong point);

    [LoggerMessage(Level = LogLevel.Warning, Message = "buffer {Index}: the consumer promised release point {Point} and did not signal it within {TimeoutMs}ms; the cycle publishes nothing")]
    private partial void LogReleaseTimedOut(int index, ulong point, double timeoutMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "buffer {Index}: waiting for release point {Point} failed with errno {Errno}; the cycle publishes nothing")]
    private partial void LogReleaseFailed(int index, ulong point, int errno);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OnFormat modifier=0x{Modifier:x} needsFixation={NeedsFixation}")]
    private partial void LogOnFormat(ulong modifier, bool needsFixation);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "peer capabilities: negotiates device ids={Negotiates}; announcing {DeviceFormats} of {Offers} device formats")]
    private partial void LogDeviceOffers(bool negotiates, int deviceFormats, int offers);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "the daemon refused the announced formats ({Result}); the stream is activated anyway and will not negotiate")]
    private partial void LogAnnounceRefused(int result);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OnPostFormat fixated={Fixated} needsFixation={NeedsFixation} planeCount={PlaneCount}")]
    private partial void LogOnPostFormat(bool fixated, bool needsFixation, int planeCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "buffer {Index} declined: the allocator backed {Backed} of {Needed} planes")]
    private partial void LogPartialAllocation(int index, int backed, uint needed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "buffer {Index} declined: plane {Plane} carries descriptor {Fd}")]
    private partial void LogInvalidPlaneDescriptor(int index, uint plane, long fd);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the allocator declined buffer {Index}; an unbacked buffer fails the allocation of the whole pool, so back every buffer the pool asks for")]
    private partial void LogBufferDeclined(int index);

    [LoggerMessage(Level = LogLevel.Warning, Message = "buffer index {Index} is past the pool table; it stays unbacked, which fails the allocation of the whole pool")]
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

    /// <summary>
    /// Waits until the daemon has given this stream a node id, and returns it.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The node id, the same value <see cref="NodeId"/> reports from then on.</returns>
    /// <remarks>
    /// <see cref="NodeId"/> is null straight after <c>Connect</c>: the stream is a proxy until the
    /// daemon binds it, and the id is only assigned then. Linking or targeting this stream needs the
    /// id, so await this rather than reading the property and hoping. It completes when the stream
    /// first reaches Paused, which is where upstream's own examples read the id.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not connected yet.</exception>
    /// <exception cref="PipeWireException">The stream reached its error state instead.</exception>
    public Task<uint> WaitForNodeIdAsync(CancellationToken cancellationToken = default)
    {
        PipeWireStreamCore core = _core
            ?? throw new InvalidOperationException("Connect before waiting for the node id.");

        return core.WaitForNodeIdAsync(cancellationToken);
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

    /// <summary>The graph clock as of the last cycle, or null before the graph offers it.</summary>
    /// <remarks>
    /// Two streams on one context are driven by the same clock, so their clocks are directly
    /// comparable - which is what lets audio and video be lined up against each other.
    /// </remarks>
    public PipeWireGraphClock? GraphClock => _core?.GraphClock;

    /// <summary>What the graph's resampler is doing for this stream, or null if none is.</summary>
    public PipeWireRateMatch? RateMatch => _core?.RateMatch;

    /// <summary>
    /// Applies a rate correction to this stream, 1.0 being none.
    /// </summary>
    /// <remarks>
    /// For bridging the graph clock to one this library does not own - a network transport, another
    /// device. The upstream pattern is to derive the correction from how far the queue is from its
    /// target, smooth it, and apply it here; see PipeWire's own rtp and tunnel modules. Applying an
    /// unsmoothed correction makes the drift worse rather than better.
    /// </remarks>
    public void SetRate(double rate, CancellationToken cancellationToken = default) =>
        _core?.SetRate(rate, cancellationToken);

    /// <summary>
    /// Announces the latency this stream adds, so the rest of the graph can compensate.
    /// </summary>
    /// <remarks>
    /// Anything holding a queue - a network transport, an encoder - adds delay that nothing else
    /// can see. Left unannounced it becomes drift between this stream and everything it is meant
    /// to stay in sync with. Pass the process latency too when the delay is per-cycle rather than
    /// fixed; PipeWire's own transport modules announce both.
    /// </remarks>
    public void AnnounceLatency(
        PipeWireLatency latency,
        PipeWireProcessLatency? processLatency = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(latency);
        _core?.AnnounceLatency(latency, processLatency, cancellationToken);
    }

    /// <summary>
    /// Plays out what is queued and waits until the daemon says it has finished.
    /// </summary>
    /// <remarks>
    /// The difference between stopping and ending: disposing a stream drops whatever is still
    /// queued, which truncates the tail of the audio. This waits for it.
    /// </remarks>
    public Task DrainAsync(CancellationToken cancellationToken = default) =>
        _core?.DrainAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Runs one graph cycle and waits for it to complete. Only meaningful while
    /// <see cref="IsDriving"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="TriggerProcess"/> starts a cycle and returns; this waits for the daemon to report
    /// it finished, which is what a producer pacing its own output needs in order to know when the
    /// next frame may be submitted.
    /// <para>
    /// Upstream reports completion (<c>trigger_done</c>) only to a driving stream, so this faults with
    /// <see cref="InvalidOperationException"/> when <see cref="IsDriving"/> is false rather than
    /// waiting for a report that will not come. Connect with <c>driver: true</c> to be one.
    /// </para>
    /// </remarks>
    public Task TriggerProcessAndWaitAsync(CancellationToken cancellationToken = default) =>
        _core?.TriggerAndWaitAsync(cancellationToken) ?? Task.CompletedTask;
}
