using PipeWire.NET.Interop;

namespace PipeWire.NET.Media;

/// <summary>What kind of timeline a sync descriptor is.</summary>
internal enum SyncTimelineKind
{
    /// <summary>Neither of the two below: nothing here can wait on it or signal it.</summary>
    Unknown,

    /// <summary>A DRM syncobj timeline, which is what <c>SPA_DATA_SyncObj</c> means.</summary>
    Syncobj,

    /// <summary>An eventfd, the stand-in upstream's video-src-sync example puts there.</summary>
    Eventfd,
}
