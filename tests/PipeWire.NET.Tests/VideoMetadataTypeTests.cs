using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// The frame-metadata value types. They carry no logic, but they are the shape a consumer reads a
/// screencast through, so a field wired to the wrong source is a defect that only shows up as a
/// cursor in the wrong place.
/// </summary>
[TestClass]
public sealed class VideoMetadataTypeTests
{
    [TestMethod]
    public void ACursorCarriesItsPositionAndHotspotSeparately()
    {
        ReadOnlySpan<byte> pixels = [1, 2, 3, 4];
        VideoCursor cursor = new(
            id: 7, x: 100, y: 200, hotspotX: 3, hotspotY: 4,
            format: PixelFormat.Bgra, width: 32, height: 32, stride: 128, pixels: pixels);

        // Position and hotspot are different things: the hotspot is the offset within the bitmap
        // that actually points, so swapping them draws the cursor up and to the left of the mouse.
        Assert.AreEqual(7u, cursor.Id);
        Assert.AreEqual(100, cursor.X);
        Assert.AreEqual(200, cursor.Y);
        Assert.AreEqual(3, cursor.HotspotX);
        Assert.AreEqual(4, cursor.HotspotY);
        Assert.AreEqual(PixelFormat.Bgra, cursor.Format);
        Assert.AreEqual(32u, cursor.Width);
        Assert.AreEqual(32u, cursor.Height);
        Assert.AreEqual(128, cursor.Stride);
        Assert.AreEqual(4, cursor.Pixels.Length);
    }

    /// <summary>
    /// The common case: the pointer moved but its image did not, so no bitmap is sent and the
    /// consumer is expected to keep the last one. An empty span here must not read as "no cursor".
    /// </summary>
    [TestMethod]
    public void ACursorThatMovedWithoutChangingImageHasNoPixels()
    {
        VideoCursor cursor = new(
            id: 1, x: 5, y: 6, hotspotX: 0, hotspotY: 0,
            format: PixelFormat.Unknown, width: 0, height: 0, stride: 0, pixels: default);

        Assert.IsTrue(cursor.Pixels.IsEmpty);
        Assert.AreEqual(0u, cursor.Width);
        Assert.AreEqual(5, cursor.X);
    }

    [TestMethod]
    public void ARegionComparesByValue()
    {
        VideoRegion a = new(1, 2, 3, 4);
        VideoRegion b = new(1, 2, 3, 4);
        VideoRegion different = new(1, 2, 3, 5);

        Assert.AreEqual(a, b);
        Assert.AreNotEqual(a, different);
        Assert.AreEqual(1, a.X);
        Assert.AreEqual(2, a.Y);
        Assert.AreEqual(3u, a.Width);
        Assert.AreEqual(4u, a.Height);
    }
}
