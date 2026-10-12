namespace PipeWire.NET.Media;

/// <summary>A point on an explicit-sync timeline: a DRM syncobj (or its eventfd stand-in) and a value.</summary>
/// <param name="Fd">The timeline's descriptor, borrowed from the buffer: valid while the buffer is.</param>
/// <param name="Point">The value that is reached when the frame is ready.</param>
public readonly record struct PipeWireSyncPoint(int Fd, ulong Point);
