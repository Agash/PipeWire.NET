namespace PipeWire.NET.Media;

/// <summary>
/// Whether a capture keeps its most recent frame for pulling, and in what form.
/// </summary>
/// <remarks>
/// The push path (a handler on the capture's frame event) always runs. This decides only what, if
/// anything, is additionally retained so a consumer on its own clock can ask for the current frame.
/// </remarks>
public enum FrameRetention
{
    /// <summary>Keep nothing. The default, and free.</summary>
    None = 0,

    /// <summary>
    /// Keep a frame that owns what it carries: host bytes copied, dmabuf descriptors duplicated.
    /// Costs an allocation and a <c>dup</c> per plane per cycle, and the frame must be disposed.
    /// Choose this when a frame has to outlive the cycle it arrived on.
    /// </summary>
    Owned,

    /// <summary>
    /// Keep a frame by value, borrowing the pool's descriptors. Allocates nothing and closes
    /// nothing, but the contents are only good until the producer recycles the buffer. Choose this
    /// for a GPU consumer that imports and submits promptly.
    /// </summary>
    Borrowed,
}
