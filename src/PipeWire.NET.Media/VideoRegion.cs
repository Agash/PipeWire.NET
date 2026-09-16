namespace PipeWire.NET.Media;

/// <summary>A rectangle within a frame.</summary>
/// <param name="X">Left edge, in pixels.</param>
/// <param name="Y">Top edge, in pixels.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct VideoRegion(int X, int Y, uint Width, uint Height);
