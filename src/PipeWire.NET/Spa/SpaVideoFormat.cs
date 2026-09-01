using PipeWire.NET.Generated;

namespace PipeWire.NET.Spa;

/// <summary>Short aliases for the generated <see cref="spa_video_format"/> enum.</summary>
internal static class SpaVideoFormat
{
    internal const uint Unknown = (uint)spa_video_format.SPA_VIDEO_FORMAT_UNKNOWN;
    internal const uint I420    = (uint)spa_video_format.SPA_VIDEO_FORMAT_I420;
    internal const uint YUY2    = (uint)spa_video_format.SPA_VIDEO_FORMAT_YUY2;
    internal const uint RGBA    = (uint)spa_video_format.SPA_VIDEO_FORMAT_RGBA;
    internal const uint BGRA    = (uint)spa_video_format.SPA_VIDEO_FORMAT_BGRA;
    internal const uint RGBx    = (uint)spa_video_format.SPA_VIDEO_FORMAT_RGBx;
    internal const uint BGRx    = (uint)spa_video_format.SPA_VIDEO_FORMAT_BGRx;
    internal const uint NV12    = (uint)spa_video_format.SPA_VIDEO_FORMAT_NV12;
}
