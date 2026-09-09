using PipeWire.NET.Drm;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>
/// Maps SPA raw video formats to DRM fourcc codes, the pixel-layout half of a dmabuf description.
/// The other half is the tiling/compression layout, see <see cref="DrmFormatModifier"/>. A consumer
/// importing a dmabuf into a GPU needs both.
/// </summary>
/// <remarks>
/// <para>
/// The fourcc values themselves come from <see cref="DrmFourcc"/>, generated from
/// <c>drm_fourcc.h</c>. Only the mapping is written by hand, because it exists in neither project's
/// headers: PipeWire's GStreamer element defers to GStreamer, whose table lives in the C source of
/// <c>video-info-dma.c</c>. This table is transcribed from there.
/// </para>
/// <para>
/// Transcribed rather than derived, because the two naming schemes run in opposite directions: a
/// SPA/GStreamer name lists channels in <em>memory (byte) order</em>, while a DRM fourcc names the
/// channels of a <em>little-endian 32-bit word</em>, which reverses them. So <c>RGBA</c> is
/// <c>ABGR8888</c>, not <c>RGBA8888</c>. Guessing from the names swaps red and blue, which renders
/// as a plausible but wrong image rather than failing outright.
/// </para>
/// <para>
/// Only formats whose DRM equivalent is the plain (<c>LINEAR</c>) layout are covered. Formats that
/// upstream maps only under a vendor tiling modifier are deliberately absent, as is
/// <see cref="SpaVideoFormat.Ayuv"/>: DRM's <c>AYUV</c> is GStreamer's <c>VUYA</c>, which has no SPA
/// equivalent, so mapping SPA's <c>AYUV</c> onto it would reorder the channels.
/// </para>
/// </remarks>
public static class DrmFormat
{
    /// <summary><c>DRM_FORMAT_INVALID</c> - no known fourcc for this format.</summary>
    public const uint Invalid = DrmFourcc.DRM_FORMAT_INVALID;

    /// <summary>
    /// Returns the DRM fourcc for <paramref name="format"/>, or <see cref="Invalid"/> when the format
    /// has no plain-layout DRM equivalent.
    /// </summary>
    public static uint FromVideoFormat(SpaVideoFormat format) => format switch
    {
        SpaVideoFormat.Yuy2 => DrmFourcc.DRM_FORMAT_YUYV,
        SpaVideoFormat.Yvyu => DrmFourcc.DRM_FORMAT_YVYU,
        SpaVideoFormat.Uyvy => DrmFourcc.DRM_FORMAT_UYVY,
        SpaVideoFormat.Vyuy => DrmFourcc.DRM_FORMAT_VYUY,
        SpaVideoFormat.Nv12 => DrmFourcc.DRM_FORMAT_NV12,
        SpaVideoFormat.Nv21 => DrmFourcc.DRM_FORMAT_NV21,
        SpaVideoFormat.Nv16 => DrmFourcc.DRM_FORMAT_NV16,
        SpaVideoFormat.Nv61 => DrmFourcc.DRM_FORMAT_NV61,
        SpaVideoFormat.Nv24 => DrmFourcc.DRM_FORMAT_NV24,
        SpaVideoFormat.Yuv9 => DrmFourcc.DRM_FORMAT_YUV410,
        SpaVideoFormat.Yvu9 => DrmFourcc.DRM_FORMAT_YVU410,
        SpaVideoFormat.Y41B => DrmFourcc.DRM_FORMAT_YUV411,
        SpaVideoFormat.I420 => DrmFourcc.DRM_FORMAT_YUV420,
        SpaVideoFormat.Yv12 => DrmFourcc.DRM_FORMAT_YVU420,
        SpaVideoFormat.Y42B => DrmFourcc.DRM_FORMAT_YUV422,
        SpaVideoFormat.Y444 => DrmFourcc.DRM_FORMAT_YUV444,
        SpaVideoFormat.Rgb15 => DrmFourcc.DRM_FORMAT_XRGB1555,
        SpaVideoFormat.Rgb16 => DrmFourcc.DRM_FORMAT_RGB565,
        SpaVideoFormat.Bgr16 => DrmFourcc.DRM_FORMAT_BGR565,
        SpaVideoFormat.Rgb => DrmFourcc.DRM_FORMAT_BGR888,
        SpaVideoFormat.Bgr => DrmFourcc.DRM_FORMAT_RGB888,
        SpaVideoFormat.Rgba => DrmFourcc.DRM_FORMAT_ABGR8888,
        SpaVideoFormat.Rgbx => DrmFourcc.DRM_FORMAT_XBGR8888,
        SpaVideoFormat.Bgra => DrmFourcc.DRM_FORMAT_ARGB8888,
        SpaVideoFormat.Bgrx => DrmFourcc.DRM_FORMAT_XRGB8888,
        SpaVideoFormat.Argb => DrmFourcc.DRM_FORMAT_BGRA8888,
        SpaVideoFormat.XRgb => DrmFourcc.DRM_FORMAT_BGRX8888,
        SpaVideoFormat.Abgr => DrmFourcc.DRM_FORMAT_RGBA8888,
        SpaVideoFormat.XBgr => DrmFourcc.DRM_FORMAT_RGBX8888,
        SpaVideoFormat.P010_10Le => DrmFourcc.DRM_FORMAT_P010,
        SpaVideoFormat.Gray8 => DrmFourcc.DRM_FORMAT_R8,
        SpaVideoFormat.Gray16Le => DrmFourcc.DRM_FORMAT_R16,
        SpaVideoFormat.Gray16Be => DrmFourcc.DRM_FORMAT_R16 | DrmFourcc.DRM_FORMAT_BIG_ENDIAN,
        _ => Invalid,
    };

    /// <summary>
    /// Returns the DRM fourcc for <paramref name="format"/>, or <see cref="Invalid"/> for
    /// <see cref="PixelFormat.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// The convenience overload for the formats this library negotiates. It defers to
    /// <see cref="FromVideoFormat"/> so the two cannot drift apart.
    /// </remarks>
    public static uint FromPixelFormat(PixelFormat format) => format switch
    {
        PixelFormat.Rgba => FromVideoFormat(SpaVideoFormat.Rgba),
        PixelFormat.Bgra => FromVideoFormat(SpaVideoFormat.Bgra),
        PixelFormat.Rgbx => FromVideoFormat(SpaVideoFormat.Rgbx),
        PixelFormat.Bgrx => FromVideoFormat(SpaVideoFormat.Bgrx),
        PixelFormat.Yuyv => FromVideoFormat(SpaVideoFormat.Yuy2),
        PixelFormat.Yuv420 => FromVideoFormat(SpaVideoFormat.I420),
        PixelFormat.Nv12 => FromVideoFormat(SpaVideoFormat.Nv12),
        _ => Invalid,
    };
}
