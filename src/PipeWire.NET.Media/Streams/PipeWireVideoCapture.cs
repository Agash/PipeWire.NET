using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media.Streams;

/// <summary>
/// Receives video frames from a PipeWire source (V4L2 camera, virtual camera,
/// screen-capture portal node, or another app's video output).
/// </summary>
/// <remarks>
/// <see cref="FrameReady"/> fires on the PipeWire loop thread; the <see cref="VideoFrame"/>
/// is a <see langword="ref struct"/> whose data is valid only for the duration of the handler.
/// <para>
/// <b>Lifetime.</b> One connection per instance: <c>Connect</c> refuses a second call, and disposal
/// is final. To point at a different source, make a new instance. There is deliberately no
/// reconnect, because a reconnect that reuses the negotiated format and buffers of a stream that
/// already ended is a different object wearing the old one's state.
/// </para>
/// <para>
/// <b>What the daemon does when a source disappears</b> is a separate question, and by default it
/// attaches the stream to another one. That is convenient for a media player and wrong for anything
/// that cares which device it is reading: frames keep arriving, from somewhere else, with nothing
/// in the API to say so. Pass <c>stayWithTheSource</c> to end the stream instead.
/// </para>
/// <para>
/// <b>Dispose it; do not let it fall out of scope.</b> The callbacks the daemon holds refer back
/// here through a weak handle, so an instance the application drops is collected and simply stops
/// delivering, with no error and no final state change. That is the deliberate half of the trade:
/// a strong handle would keep every one ever made alive for the life of the process. What it costs
/// is that the garbage collector cannot be the thing that closes one, because by the time it runs
/// there is nothing left to close it from.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireVideoCapture : IAsyncDisposable
{
    /// <summary>Wildcard node id - let PipeWire auto-select a source.</summary>
    public const uint AnyNode = Native.PW_ID_ANY;

    /// <summary>Signature for <see cref="FrameReady"/>.</summary>
    public delegate void FrameReadyHandler(PipeWireVideoCapture sender, VideoFrame frame);

    /// <summary>Signature for <see cref="StateChanged"/>.</summary>
    public delegate void StateChangedHandler(PipeWireVideoCapture sender, PipeWireStreamState oldState, PipeWireStreamState newState);

    /// <summary>Raised on the loop thread when a frame is ready. Do not cache the frame.</summary>
    public event FrameReadyHandler? FrameReady;

    /// <summary>Raised when the connection state changes.</summary>
    public event StateChangedHandler? StateChanged;

    private readonly PipeWireContext _ctx;
    private readonly string _name;
    private readonly ILogger _logger;
    private PipeWireStreamCore? _core;
    private ulong _sequence;

    // Boxed and swapped whole rather than mutated in place. The format is written by the loop
    // thread in OnFormat and read from NegotiatedModifier on whatever thread the caller is on; a
    // multi-field struct has no atomic assignment, so a reader could otherwise see a width from one
    // negotiation next to a modifier from the next. Swapping a reference is atomic, and the cost is
    // one allocation per negotiation rather than one per frame.
    private sealed class NegotiatedFormat(SpaFormatPod.VideoFormatInfo info)
    {
        public SpaFormatPod.VideoFormatInfo Info { get; } = info;
    }

    private NegotiatedFormat _fmtCell =
        new(new SpaFormatPod.VideoFormatInfo(PixelFormat.Bgra, 0, 0, VideoColorInfo.Unknown));

    private SpaFormatPod.VideoFormatInfo Format => Volatile.Read(ref _fmtCell).Info;

    // Whether the caller opted into modifier negotiation, and the single pixel format the modifiers
    // apply to. We do NOT retain the offered modifier list: fixation re-offers the producer's preferred
    // returned modifier (carried as the scalar _fmt.Modifier), which is always within our offered set.
    private bool _modifiersOffered;
    private PixelFormat _modifierFormat;
    private bool _modifierFixated;

    /// <summary>
    /// The DRM format modifier negotiated for delivered dmabuf frames, or
    /// <see cref="DrmFormatModifier.Invalid"/> when none (host-memory path or no modifier offered).
    /// </summary>
    public ulong NegotiatedModifier => Format.Modifier;

    /// <param name="context">A started <see cref="PipeWireContext"/>.</param>
    /// <param name="name">node.name advertised in the graph.</param>
    public PipeWireVideoCapture(PipeWireContext context, string name = "PipeWire.NET.VideoCapture")
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        _ctx = context;
        _name = name;
        _logger = context.LoggerFactory.CreateLogger($"PipeWire.NET.{name}");
    }

    /// <summary>Connects to a discovered source.</summary>
    public void Connect(PipeWireNode source, ReadOnlySpan<PixelFormat> preferredFormats = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Connect(source.NodeId, preferredFormats);
    }

    /// <summary>Connects to a source by node id (default: auto-select).</summary>
    /// <param name="targetNodeId">Source node id, or <see cref="AnyNode"/> to auto-select.</param>
    /// <param name="preferredFormats">Preferred pixel formats in priority order.</param>
    /// <param name="stayWithTheSource">
    /// <see langword="true"/> to end the stream when its source goes away, rather than letting the
    /// daemon attach it to another one.
    /// </param>
    /// <param name="preferredWidth">Preferred width in pixels.</param>
    /// <param name="preferredHeight">Preferred height in pixels.</param>
    /// <param name="preferredFrameRate">Preferred frame rate in frames per second.</param>
    /// <remarks>
    /// The geometry is a preference, not a demand: the offer accepts a range around it, so a
    /// producer of another size still negotiates and the frames that arrive may not match what was
    /// asked for. Read <see cref="VideoFrame.Width"/> and <see cref="VideoFrame.Height"/> rather
    /// than assuming. The defaults are what a consumer that does not care should ask for.
    /// </remarks>
    /// <param name="targetObjectName">
    /// Optional <c>target.object</c> - bind to a specific node by name/serial regardless of
    /// the session manager's default-device routing.
    /// </param>
    /// <param name="modifiers">
    /// DRM format modifiers to offer for a zero-copy dmabuf negotiation (the consumer's
    /// GPU-importable set). When non-empty, <paramref name="preferredFormats"/> must name exactly one
    /// format. The library auto-fixates to the producer's preferred modifier from this set.
    /// </param>
    public unsafe void Connect(uint targetNodeId = AnyNode,
        ReadOnlySpan<PixelFormat> preferredFormats = default,
        string? targetObjectName = null,
        ReadOnlySpan<long> modifiers = default,
        bool stayWithTheSource = false,
        int preferredWidth = 1920,
        int preferredHeight = 1080,
        int preferredFrameRate = 30)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preferredWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preferredHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preferredFrameRate);
        if (!modifiers.IsEmpty && preferredFormats.Length != 1)
            throw new ArgumentException(
                "Exactly one pixel format must be specified when offering DRM modifiers (modifiers are per-format).",
                nameof(preferredFormats));

        _modifiersOffered = !modifiers.IsEmpty;
        _modifierFormat = preferredFormats.Length == 1 ? preferredFormats[0] : default;
        _modifierFixated = false;

        var props = new StreamProperties(StreamMediaType.Video, StreamCategory.Capture)
            .WithRole("Camera")
            .WithNodeName(_name);
        if (targetObjectName is not null) props.WithTargetObject(targetObjectName);
        // Built locally and only published once the connect succeeded. Assigning the field first
        // leaves a failed connect behind a stream that reports itself already connected and can
        // never be retried.

        var core = new PipeWireStreamCore(_ctx, props, _name, OnBuffer, OnState, OnFormat, OnPostFormat);

        Span<byte> pod = stackalloc byte[1024];
        int len = SpaFormatPod.WriteVideoFormat(pod, preferredFormats,
            (uint)preferredWidth, (uint)preferredHeight, (uint)preferredFrameRate, fixedSize: false,
            modifiers: modifiers);

        // A second offer with no modifiers, so a producer that cannot provide DMA-BUF has a
        // host-memory shape to agree to. Only when modifiers were asked for: without them the
        // first pod already is the host-memory offer and a duplicate says nothing.
        Span<byte> fallback = stackalloc byte[1024];
        int fallbackLen = modifiers.IsEmpty
            ? 0
            : SpaFormatPod.WriteVideoFormat(fallback, preferredFormats,
                (uint)preferredWidth, (uint)preferredHeight, (uint)preferredFrameRate, fixedSize: false);

        try
        {
            core.Connect(SpaDirection.Input, targetNodeId,
            PipeWireStreamFlags.Autoconnect | PipeWireStreamFlags.MapBuffers
                | (stayWithTheSource ? PipeWireStreamFlags.DontReconnect : 0),
            pod[..len],
            fallback[..fallbackLen]);
            _core = core;
        }
        catch
        {
            core.Dispose();
            throw;
        }

    }

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
    public ValueTask DisposeAsync() => _core?.DisposeAsync() ?? ValueTask.CompletedTask;

    private unsafe void OnBuffer(spa_data* d, pw_buffer* buf, in PipeWireStreamCore.StreamClock clock)
    {
        if (d->chunk is null) return;

        uint offset = d->chunk->offset;
        uint size   = d->chunk->size;
        if (size == 0) return;

        // The chunk header lives in memory the producer owns, so its offset and size are inputs,
        // not facts. A span built from an out-of-range pair reads straight past the mapping, and a
        // size above int.MaxValue casts to a negative length.
        if ((ulong)offset + size > d->maxsize) return;
        if (size > int.MaxValue) return;

        // data may be null for a pure DMA-BUF buffer that wasn't host-mapped.
        var pixels = d->data is null
            ? ReadOnlySpan<byte>.Empty
            : new ReadOnlySpan<byte>((byte*)d->data + offset, (int)size);

        PipeWireBufferType bufferType = SpaFormatPod.ToBufferType((SpaDataType)d->type);
        bool fdBacked = bufferType is PipeWireBufferType.DmaBuf or PipeWireBufferType.MemFd;
        long fd = fdBacked && (long)d->fd >= 0 ? (long)d->fd : -1;

        // Surface every plane of an fd-backed frame so a consumer can import it zero-copy: a planar
        // dmabuf (NV12) carries one spa_data per plane, each with its own fd/offset/stride. Host-memory
        // frames keep the single-plane view (Planes stays empty).
        spa_buffer* spaBuf = buf->buffer;
        Span<VideoPlane> planes = stackalloc VideoPlane[8];
        int planeCount = 0;
        if (fdBacked)
        {
            uint n = Math.Min(spaBuf->n_datas, (uint)planes.Length);
            for (uint i = 0; i < n; i++)
            {
                spa_data* p = &spaBuf->datas[i];
                if (p->chunk is null) continue;

                // A plane the producer did not back has fd -1, and handing that to an importer
                // fails as EINVAL somewhere deep in the driver rather than here where it can be named.
                if ((long)p->fd < 0) continue;
                // For dmabuf the plane's offset within its fd is chunk->offset; mapoffset is an mmap
                // concept (0 for dmabuf). stride is signed in SPA but always >= 0 for video here.
                planes[planeCount++] = new VideoPlane(
                    (long)p->fd, p->chunk->offset, p->chunk->stride, p->maxsize);
            }
        }

        SpaFormatPod.VideoFormatInfo fmt = Format;

        // Nothing is negotiated, so nothing describes this buffer. Emitting it anyway hands the
        // handler a frame whose geometry is zero or, worse, the previous negotiation's.
        if (fmt.Width <= 0 || fmt.Height <= 0) return;

        var frame = new VideoFrame(
            pixels, d->chunk->stride, fmt.Width, fmt.Height, fmt.Format, ++_sequence,
            bufferType: bufferType,
            fd: fd,
            mapOffset: d->mapoffset,
            presentationTimeNs: SpaFormatPod.FindPresentationTimeNs(buf->buffer),
            color: fmt.Color,
            captureClockNs: clock.CaptureClockNs,
            mediaClockNs: clock.MediaClockNs,
            delayNs: clock.DelayNs,
            modifier: fdBacked ? fmt.Modifier : DrmFormatModifier.Invalid,
            planes: planes[..planeCount]);

        FrameReady?.Invoke(this, frame);
    }

    private void OnState(PipeWireStreamState oldState, PipeWireStreamState newState) =>
        StateChanged?.Invoke(this, oldState, newState);

    private unsafe void OnFormat(spa_pod* param)
    {
        if (param is null)
        {
            // Withdrawn: the stream is unconfigured until a new format arrives, and OnBuffer stops
            // emitting because the geometry is gone.
            Volatile.Write(ref _fmtCell,
                new NegotiatedFormat(
                    new SpaFormatPod.VideoFormatInfo(PixelFormat.Unknown, 0, 0, VideoColorInfo.Unknown)));
            _modifierFixated = false;
            LogFormatWithdrawn();
            return;
        }

        SpaFormatPod.VideoFormatInfo parsed = SpaFormatPod.ParseVideoFormat(param, Format);
        Volatile.Write(ref _fmtCell, new NegotiatedFormat(parsed));
        LogNegotiatedFormat(parsed.Format, parsed.Width, parsed.Height, parsed.Modifier, parsed.ModifierNeedsFixation);
    }

    // After the format is negotiated we know the geometry, so declare our buffer needs:
    // accept host memory AND DMA-BUF (zero-copy GPU), plus request the PTS header meta.
    private void OnPostFormat(PipeWireStreamCore core)
    {
        SpaFormatPod.VideoFormatInfo fmt = Format;
        if (fmt.Format == PixelFormat.Unknown || fmt.Width <= 0 || fmt.Height <= 0) return;

        // Two-step modifier fixation: when we offered a modifier choice with DONT_FIXATE the producer
        // returns the subset it supports without collapsing it (ModifierNeedsFixation). Because we only
        // ever offer modifiers our GPU can import, the producer's preferred returned modifier
        // is always safe - so re-offer just that single value, with DONT_FIXATE cleared, to fixate
        // the negotiation. One scalar, one stack buffer, no allocation.
        if (!_modifierFixated && _modifiersOffered && fmt.ModifierNeedsFixation)
        {
            Span<byte> fixate = stackalloc byte[512];
            ReadOnlySpan<PixelFormat> chosenFormat = [_modifierFormat];
            ReadOnlySpan<long> chosen = [(long)fmt.Modifier];
            int fl = SpaFormatPod.WriteVideoFormat(fixate, chosenFormat,
                (uint)fmt.Width, (uint)fmt.Height, 30, fixedSize: false,
                modifiers: chosen, fixateModifier: true);

            // Marked done only if the daemon took it. A refused fixation has to be retried, or the
            // negotiation stays unfixated, no buffers are ever allocated, and the stream delivers
            // nothing.
            int rc = core.RequestParamsFromCallback(fixate[..fl]);
            if (rc >= 0)
            {
                _modifierFixated = true;
                return; // a fresh param_changed will arrive with the fixated format
            }

            LogFixationRefused(rc);
        }

        Span<byte> meta = stackalloc byte[64];
        int ml = SpaFormatPod.WriteHeaderMetaParam(meta);

        int stride = SpaFormatPod.VideoStride(fmt.Format, fmt.Width);
        int size = SpaFormatPod.VideoImageSize(fmt.Format, fmt.Width, fmt.Height);
        if (size <= 0) { core.RequestParamsFromCallback(meta[..ml]); return; }   // geometry not known yet

        // Block count = number of planes. A planar format (I420=3, NV12=2) is carried as one spa_data
        // block per plane for BOTH host memory (one MemFd per plane) and DMA-BUF (one fd per plane) -
        // gst's pipewiresink splits the planes either way. Declaring a single block for a multi-plane
        // format makes the daemon reject buffer allocation ("alloc buffers: Invalid argument"); packed
        // formats are a single block. Offer host memory and DMA-BUF so a GPU producer can go zero-copy.
        int blocks = SpaFormatPod.VideoPlaneCount(fmt.Format);

        // As a consumer we do not dictate the block size: SPA_PARAM_BUFFERS_size is per block, and
        // the producer owns how it lays its planes out. Pinning a fixed figure risks refusal when
        // the producer layout differs from this arithmetic.
        int blockSize = SpaFormatPod.VideoBlockSize(fmt.Format, fmt.Width, fmt.Height);
        Span<byte> buffers = stackalloc byte[256];
        int bl = SpaFormatPod.WriteVideoBuffersParam(
            buffers, blockSize, stride, SpaFormatPod.VideoCaptureDataTypeMask, blocks, sizeIsAnyOf: true);

        LogRequestedBuffers(blocks, blockSize, stride, SpaFormatPod.VideoCaptureDataTypeMask);
        core.RequestParamsFromCallback(buffers[..bl], meta[..ml]);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "negotiated format {Format} {Width}x{Height} modifier=0x{Modifier:x} needsFixation={NeedsFixation}")]
    private partial void LogNegotiatedFormat(PixelFormat format, int width, int height, ulong modifier, bool needsFixation);

    [LoggerMessage(Level = LogLevel.Debug, Message = "requesting buffers blocks={Blocks} size={Size} stride={Stride} dataTypeMask=0x{DataTypeMask:x}")]
    private partial void LogRequestedBuffers(int blocks, int size, int stride, int dataTypeMask);

    [LoggerMessage(Level = LogLevel.Debug, Message = "the daemon withdrew the format; the stream is unconfigured")]
    private partial void LogFormatWithdrawn();

    [LoggerMessage(Level = LogLevel.Warning, Message = "the daemon refused the modifier fixation ({Result}); it will be retried on the next negotiation")]
    private partial void LogFixationRefused(int result);
}
