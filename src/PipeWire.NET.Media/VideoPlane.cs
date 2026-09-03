using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Media;

/// <summary>
/// The dmabuf layout of one plane of a video frame. A packed format (BGRA) has a single plane;
/// a planar format (NV12) has two (Y, then interleaved UV); a modifier with auxiliary/compression
/// data can add more. Together with <see cref="VideoFrame.Modifier"/> this is everything a GPU
/// needs to import the frame zero-copy (e.g. one <c>VkImage</c> per plane fd, or a disjoint
/// multi-plane image via <c>VK_EXT_image_drm_format_modifier</c>).
/// </summary>
/// <param name="Fd">
/// dmabuf file descriptor backing this plane (may be shared across planes).
/// <para>
/// On a frame received from <see cref="PipeWireVideoCapture"/> this is borrowed from the stream's
/// pool for the duration of the handler: do not close it, do not store it, and duplicate it before
/// handing it to an importer that takes ownership - see <see cref="VideoFrame.Fd"/> and
/// <see cref="VideoFrame.DuplicateFd"/>. On a plane the application fills in
/// <see cref="PipeWireVideoOutput.AllocateDmaBuf"/> the descriptor is the application's own, and
/// stays the application's: PipeWire never closes it, and the release notification is
/// <see cref="PipeWireVideoOutput.ReleaseDmaBuf"/>.
/// </para>
/// </param>
/// <param name="Offset">Byte offset of the plane within its backing fd.</param>
/// <param name="Stride">Bytes per row of the plane.</param>
/// <param name="Size">Plane size in bytes, or 0 when the producer did not report it.</param>
public readonly record struct VideoPlane(long Fd, uint Offset, int Stride, uint Size)
{
    /// <summary>A private copy of <see cref="Fd"/> that the caller owns and must close.</summary>
    /// <returns>A new descriptor, or -1 when this plane is not fd-backed.</returns>
    /// <exception cref="IOException">The kernel refused to duplicate the descriptor.</exception>
    /// <remarks>
    /// The per-plane counterpart to <see cref="VideoFrame.DuplicateFd"/>, which duplicates the
    /// first plane only. Planes of a planar format may be backed by different descriptors, so an
    /// importer taking ownership of each one needs a copy of each one.
    /// </remarks>
    public int DuplicateFd() => Descriptors.Duplicate(Fd);
}
