using System.Runtime.Versioning;
using PipeWire.NET.Interop;

namespace PipeWire.NET;

/// <summary>How much PipeWire's own library logging says.</summary>
/// <remarks>
/// These are the levels the native library uses, not the ones <c>ILogger</c> uses. They control
/// what libpipewire writes about itself, which is separate from what this library logs.
/// </remarks>
public enum PipeWireLogLevel
{
    /// <summary>Nothing at all.</summary>
    None = 0,

    /// <summary>Failures only.</summary>
    Error = 1,

    /// <summary>Failures and things that will probably become failures.</summary>
    Warn = 2,

    /// <summary>Lifecycle: connections, formats, the shape of the session.</summary>
    Info = 3,

    /// <summary>Enough to follow what the library is doing.</summary>
    Debug = 4,

    /// <summary>Everything, including per-buffer activity on the realtime path.</summary>
    Trace = 5,
}
