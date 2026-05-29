using System.Runtime.Versioning;
using PipeWire.NET.Generated;
using PipeWire.NET.Spa;

namespace PipeWire.NET;

/// <summary>
/// Receives video frames from a PipeWire source (V4L2 camera, virtual camera,
/// screen-capture portal node, or another app's video output).
/// </summary>
/// <remarks>
/// <see cref="FrameReady"/> fires on the PipeWire loop thread; the <see cref="VideoFrame"/>
/// is a <see langword="ref struct"/> whose data is valid only for the duration of the handler.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PipeWireVideoCapture : IAsyncDisposable
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
    private PipeWireStreamCore? _core;
    private ulong _sequence;
    private SpaFormat.VideoFormatInfo _fmt = new(PixelFormat.Bgra, 0, 0, VideoColorInfo.Unknown);

    /// <param name="context">A started <see cref="PipeWireContext"/>.</param>
    /// <param name="name">node.name advertised in the graph.</param>
    public PipeWireVideoCapture(PipeWireContext context, string name = "PipeWire.NET.VideoCapture")
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        _ctx = context;
        _name = name;
    }

    /// <summary>Connects to a discovered source.</summary>
    public void Connect(PipeWireSource source, ReadOnlySpan<PixelFormat> preferredFormats = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Connect(source.NodeId, preferredFormats);
    }

    /// <summary>Connects to a source by node id (default: auto-select).</summary>
    /// <param name="targetNodeId">Source node id, or <see cref="AnyNode"/> to auto-select.</param>
    /// <param name="preferredFormats">Preferred pixel formats in priority order.</param>
    /// <param name="targetObjectName">
    /// Optional <c>target.object</c> - bind to a specific node by name/serial regardless of
    /// the session manager's default-device routing.
    /// </param>
    public unsafe void Connect(uint targetNodeId = AnyNode,
        ReadOnlySpan<PixelFormat> preferredFormats = default,
        string? targetObjectName = null)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");

        var props = new StreamProperties(StreamMediaType.Video, StreamCategory.Capture)
            .WithRole("Camera")
            .WithNodeName(_name);
        if (targetObjectName is not null) props.WithTargetObject(targetObjectName);

        _core = new PipeWireStreamCore(_ctx, props, _name, OnBuffer, OnState, OnFormat, OnPostFormat);

        Span<byte> pod = stackalloc byte[1024];
        int len = SpaFormat.WriteVideoFormat(pod, preferredFormats, 1920, 1080, 30, fixedSize: false);
        _core.Connect(spa_direction.SPA_DIRECTION_INPUT, targetNodeId,
            pw_stream_flags.PW_STREAM_FLAG_AUTOCONNECT | pw_stream_flags.PW_STREAM_FLAG_MAP_BUFFERS,
            pod[..len]);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _core?.DisposeAsync() ?? ValueTask.CompletedTask;

    private unsafe void OnBuffer(spa_data* d, pw_buffer* buf, in PipeWireStreamCore.StreamClock clock)
    {
        if (d->chunk is null) return;

        uint offset = d->chunk->offset;
        uint size   = d->chunk->size;
        if (size == 0) return;

        // data may be null for a pure DMA-BUF buffer that wasn't host-mapped.
        var pixels = d->data is null
            ? ReadOnlySpan<byte>.Empty
            : new ReadOnlySpan<byte>((byte*)d->data + offset, (int)size);

        PipeWireBufferType bufferType = SpaFormat.ToBufferType(d->type);
        bool fdBacked = bufferType is PipeWireBufferType.DmaBuf or PipeWireBufferType.MemFd;
        long fd = fdBacked && (long)d->fd >= 0 ? (long)d->fd : -1;

        var frame = new VideoFrame(
            pixels, d->chunk->stride, _fmt.Width, _fmt.Height, _fmt.Format, ++_sequence,
            bufferType: bufferType,
            fd: fd,
            mapOffset: d->mapoffset,
            presentationTimeNs: SpaFormat.FindPresentationTimeNs(buf->buffer),
            color: _fmt.Color,
            captureClockNs: clock.CaptureClockNs,
            mediaClockNs: clock.MediaClockNs,
            delayNs: clock.DelayNs);

        FrameReady?.Invoke(this, frame);
    }

    private void OnState(PipeWireStreamState oldState, PipeWireStreamState newState) =>
        StateChanged?.Invoke(this, oldState, newState);

    private unsafe void OnFormat(spa_pod* param) =>
        _fmt = SpaFormat.ParseVideoFormat(param, _fmt);

    // After the format is negotiated we know the geometry, so declare our buffer needs:
    // accept host memory AND DMA-BUF (zero-copy GPU), plus request the PTS header meta.
    private void OnPostFormat(PipeWireStreamCore core)
    {
        Span<byte> meta = stackalloc byte[64];
        int ml = SpaFormat.WriteHeaderMetaParam(meta);

        int stride = SpaFormat.VideoStride(_fmt.Format, _fmt.Width);
        int size = SpaFormat.VideoImageSize(_fmt.Format, _fmt.Width, _fmt.Height);
        if (size <= 0) { core.RequestParams(meta[..ml]); return; }   // geometry not known yet

        // Canonical buffer param (size/stride correct for packed or planar) advertising that we
        // accept DMA-BUF and host memory - a GPU producer can then hand us zero-copy buffers.
        Span<byte> buffers = stackalloc byte[256];
        int bl = SpaFormat.WriteVideoBuffersParam(buffers, size, stride, SpaFormat.VideoCaptureDataTypeMask);

        core.RequestParams(buffers[..bl], meta[..ml]);
    }
}
