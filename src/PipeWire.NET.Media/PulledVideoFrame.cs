using System.Collections.Immutable;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>One plane of a <see cref="PulledVideoFrame"/>, owning its descriptor.</summary>
/// <remarks>
/// The difference from <see cref="VideoPlane"/> is ownership. A <see cref="VideoPlane"/> borrows a
/// descriptor from the stream's pool for the length of one callback; this one holds a duplicate that
/// stays valid until the frame is disposed, which is what lets a pulled frame outlive the cycle it
/// arrived on.
/// </remarks>
/// <param name="Descriptor">The duplicated dmabuf descriptor. Closed when the frame is disposed.</param>
/// <param name="Offset">Byte offset of the plane within its backing descriptor.</param>
/// <param name="Stride">Bytes per row of the plane.</param>
/// <param name="Size">Plane size in bytes, or 0 when the producer did not report it.</param>
public sealed record PulledVideoPlane(
    SafeDescriptorHandle Descriptor,
    uint Offset,
    int Stride,
    uint Size);

/// <summary>
/// A frame taken out of the stream and handed to a consumer that pulls on its own schedule, rather
/// than one that runs inside the capture callback.
/// </summary>
/// <remarks>
/// <para>
/// The push path (<see cref="PipeWireVideoCapture.FrameReady"/>) hands out a
/// <see cref="VideoFrame"/>, which is a <see langword="ref struct"/> over pool memory and is invalid
/// the moment the handler returns. That suits a consumer driven by the graph. A consumer with its
/// own clock - an encoder, or a transport sending on a connection whose pacing it does not control -
/// needs to ask for the current frame instead, which means the frame has to outlive the cycle.
/// </para>
/// <para>
/// So this type owns what it carries: host bytes are copied, and dmabuf descriptors are duplicated.
/// Dispose it to close them. Dropping one on the floor leaks a descriptor until finalization, which
/// on a 60fps stream exhausts the process limit in under a minute.
/// </para>
/// </remarks>
public sealed class PulledVideoFrame : IDisposable
{
    private bool _disposed;

    internal PulledVideoFrame(
        ImmutableArray<byte> pixels,
        ImmutableArray<PulledVideoPlane> planes,
        int stride,
        int width,
        int height,
        PixelFormat format,
        uint drmFourcc,
        ulong modifier,
        ulong sequenceNumber,
        PipeWireBufferType bufferType,
        VideoColorInfo color,
        long? presentationTimeNs,
        long? captureClockNs,
        long? mediaClockNs,
        long delayNs,
        VideoRegion? crop,
        SpaMetaVideotransformValue transform)
    {
        Pixels = pixels;
        Planes = planes;
        Stride = stride;
        Width = width;
        Height = height;
        Format = format;
        DrmFourcc = drmFourcc;
        Modifier = modifier;
        SequenceNumber = sequenceNumber;
        BufferType = bufferType;
        Color = color;
        PresentationTimeNs = presentationTimeNs;
        CaptureClockNs = captureClockNs;
        MediaClockNs = mediaClockNs;
        DelayNs = delayNs;
        Crop = crop;
        Transform = transform;
    }

    /// <summary>The frame's bytes. Empty for a dmabuf frame, whose pixels live on the GPU.</summary>
    public ImmutableArray<byte> Pixels { get; }

    /// <summary>
    /// Per-plane dmabuf layout, each owning a duplicated descriptor. Empty for a host-memory frame.
    /// </summary>
    public ImmutableArray<PulledVideoPlane> Planes { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride { get; }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Negotiated pixel format.</summary>
    public PixelFormat Format { get; }

    /// <summary>
    /// DRM fourcc for <see cref="Format"/>, or <see cref="DrmFormat.Invalid"/> when it has none.
    /// Together with <see cref="Modifier"/> this is what an importer needs to bind the descriptors.
    /// </summary>
    public uint DrmFourcc { get; }

    /// <summary>Negotiated DRM format modifier, or <see cref="DrmFormatModifier.Invalid"/>.</summary>
    public ulong Modifier { get; }

    /// <summary>The frame index within this session.</summary>
    public ulong SequenceNumber { get; }

    /// <summary>Whether the frame arrived as host memory or as a dmabuf.</summary>
    public PipeWireBufferType BufferType { get; }

    /// <summary>Negotiated colour metadata.</summary>
    public VideoColorInfo Color { get; }

    /// <summary>
    /// Presentation timestamp, or null when the producer sent none. On the same monotonic clock as
    /// an audio stream on the same graph, which is what makes the two alignable.
    /// </summary>
    public long? PresentationTimeNs { get; }

    /// <summary>Graph clock time of the cycle this frame arrived on, or null.</summary>
    public long? CaptureClockNs { get; }

    /// <summary>Media position at the cycle, or null.</summary>
    public long? MediaClockNs { get; }

    /// <summary>Signal delay between the source and this stream.</summary>
    public long DelayNs { get; }

    /// <summary>The producer's crop rectangle, when it sent one.</summary>
    public VideoRegion? Crop { get; }

    /// <summary>How the image is oriented.</summary>
    public SpaMetaVideotransformValue Transform { get; }

    /// <summary>True when the frame carries dmabuf descriptors rather than host bytes.</summary>
    public bool IsFdBacked => !Planes.IsDefaultOrEmpty;

    /// <summary>Closes every descriptor the frame owns. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!Planes.IsDefaultOrEmpty)
        {
            foreach (PulledVideoPlane plane in Planes)
                plane.Descriptor.Dispose();
        }
    }
}
