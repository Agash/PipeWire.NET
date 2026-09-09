using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Drm;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Pins the SPA-to-DRM format mapping against GStreamer's <c>video-info-dma.c</c>, the table it is
/// transcribed from. A wrong entry here does not throw: it swaps colour channels, so the failure
/// surfaces as a subtly wrong image far from this code.
/// </summary>
[TestClass]
public sealed class DrmFormatTests
{
    /// <summary>Renders a fourcc back into the four characters it packs, for readable failures.</summary>
    private static string Tag(uint fourcc)
    {
        uint v = fourcc & ~DrmFourcc.DRM_FORMAT_BIG_ENDIAN;
        StringBuilder sb = new(4);
        for (int i = 0; i < 4; i++) sb.Append((char)((v >> (8 * i)) & 0xFF));
        return sb.ToString();
    }

    [TestMethod]
    public void FourccPacksItsCharactersLittleEndian()
    {
        // The property every other assertion here relies on being readable.
        Assert.AreEqual("NV12", Tag(DrmFourcc.DRM_FORMAT_NV12));
        Assert.AreEqual("AR24", Tag(DrmFourcc.DRM_FORMAT_ARGB8888));
    }

    /// <summary>
    /// The trap: a SPA name lists channels in memory order, a DRM fourcc names them in
    /// little-endian word order, so the two read as mirror images of each other. Mapping
    /// <c>Rgba</c> to <c>RGBA8888</c> "because the names match" swaps red and blue.
    /// </summary>
    [TestMethod]
    public void ByteOrderIsReversedBetweenSpaAndDrmNames()
    {
        Assert.AreEqual(DrmFourcc.DRM_FORMAT_ABGR8888, DrmFormat.FromVideoFormat(SpaVideoFormat.Rgba));
        Assert.AreEqual(DrmFourcc.DRM_FORMAT_ARGB8888, DrmFormat.FromVideoFormat(SpaVideoFormat.Bgra));
        Assert.AreEqual(DrmFourcc.DRM_FORMAT_RGBA8888, DrmFormat.FromVideoFormat(SpaVideoFormat.Abgr));
        Assert.AreEqual(DrmFourcc.DRM_FORMAT_BGRA8888, DrmFormat.FromVideoFormat(SpaVideoFormat.Argb));

        // Same reversal on the 24-bit pair, where it is easiest to get backwards.
        Assert.AreEqual(DrmFourcc.DRM_FORMAT_BGR888, DrmFormat.FromVideoFormat(SpaVideoFormat.Rgb));
        Assert.AreEqual(DrmFourcc.DRM_FORMAT_RGB888, DrmFormat.FromVideoFormat(SpaVideoFormat.Bgr));
    }

    [TestMethod]
    [DataRow(SpaVideoFormat.Nv12, "NV12")]
    [DataRow(SpaVideoFormat.Nv21, "NV21")]
    [DataRow(SpaVideoFormat.Nv16, "NV16")]
    [DataRow(SpaVideoFormat.Nv61, "NV61")]
    [DataRow(SpaVideoFormat.Nv24, "NV24")]
    [DataRow(SpaVideoFormat.I420, "YU12")]
    [DataRow(SpaVideoFormat.Yv12, "YV12")]
    [DataRow(SpaVideoFormat.Y42B, "YU16")]
    [DataRow(SpaVideoFormat.Y444, "YU24")]
    [DataRow(SpaVideoFormat.Y41B, "YU11")]
    [DataRow(SpaVideoFormat.Yuy2, "YUYV")]
    [DataRow(SpaVideoFormat.Yvyu, "YVYU")]
    [DataRow(SpaVideoFormat.Uyvy, "UYVY")]
    [DataRow(SpaVideoFormat.Vyuy, "VYUY")]
    [DataRow(SpaVideoFormat.Bgrx, "XR24")]
    [DataRow(SpaVideoFormat.Rgbx, "XB24")]
    [DataRow(SpaVideoFormat.P010_10Le, "P010")]
    [DataRow(SpaVideoFormat.Gray8, "R8  ")]
    public void MapsToTheFourccUpstreamNames(SpaVideoFormat format, string expected)
        => Assert.AreEqual(expected, Tag(DrmFormat.FromVideoFormat(format)));

