using System.Runtime.InteropServices;

using System.Collections.Immutable;

namespace PipeWire.NET.Media;

/// <summary>
/// A single video frame delivered by <see cref="PipeWireVideoCapture.FrameReady"/>.
/// The <see cref="Data"/> span is valid only for the duration of the event handler.
/// </summary>
public readonly ref partial struct VideoFrame
{
    /// <param name="data">
    /// Raw pixel data (host-mapped). Empty for a pure <see cref="PipeWireBufferType.DmaBuf"/>
    /// frame that was not memory-mapped - use <paramref name="fd"/> for zero-copy import.
    /// </param>
    /// <param name="stride">Bytes per row in the primary plane.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="format">Pixel format.</param>
    /// <param name="sequenceNumber">Monotonically increasing per-session counter.</param>
    /// <param name="bufferType">How the data is backed (host memory, fd, or DMA-BUF).</param>
    /// <param name="fd">Backing file descriptor (DMA-BUF / MemFd), or -1 when host-only.</param>
    /// <param name="mapOffset">Offset of the mapped region within the backing memory/fd.</param>
    /// <param name="presentationTimeNs">Presentation timestamp in nanoseconds, or null if unavailable.</param>
    /// <param name="color">Negotiated color metadata.</param>
    /// <param name="captureClockNs">Graph clock time (monotonic ns) of the capture cycle.</param>
    /// <param name="mediaClockNs">Media position (ns) at the cycle; null if unknown.</param>
    /// <param name="delayNs">Signal delay/latency (ns) from source to this stream.</param>
    /// <param name="modifier">
    /// Negotiated DRM format modifier for a dmabuf frame, or <see cref="DrmFormatModifier.Invalid"/>
    /// when none (host-memory path). Drives the GPU import layout.
    /// </param>
    /// <param name="planes">
    /// Per-plane dmabuf layout (fd/offset/stride/size). Multi-plane formats expose every plane here;
    /// for a single-plane frame this carries the one plane and mirrors
    /// <paramref name="fd"/>/<paramref name="stride"/>. Empty for a host-memory frame.
    /// </param>
    /// <param name="syncTimeline">
    /// The frame's explicit synchronisation points, or null when it carries none.
    /// </param>
    public VideoFrame(
        ReadOnlySpan<byte> data,
        int stride,
        int width,
        int height,
        PixelFormat format,
        ulong sequenceNumber,
        PipeWireBufferType bufferType = PipeWireBufferType.MemPtr,
        long fd = -1,
        uint mapOffset = 0,
        long presentationTimeNs = -1,
        VideoColorInfo color = default,
        long captureClockNs = -1,
        long mediaClockNs = -1,
        long delayNs = 0,
        ulong modifier = DrmFormatModifier.Invalid,
        ReadOnlySpan<VideoPlane> planes = default,
        VideoSyncTimeline? syncTimeline = null)
    {
        Data               = data;
        Stride             = stride;
        Width              = width;
        Height             = height;
        Format             = format;
        SequenceNumber     = sequenceNumber;
        BufferType         = bufferType;
        Fd                 = fd;
        MapOffset          = mapOffset;
        PresentationTimeNs = presentationTimeNs < 0 ? null : presentationTimeNs;
        Color              = color;
        CaptureClockNs     = captureClockNs < 0 ? null : captureClockNs;
        MediaClockNs       = mediaClockNs < 0 ? null : mediaClockNs;
        DelayNs            = delayNs;
        Modifier           = modifier;
        Planes             = planes;
        SyncTimeline       = syncTimeline;
    }

    /// <summary>Raw pixel bytes. Empty for an unmapped DMA-BUF frame (use <see cref="Fd"/>).</summary>
    public ReadOnlySpan<byte> Data { get; }

    /// <summary>Bytes per row in the primary plane.</summary>
    public int Stride { get; }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Pixel format of <see cref="Data"/>.</summary>
    public PixelFormat Format { get; }

    /// <summary>Monotonically increasing frame index for this session.</summary>
    public ulong SequenceNumber { get; }

    /// <summary>How the frame data is backed in memory.</summary>
    public PipeWireBufferType BufferType { get; }

    /// <summary>
    /// Backing file descriptor for <see cref="PipeWireBufferType.DmaBuf"/> /
    /// <see cref="PipeWireBufferType.MemFd"/>, or -1 when host-memory only.
    /// Import this into the GPU for a zero-copy pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Borrowed, not owned.</strong> The descriptor belongs to the stream's buffer pool and
    /// is valid only for the duration of the handler this frame was delivered to. Do not close it,
    /// and do not keep it: the pool recycles buffers, so the same number means a different buffer a
    /// few frames later, and a stored one eventually names something else entirely.
    /// </para>
    /// <para>
    /// This matters most at exactly the place the descriptor is useful. Several GPU import APIs
    /// <em>take ownership and close it themselves</em> - Vulkan's
    /// <c>VkImportMemoryFdInfoKHR</c> under <c>VK_KHR_external_memory_fd</c> is the common one, and
    /// some EGL paths do the same. Handing them this value directly means the pool's descriptor is
    /// closed underneath it: the next time PipeWire touches the buffer it operates on a number the
    /// process has since handed to some unrelated file. Pass <see cref="DuplicateFd"/> to anything
    /// that consumes ownership.
    /// </para>
    /// </remarks>
    public long Fd { get; }

    /// <summary>Offset of the mapped region within the backing memory / fd.</summary>
    public uint MapOffset { get; }

    /// <summary>Negotiated color metadata (range/matrix/transfer/primaries) - all <c>Unknown</c> if unreported.</summary>
    public VideoColorInfo Color { get; }

    /// <summary>
    /// Graph clock time (monotonic ns) of the processing cycle that delivered this frame,
    /// from <c>pw_stream_get_time</c>. This is the SAME clock for every stream in the graph,
    /// so audio and video frames can be aligned on it for A/V sync, or null.
    /// </summary>
    public long? CaptureClockNs { get; }

    /// <summary>Media position (ns) of this stream at the capture cycle (<c>ticks*rate</c>); null if unknown.</summary>
    public long? MediaClockNs { get; }

    /// <summary>
    /// Signal delay (ns) between the source and this stream. The frame's content corresponds to
    /// roughly <see cref="CaptureClockNs"/> - <see cref="DelayNs"/> on the shared clock - use this
    /// for latency-compensated, sample/frame-accurate timestamping.
    /// </summary>
    public long DelayNs { get; }

    /// <summary>
    /// Presentation timestamp in nanoseconds (from SPA_META_Header), or -1 if the source
    /// did not attach a header. Use for A/V sync over a transport like WebRTC.
    /// </summary>
    public long? PresentationTimeNs { get; }

    /// <summary>
    /// Negotiated DRM format modifier for a DMA-BUF frame (tiling/compression layout), or
    /// <see cref="DrmFormatModifier.Invalid"/> when none was negotiated. Pair with
    /// <see cref="Planes"/> to import the frame zero-copy via <c>VK_EXT_image_drm_format_modifier</c>.
    /// </summary>
    public ulong Modifier { get; }

    /// <summary>
    /// Per-plane DMA-BUF layout (fd/offset/stride/size). Empty for a host-memory frame; for a
    /// DMA-BUF frame this carries every plane (NV12 = 2, packed = 1, plus any modifier aux planes).
    /// Valid only for the duration of the <see cref="PipeWireVideoCapture.FrameReady"/> handler.
    /// </summary>
    public ReadOnlySpan<VideoPlane> Planes { get; }

    /// <summary>The frame's explicit synchronisation points, or null when it carries none.</summary>
    /// <remarks>
    /// Present only when the consumer asked for <c>SPA_META_SyncTimeline</c> at connect time and the
    /// producer agreed. When it is present the frame's contents are <b>not</b> ready on arrival: the
    /// consumer must wait for <see cref="VideoSyncTimeline.AcquirePoint"/> on the acquire timeline
    /// before reading, and signal <see cref="VideoSyncTimeline.ReleasePoint"/> when it is done.
    /// <para>
    /// Asking for it and then ignoring it is worse than never asking, because a producer that has
    /// agreed to explicit sync stops attaching implicit fences: reading the pixels without waiting
    /// then races the GPU still writing them.
    /// </para>
    /// </remarks>
    public VideoSyncTimeline? SyncTimeline { get; }

    /// <summary>True when the frame is backed by a DMA-BUF or MemFd file descriptor.</summary>
    public bool IsFdBacked => Fd >= 0;

    /// <summary>Copies the frame so it can be kept past the handler that delivered it.</summary>
    /// <remarks>
    /// The one place the copy happens, rather than in each consumer that needs to queue, encode or
    /// align frames. Host-memory bytes are copied; descriptors are deliberately not carried: see
    /// <see cref="OwnedVideoFrame"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The frame is fd-backed (DMA-BUF/MemFd): its <see cref="Data"/> is empty and a byte copy
    /// would keep nothing, so cloning refuses rather than returning an empty frame that reads as
    /// valid. Duplicate what is kept on purpose instead: <see cref="DuplicateFd"/> for the first
    /// plane's descriptor, <see cref="VideoPlane.DuplicateFd"/> per plane.
    /// </exception>
    public OwnedVideoFrame Clone()
    {
        if (IsFdBacked)
            throw new InvalidOperationException(
                "an fd-backed frame has no host bytes to copy; duplicate its descriptors instead "
                + "(VideoFrame.DuplicateFd, VideoPlane.DuplicateFd).");

        return new OwnedVideoFrame(
            [.. Data],
            Stride,
            Width,
            Height,
            Format,
            SequenceNumber,
            Color,
            PresentationTimeNs,
            CaptureClockNs,
            MediaClockNs,
            DelayNs);
    }

    /// <summary>A private copy of <see cref="Fd"/> that the caller owns and must close.</summary>
    /// <returns>A new descriptor, or -1 when this frame is not fd-backed.</returns>
    /// <exception cref="IOException">The kernel refused to duplicate the descriptor.</exception>
    /// <remarks>
    /// The safe way to hand a frame's memory to anything outside the handler. The copy refers to
    /// the same buffer but is a descriptor of its own, so an importer that closes what it is given
    /// closes this rather than the pool's. Ownership transfers to the caller: close it, or hand it
    /// to something documented to take it.
    /// </remarks>
    /// <remarks>
    /// This is the first plane's descriptor. A planar format whose planes are backed by different
    /// descriptors needs <see cref="VideoPlane.DuplicateFd"/> per plane; see <see cref="Planes"/>.
    /// </remarks>
    public int DuplicateFd() => Descriptors.Duplicate(Fd);
}
