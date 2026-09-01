namespace PipeWire.NET;

/// <summary>Which way media moves through a node, from the graph's point of view.</summary>
/// <remarks>
/// Named for the graph, not the application: an app publishing audio is <c>Stream/Output/Audio</c>
/// and is a <see cref="Source"/> here, because the graph reads from it.
/// </remarks>
public enum PipeWireMediaFlow
{
    /// <summary>No <c>media.class</c>, or one whose direction this library does not recognise.</summary>
    Unknown,

    /// <summary>The graph reads from this node.</summary>
    Source,

    /// <summary>The graph writes to this node.</summary>
    Sink,

    /// <summary>Both, e.g. a MIDI bridge or a duplex device.</summary>
    Duplex,
}