    /// <summary>
    /// The big-endian grey pair share a fourcc and are told apart only by the high bit. Dropping it
    /// would silently alias two different layouts onto one.
    /// </summary>
    [TestMethod]
    public void BigEndianGreyIsDistinguishedByTheHighBit()
    {
        uint le = DrmFormat.FromVideoFormat(SpaVideoFormat.Gray16Le);
        uint be = DrmFormat.FromVideoFormat(SpaVideoFormat.Gray16Be);

        Assert.AreNotEqual(le, be, "the two grey layouts must not share a fourcc");
        Assert.AreEqual(0u, le & DrmFourcc.DRM_FORMAT_BIG_ENDIAN);
        Assert.AreEqual(DrmFourcc.DRM_FORMAT_BIG_ENDIAN, be & DrmFourcc.DRM_FORMAT_BIG_ENDIAN);
        Assert.AreEqual(le, be & ~DrmFourcc.DRM_FORMAT_BIG_ENDIAN);
    }

    /// <summary>
    /// DRM's <c>AYUV</c> is GStreamer's <c>VUYA</c>, a format SPA does not have. Upstream therefore
    /// maps neither, and SPA's own <c>Ayuv</c> must not be attached to it.
    /// </summary>
    [TestMethod]
    public void AyuvIsNotMappedBecauseItIsADifferentChannelOrder()
        => Assert.AreEqual(DrmFormat.Invalid, DrmFormat.FromVideoFormat(SpaVideoFormat.Ayuv));

    /// <summary>
    /// Walks the whole SPA enum. Two properties hold across it: the mapping never throws for any
    /// value, and no two distinct formats share a fourcc, since an alias would silently hand an
    /// importer the wrong channel order for one of them.
    /// </summary>
    [TestMethod]
    public void TheMappingIsTotalAndFreeOfAliases()
    {
        Dictionary<uint, SpaVideoFormat> seen = [];
        int mapped = 0;

        foreach (SpaVideoFormat format in Enum.GetValues<SpaVideoFormat>())
        {
            uint fourcc = DrmFormat.FromVideoFormat(format);
            if (fourcc == DrmFormat.Invalid)
                continue;

            mapped++;
            Assert.IsFalse(
                seen.TryGetValue(fourcc, out SpaVideoFormat other),
                $"{format} and {other} both map to {Tag(fourcc)}");
            seen[fourcc] = format;
        }

        // The count is pinned deliberately: upstream's table is the source of truth, and a change
        // to this number means the table moved and should be re-read rather than assumed.
        Assert.AreEqual(33, mapped, "the number of mapped formats changed");
    }

    [TestMethod]
    public void AnUnmappedFormatIsInvalidRatherThanAGuess()
    {
        Assert.AreEqual(DrmFormat.Invalid, DrmFormat.FromVideoFormat(SpaVideoFormat.Unknown));
        Assert.AreEqual(DrmFormat.Invalid, DrmFormat.FromPixelFormat(PixelFormat.Unknown));
    }

    /// <summary>The two overloads must not drift apart.</summary>
    [TestMethod]
    [DataRow(PixelFormat.Rgba, SpaVideoFormat.Rgba)]
    [DataRow(PixelFormat.Bgra, SpaVideoFormat.Bgra)]
    [DataRow(PixelFormat.Rgbx, SpaVideoFormat.Rgbx)]
    [DataRow(PixelFormat.Bgrx, SpaVideoFormat.Bgrx)]
    [DataRow(PixelFormat.Yuyv, SpaVideoFormat.Yuy2)]
    [DataRow(PixelFormat.Yuv420, SpaVideoFormat.I420)]
    [DataRow(PixelFormat.Nv12, SpaVideoFormat.Nv12)]
    public void ThePixelFormatOverloadAgreesWithTheSpaOne(PixelFormat pixel, SpaVideoFormat spa)
    {
        Assert.AreEqual(DrmFormat.FromVideoFormat(spa), DrmFormat.FromPixelFormat(pixel));
        Assert.AreNotEqual(DrmFormat.Invalid, DrmFormat.FromPixelFormat(pixel));
    }
}
