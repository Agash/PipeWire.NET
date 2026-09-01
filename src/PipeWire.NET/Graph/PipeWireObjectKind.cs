namespace PipeWire.NET.Graph;

/// <summary>The kind of graph object an <see cref="IPipeWireObject"/> represents.</summary>
public enum PipeWireObjectKind : byte
{
    /// <summary>A processing node: a device, an application stream, or a virtual sink.</summary>
    Node,

    /// <summary>An endpoint on a node that links attach to.</summary>
    Port,

    /// <summary>A connection between an output port and an input port.</summary>
    Link,
}
