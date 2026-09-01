namespace PipeWire.NET.Graph;

/// <summary>
/// What a client may do with a registry object.
/// </summary>
/// <remarks>
/// The values match PipeWire's permission bits, which the C headers write in octal. C# has no octal
/// literals, so they are spelled in hex here: transcribing <c>0400</c> directly would yield decimal
/// 400 rather than 256.
/// </remarks>
[Flags]
public enum PipeWirePermissions : uint
{
    /// <summary>No access.</summary>
    None = 0,

    /// <summary>Metadata may be set on the object. C octal <c>0010</c>, since PipeWire 0.3.9.</summary>
    Metadata = 0x008,

    /// <summary>
    /// A link may be made to a node this client cannot otherwise see.
    /// C octal <c>0020</c>, since PipeWire 0.3.77.
    /// </summary>
    Link = 0x010,

    /// <summary>
    /// Methods may be called on the object. C octal <c>0100</c>. Modifying methods additionally
    /// require <see cref="Write"/>.
    /// </summary>
    Execute = 0x040,

    /// <summary>Methods that modify the object may be called. C octal <c>0200</c>.</summary>
    Write = 0x080,

    /// <summary>The object is visible and its events are delivered. C octal <c>0400</c>.</summary>
    Read = 0x100,
}
