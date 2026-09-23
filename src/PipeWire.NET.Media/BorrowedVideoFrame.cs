using System.Runtime.CompilerServices;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>One plane of a <see cref="BorrowedVideoFrame"/>, as a raw descriptor number.</summary>
/// <remarks>
/// Borrowed, not owned: the descriptor belongs to the stream's buffer pool and is not duplicated.
/// It stays open while the stream is streaming, and is closed when the pool is torn down by a
/// renegotiation or a disconnect - which is why a borrowed frame is only valid until the next pull
/// or the next state change.
/// </remarks>
/// <param name="Fd">The pool's dmabuf descriptor, matching <c>spa_data.fd</c>. Do not close it.</param>
/// <param name="Offset">Byte offset of the plane within its backing descriptor.</param>
/// <param name="Stride">Bytes per row of the plane.</param>
/// <param name="Size">Plane size in bytes, or 0 when the producer did not report it.</param>
public readonly record struct BorrowedVideoPlane(long Fd, uint Offset, uint Stride, uint Size);

// Inline storage for up to four planes: packed uses 1, NV12 uses 2, I420 uses 3, and a modifier
// with auxiliary data can add one more. Keeps a BorrowedVideoFrame a pure value type, so pulling a
// frame allocates nothing at all - which is the whole point of this shape existing beside the
// owning one.
[InlineArray(MaxPlanes)]
internal struct BorrowedPlaneArray
{
    public const int MaxPlanes = 4;
    private BorrowedVideoPlane _element0;
}

/// <summary>
/// A frame handed to a puller by value, borrowing the pool's descriptors rather than duplicating
/// them.
/// </summary>
/// <remarks>
/// <para>
/// The allocation-free counterpart to <see cref="PulledVideoFrame"/>. That one owns what it
/// carries, which costs a heap object and a <c>dup</c> per plane per frame; this one copies a
/// handful of integers into the caller's own storage and touches neither the heap nor the kernel.
/// It is the shape a GPU consumer wants: the descriptor goes straight to an importer (VAAPI via
/// DRM-PRIME, or Vulkan with <c>VK_EXT_image_drm_format_modifier</c>) alongside
/// <see cref="DrmFourcc"/> and <see cref="Modifier"/>.
/// </para>
/// <para>
/// The contract is the one Spout and Syphon also live with: the handle stays valid, the contents
/// may not. Consume the frame before the next pull. The producer may recycle the buffer as soon as
/// the cycle it arrived on has ended, because the explicit-sync release point is signalled there -
/// withholding it would stall the producer. A consumer that needs a frame to outlive the cycle
/// wants <see cref="PulledVideoFrame"/> instead.
/// </para>
/// </remarks>
public readonly struct BorrowedVideoFrame
{
    private readonly BorrowedPlaneArray _planes;

    internal BorrowedVideoFrame(
        ReadOnlySpan<BorrowedVideoPlane> planes,
        int stride,
        int width,
        int height,
        PixelFormat format,
        uint drmFourcc,
        ulong modifier,
        ulong sequenceNumber,
        long? presentationTimestampNs,
        long? queuedTimeNs,
        long? graphTimeNs,
        long? streamPositionNs,
        long delayNs,
        VideoRegion? crop,
        SpaMetaVideotransformValue transform
    )
    {
        PlaneCount = Math.Min(planes.Length, BorrowedPlaneArray.MaxPlanes);
        for (int i = 0; i < PlaneCount; i++)
            _planes[i] = planes[i];

        Stride = stride;
        Width = width;
        Height = height;
        Format = format;
        DrmFourcc = drmFourcc;
        Modifier = modifier;
        SequenceNumber = sequenceNumber;
        PresentationTimestampNs = presentationTimestampNs;
        QueuedTimeNs = queuedTimeNs;
        GraphTimeNs = graphTimeNs;
        StreamPositionNs = streamPositionNs;
        DelayNs = delayNs;
        Crop = crop;
        Transform = transform;
    }

    /// <summary>Number of valid planes, indexed <c>0 .. PlaneCount-1</c> through <see cref="this[int]"/>.</summary>
    public int PlaneCount { get; }

    /// <summary>The plane at <paramref name="index"/>.</summary>
    /// <param name="index">Zero-based, below <see cref="PlaneCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the valid planes.</exception>
    /// <remarks>
    /// An indexer rather than a span property: handing out a span over the inline array would expose
    /// a reference into this struct's own storage (CS8170), so planes come back by value - which is
    /// still no allocation.
    /// </remarks>
    public BorrowedVideoPlane this[int index]
    {
        get
        {
            if ((uint)index >= (uint)PlaneCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            return _planes[index];
        }
    }

    /// <summary>Bytes per row.</summary>
    public int Stride { get; }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Negotiated pixel format.</summary>
    public PixelFormat Format { get; }

    /// <summary>DRM fourcc for <see cref="Format"/>, or <see cref="DrmFormat.Invalid"/>.</summary>
    public uint DrmFourcc { get; }

    /// <summary>Negotiated DRM format modifier, or <see cref="DrmFormatModifier.Invalid"/>.</summary>
    public ulong Modifier { get; }

    /// <summary>The frame index within this session.</summary>
    public ulong SequenceNumber { get; }

    /// <summary>
    /// The producer's presentation timestamp for this frame, in nanoseconds, from its
    /// <c>SPA_META_Header</c>; null when the producer attached none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The timestamp to align audio and video on, as upstream's video-play-sync does. It is the
    /// producer's own timeline, so it is comparable across two streams only when one producer
    /// stamped both, or both used the same clock: this library's outputs and upstream's video-src
    /// stamp CLOCK_MONOTONIC, while GStreamer's pipewiresink stamps each pipeline's own running time.
    /// </para>
    /// <para>
    /// Carried per buffer, so frames that arrive in the same graph cycle still have their own.
    /// </para>
    /// </remarks>
    public long? PresentationTimestampNs { get; }

    /// <summary>The cycle time the buffer was queued in (<c>pw_buffer.time</c>), or null.</summary>
    /// <remarks>See <see cref="VideoFrame.QueuedTimeNs"/>.</remarks>
    public long? QueuedTimeNs { get; }

    /// <summary>
    /// Graph clock time (CLOCK_MONOTONIC nanoseconds) of the processing cycle that delivered this
    /// frame, from <c>pw_stream_get_time_n</c>; null if the graph offered none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per cycle, not per buffer: everything delivered in one cycle shares it, so a burst of
    /// buffers carries one value. For per-buffer time use <see cref="PresentationTimestampNs"/> or
    /// <see cref="QueuedTimeNs"/>.
    /// </para>
    /// <para>
    /// It only advances if the group's driver publishes a clock. Driver nodes always do (a sound
    /// card, a null sink, the dummy driver). A stream acting as driver has to write it itself:
    /// this library's outputs do, and GStreamer's pipewiresink does for audio but deliberately not
    /// for video, so a capture of a pipewiresink video source sees this frozen.
    /// </para>
    /// </remarks>
    public long? GraphTimeNs { get; }

    /// <summary>Media position at the cycle, or null.</summary>
    public long? StreamPositionNs { get; }

    /// <summary>Signal delay between the source and this stream.</summary>
    public long DelayNs { get; }

    /// <summary>The producer's crop rectangle, when it sent one.</summary>
    public VideoRegion? Crop { get; }

    /// <summary>How the image is oriented.</summary>
    public SpaMetaVideotransformValue Transform { get; }

    /// <summary>True when this frame carries dmabuf planes rather than being the default value.</summary>
    public bool IsFdBacked => PlaneCount > 0;
}
