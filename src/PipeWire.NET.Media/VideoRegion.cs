namespace PipeWire.NET.Media;

/// <summary>A rectangle within a frame.</summary>
/// <param name="X">Left edge, in pixels.</param>
/// <param name="Y">Top edge, in pixels.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct VideoRegion(int X, int Y, uint Width, uint Height);


/// <summary>
/// Where the pointer is, and optionally what it looks like.
/// </summary>
/// <remarks>
/// A screencast delivers the cursor as metadata rather than painting it into the frame, so a
/// consumer can draw it at its own rate - or not at all. For a transport that matters twice over:
/// the cursor moves far more often than the screen changes, and a receiver that renders it locally
/// gets a pointer that tracks the mouse rather than the frame rate.
/// <para>
/// The bitmap is sent only when it changes, so <see cref="Pixels"/> is usually empty even while
/// the position keeps moving. A consumer is expected to keep the last one it saw.
/// </para>
/// </remarks>
public readonly ref struct VideoCursor
{
    internal VideoCursor(
        uint id, int x, int y, int hotspotX, int hotspotY,
        PixelFormat format, uint width, uint height, int stride, ReadOnlySpan<byte> pixels)
    {
        Id = id;
        X = x;
        Y = y;
        HotspotX = hotspotX;
        HotspotY = hotspotY;
        Format = format;
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    /// <summary>The cursor's id. Changes when the pointer image changes.</summary>
    public uint Id { get; }

    /// <summary>Horizontal position on screen.</summary>
    public int X { get; }

    /// <summary>Vertical position on screen.</summary>
    public int Y { get; }

    /// <summary>Horizontal offset of the hotspot within the bitmap.</summary>
    public int HotspotX { get; }

    /// <summary>Vertical offset of the hotspot within the bitmap.</summary>
    public int HotspotY { get; }

    /// <summary>The bitmap's pixel format, when one is present.</summary>
    public PixelFormat Format { get; }

    /// <summary>Bitmap width in pixels, or 0 when no bitmap came with this frame.</summary>
    public uint Width { get; }

    /// <summary>Bitmap height in pixels, or 0 when no bitmap came with this frame.</summary>
    public uint Height { get; }

    /// <summary>Bytes per row of the bitmap.</summary>
    public int Stride { get; }

    /// <summary>
    /// The bitmap's pixels, empty when this frame carried no new one.
    /// </summary>
    /// <remarks>Valid only for the duration of the frame handler, like the frame's own data.</remarks>
    public ReadOnlySpan<byte> Pixels { get; }
}
