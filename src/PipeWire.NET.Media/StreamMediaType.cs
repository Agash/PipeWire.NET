using System.Runtime.Versioning;
using System.Text;
using PipeWire.NET.Generated;

namespace PipeWire.NET.Media;

/// <summary>The kind of media a stream carries.</summary>
public enum StreamMediaType
{
    /// <summary><c>media.type=Video</c>.</summary>
    Video,
    /// <summary><c>media.type=Audio</c>.</summary>
    Audio,
}
