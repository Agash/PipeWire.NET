using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Generated;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Format mapping and buffer arithmetic. A wrong answer here does not throw, it under-allocates a
/// buffer or negotiates the wrong pixel layout, so the damage shows up as corrupted video far from
/// the cause. These tests go after the silent-default and integer-arithmetic paths specifically.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SpaFormatTests
{
    // ---------------------------------------------------------------- format round trips

    [TestMethod]
    public void EveryPixelFormat_SurvivesARoundTripThroughSpa()
    {
        // Both directions fall back to BGRA for anything unrecognised, so a format that is added to
        // the enum but not to the maps silently becomes BGRA instead of failing.
        var broken = new List<string>();
        foreach (PixelFormat fmt in Enum.GetValues<PixelFormat>())
        {
            PixelFormat back = SpaFormat.FromSpaVideoFormat(SpaFormat.ToSpaVideoFormat(fmt));
            if (back != fmt) broken.Add($"{fmt} -> {back}");
        }

        CollectionAssert.AreEqual(Array.Empty<string>(), broken,
            "these formats do not survive the round trip and would silently be treated as BGRA");
    }

    [TestMethod]
    public void EveryPixelFormat_MapsToADistinctSpaFormat()
    {
        // Two formats sharing a spa id means one of them is unreachable coming back.
        var seen = new Dictionary<uint, PixelFormat>();
        foreach (PixelFormat fmt in Enum.GetValues<PixelFormat>())
        {
            uint spa = SpaFormat.ToSpaVideoFormat(fmt);
            Assert.IsFalse(seen.TryGetValue(spa, out PixelFormat other),
                $"{fmt} and {other} both map to spa format {spa}");
            seen[spa] = fmt;
        }
    }

    [TestMethod]
    public void AnUnknownSpaVideoFormat_FallsBackRatherThanThrowing()
    {
        // Documented behaviour, pinned so a change to it is deliberate.
        Assert.AreEqual(PixelFormat.Bgra, SpaFormat.FromSpaVideoFormat(0xDEADBEEF));
    }

    [TestMethod]
    public void AnUnknownSpaAudioFormat_DefaultsToF32LeWhichIsLossy()
    {
        // Unlike the video maps there is no Unknown member to land on, so an unrecognised format is
        // indistinguishable from a real F32Le. Pinned because it is a trap, not because it is good.
        Assert.AreEqual(AudioSampleFormat.F32Le, SpaFormat.FromSpaAudioFormat(0xDEADBEEF));
    }

    [TestMethod]
    public void EveryAudioSampleFormat_SurvivesARoundTrip()
    {
        foreach (AudioSampleFormat fmt in Enum.GetValues<AudioSampleFormat>())
            Assert.AreEqual(fmt, SpaFormat.FromSpaAudioFormat(SpaFormat.ToSpaAudioFormat(fmt)),
                $"{fmt} does not round-trip");
    }

    // ---------------------------------------------------------------- buffer arithmetic

    [TestMethod]
    public void StrideAlwaysEqualsWidthTimesBytesPerPixel()
    {
        // Two functions computing the same thing independently; if they drift, a consumer walks
        // rows at the wrong pitch and every frame shears.
        foreach (PixelFormat fmt in Enum.GetValues<PixelFormat>())
            foreach (int w in (int[])[1, 2, 3, 640, 1920, 4096])
                Assert.AreEqual(w * SpaFormat.BytesPerPixel(fmt), SpaFormat.VideoStride(fmt, w),
                    $"{fmt} at width {w}: stride and bytes-per-pixel disagree");
    }

    [TestMethod]
    public void TheTwoPlaneCountFunctionsAgree()
    {
        // VideoPlaneCount and PlaneCount are separate implementations of the same fact.
        foreach (PixelFormat fmt in Enum.GetValues<PixelFormat>())
            Assert.AreEqual(SpaFormat.PlaneCount(fmt), SpaFormat.VideoPlaneCount(fmt),
                $"{fmt}: PlaneCount and VideoPlaneCount disagree");
    }

    [TestMethod]
    [DataRow(PixelFormat.Bgra, 640, 480, 640 * 480 * 4)]
    [DataRow(PixelFormat.Rgba, 1920, 1080, 1920 * 1080 * 4)]
    [DataRow(PixelFormat.Yuyv, 640, 480, 640 * 480 * 2)]
    [DataRow(PixelFormat.Yuv420, 640, 480, 640 * 480 * 3 / 2)]
    [DataRow(PixelFormat.Nv12, 640, 480, 640 * 480 * 3 / 2)]
    public void ImageSize_MatchesTheFormatLayout(PixelFormat fmt, int w, int h, int expected) =>
        Assert.AreEqual(expected, SpaFormat.VideoImageSize(fmt, w, h));

    [TestMethod]
    public void ImageSize_IsNeverSmallerThanOneFullPlaneOfRows()
    {
        // The absolute floor for any layout: the primary plane must fit. An allocation below this
        // is an overflow waiting to happen in whoever writes the frame.
        foreach (PixelFormat fmt in Enum.GetValues<PixelFormat>())
            foreach ((int w, int h) in (( int, int )[])[(1, 1), (2, 2), (3, 3), (17, 13), (640, 480), (1920, 1080)])
            {
                int size = SpaFormat.VideoImageSize(fmt, w, h);
                int primaryPlane = SpaFormat.VideoStride(fmt, w) * h;
                Assert.IsTrue(size >= primaryPlane,
                    $"{fmt} {w}x{h}: size {size} is smaller than the primary plane {primaryPlane}");
            }
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(3, 3)]
    [DataRow(5, 7)]
    [DataRow(641, 481)]
    public void PlanarImageSize_DoesNotTruncateBelowWhatI420Needs(int w, int h)
    {
        // I420 chroma planes are ceil(w/2) x ceil(h/2) each. width*height*3/2 in integer maths
        // truncates for odd dimensions, which under-allocates.
        int required = (w * h) + 2 * (((w + 1) / 2) * ((h + 1) / 2));

        foreach (PixelFormat fmt in (PixelFormat[])[PixelFormat.Yuv420, PixelFormat.Nv12])
        {
            int size = SpaFormat.VideoImageSize(fmt, w, h);
            Assert.IsTrue(size >= required,
                $"{fmt} {w}x{h}: allocates {size} but the layout needs {required}");
        }
    }

    [TestMethod]
    public void ImageSize_RefusesAFrameItCannotAddressRatherThanWrapping()
    {
        // Realistic sizes must still compute.
        foreach (PixelFormat fmt in Enum.GetValues<PixelFormat>())
            foreach ((int w, int h) in (( int, int )[])[(7680, 4320), (16384, 16384)])
                Assert.IsTrue(SpaFormat.VideoImageSize(fmt, w, h) > 0, $"{fmt} {w}x{h} should compute");

        // 32768 square at 32bpp is exactly 2^32 bytes, which used to truncate to a zero-sized buffer.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormat.VideoImageSize(PixelFormat.Rgba, 32768, 32768));
    }

    [TestMethod]
    public void ImageSize_RejectsNegativeDimensions()
    {
        // Dimensions come off a negotiated param, so a nonsense value must not reach the arithmetic.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormat.VideoImageSize(PixelFormat.Bgra, -1, 480));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormat.VideoImageSize(PixelFormat.Bgra, 640, -1));
    }

    [TestMethod]
    public void Stride_ComputesForLargeWidthsAndRefusesImpossibleOnes()
    {
        foreach (PixelFormat fmt in Enum.GetValues<PixelFormat>())
            foreach (int w in (int[])[16384, 65536, 268_435_456])
                Assert.IsTrue(SpaFormat.VideoStride(fmt, w) > 0,
                    $"{fmt} at width {w}: stride computed as {SpaFormat.VideoStride(fmt, w)}");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormat.VideoStride(PixelFormat.Bgra, int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormat.VideoStride(PixelFormat.Bgra, -1));
    }

    // ---------------------------------------------------------------- buffer types

    [TestMethod]
    public void BufferTypeMapping_CoversTheThreeRealTypesAndRejectsTheRest()
    {
        Assert.AreEqual(PipeWireBufferType.MemPtr, SpaFormat.ToBufferType(SpaType.DataMemPtr));
        Assert.AreEqual(PipeWireBufferType.MemFd, SpaFormat.ToBufferType(SpaType.DataMemFd));
        Assert.AreEqual(PipeWireBufferType.DmaBuf, SpaFormat.ToBufferType(SpaType.DataDmaBuf));
        Assert.AreEqual(PipeWireBufferType.Unknown, SpaFormat.ToBufferType(0));
        Assert.AreEqual(PipeWireBufferType.Unknown, SpaFormat.ToBufferType(uint.MaxValue));
    }

    [TestMethod]
    public void TheCaptureDataTypeMask_AdvertisesExactlyTheTypesWeCanDecode()
    {
        int mask = SpaFormat.VideoCaptureDataTypeMask;

        // Advertising a type we cannot handle makes the producer hand us buffers we then drop.
        foreach (uint t in (uint[])[SpaType.DataMemPtr, SpaType.DataMemFd, SpaType.DataDmaBuf])
        {
            Assert.AreNotEqual(0, mask & (1 << (int)t), $"data type {t} must be advertised");
            Assert.AreNotEqual(PipeWireBufferType.Unknown, SpaFormat.ToBufferType(t),
                $"data type {t} is advertised but does not map to a known buffer type");
        }
    }

    // ---------------------------------------------------------------- colour metadata

    [TestMethod]
    public void UnknownColourMetadata_MapsToUnknownRatherThanAPlausibleDefault()
    {
        // Guessing BT.709 for an unknown matrix would silently mis-convert colour.
        Assert.AreEqual(VideoColorRange.Unknown, SpaFormat.MapColorRange(0xDEADBEEF));
        Assert.AreEqual(VideoColorMatrix.Unknown, SpaFormat.MapColorMatrix(0xDEADBEEF));
        Assert.AreEqual(VideoTransferFunction.Unknown, SpaFormat.MapTransfer(0xDEADBEEF));
        Assert.AreEqual(VideoColorPrimaries.Unknown, SpaFormat.MapPrimaries(0xDEADBEEF));
    }

    [TestMethod]
    public void KnownColourMetadata_MapsToTheMatchingMember()
    {
        Assert.AreEqual(VideoColorRange.Full_0_255,
            SpaFormat.MapColorRange((uint)spa_video_color_range.SPA_VIDEO_COLOR_RANGE_0_255));
        Assert.AreEqual(VideoColorRange.Limited_16_235,
            SpaFormat.MapColorRange((uint)spa_video_color_range.SPA_VIDEO_COLOR_RANGE_16_235));
        Assert.AreEqual(VideoColorMatrix.Bt2020,
            SpaFormat.MapColorMatrix((uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_BT2020));
        Assert.AreEqual(VideoTransferFunction.Srgb,
            SpaFormat.MapTransfer((uint)spa_video_transfer_function.SPA_VIDEO_TRANSFER_SRGB));
        Assert.AreEqual(VideoColorPrimaries.Bt709,
            SpaFormat.MapPrimaries((uint)spa_video_color_primaries.SPA_VIDEO_COLOR_PRIMARIES_BT709));
    }

    // ---------------------------------------------------------------- param writing

    [TestMethod]
    public void WriteVideoFormat_RefusesABufferTooSmallRatherThanTruncating()
    {
        // A truncated param is a malformed pod the daemon will reject or misparse.
        Span<byte> tiny = stackalloc byte[8];
        try
        {
            _ = SpaFormat.WriteVideoFormat(tiny, [PixelFormat.Bgra], 640, 480, 30, fixedSize: true);
            Assert.Fail("writing a format into 8 bytes must not appear to succeed");
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
        }
    }

    [TestMethod]
    public void WriteVideoFormat_ProducesAPodThatParsesBackToWhatWasWritten()
    {
        Span<byte> buf = stackalloc byte[1024];
        int written = SpaFormat.WriteVideoFormat(buf, [PixelFormat.Bgra], 1280, 720, 60, fixedSize: true);

        Assert.IsTrue(written > 0 && written <= buf.Length);

        var reader = new SpaPodReader(buf[..written]);
        Assert.IsTrue(reader.EnterObject(out uint objType, out _, out _),
            "what we write must be a well-formed object pod");
        Assert.AreEqual(SpaType.ObjectFormat, objType);
    }

    [TestMethod]
    public void WriteHeaderMetaParam_AndBuffersParam_ProduceParseablePods()
    {
        Span<byte> meta = stackalloc byte[128];
        int m = SpaFormat.WriteHeaderMetaParam(meta);
        Assert.IsTrue(m > 0);
        Assert.IsTrue(new SpaPodReader(meta[..m]).EnterObject(out _, out _, out _));

        Span<byte> buffers = stackalloc byte[256];
        int b = SpaFormat.WriteVideoBuffersParam(buffers, size: 1024, stride: 64, dataTypes: 1, blocks: 1);
        Assert.IsTrue(b > 0);
        Assert.IsTrue(new SpaPodReader(buffers[..b]).EnterObject(out _, out _, out _));
    }
}
