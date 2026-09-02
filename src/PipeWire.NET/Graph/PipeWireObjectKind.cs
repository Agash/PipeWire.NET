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

    /// <summary>A hardware device, from which nodes are created.</summary>
    Device,

    /// <summary>A connection to the daemon: an application, or this process.</summary>
    Client,

    /// <summary>Something the daemon can create objects with.</summary>
    Factory,

    /// <summary>A module loaded into the daemon.</summary>
    Module,

    /// <summary>A named store of settings shared between clients.</summary>
    Metadata,

    /// <summary>The daemon profiler, which reports graph timing.</summary>
    Profiler,

    /// <summary>A restricted connection handed to a sandboxed application.</summary>
    SecurityContext,

    /// <summary>The daemon core itself.</summary>
    Core,
}
