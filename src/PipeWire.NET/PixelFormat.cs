namespace PipeWire.NET;

/// <summary>Pixel formats used by <see cref="PipeWireVideoCapture"/> and <see cref="PipeWireVideoOutput"/>.</summary>
public enum PixelFormat
{
    /// <summary>32-bit RGBA, 8 bits per channel.</summary>
    Rgba,

    /// <summary>32-bit BGRA, 8 bits per channel (common on X11/Wayland compositors).</summary>
    Bgra,

    /// <summary>YUV 4:2:0 planar (Y plane followed by interleaved UV plane).</summary>
    Yuv420,

    /// <summary>YUV 4:2:2 packed (YUYV byte order).</summary>
    Yuyv,

    /// <summary>32-bit RGBX (alpha channel ignored / always 0xFF).</summary>
    Rgbx,

    /// <summary>32-bit BGRX (alpha channel ignored / always 0xFF).</summary>
    Bgrx,
}
