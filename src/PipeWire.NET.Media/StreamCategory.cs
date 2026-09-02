using System.Runtime.Versioning;
using System.Text;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>How a stream participates in the graph (maps to <c>media.category</c>).</summary>
public enum StreamCategory
{
    /// <summary>Receiving data from a source (<c>media.category=Capture</c>).</summary>
    Capture,
    /// <summary>Sending data to a sink / publishing as a source (<c>media.category=Playback</c>).</summary>
    Playback,
}
