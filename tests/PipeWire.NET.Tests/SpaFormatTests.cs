using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;
using PipeWire.NET.Media;

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
        // Unknown is the sentinel for "not one of ours" and has no spa counterpart, so it is the one
        // member that cannot round-trip. Every real format must.
        var broken = new List<string>();
        foreach (PixelFormat fmt in Enum.GetValues<PixelFormat>())
        {
            if (fmt == PixelFormat.Unknown) continue;

            PixelFormat back = SpaFormatPod.FromSpaVideoFormat(SpaFormatPod.ToSpaVideoFormat(fmt));
            if (back != fmt) broken.Add($"{fmt} -> {back}");
        }

        CollectionAssert.AreEqual(Array.Empty<string>(), broken,
            "these formats do not survive the round trip and would silently be treated as another format");
    }

    [TestMethod]
    public void EveryPixelFormat_MapsToADistinctSpaFormat()
    {
        // Two formats sharing a spa id means one of them is unreachable coming back.
        var seen = new Dictionary<SpaVideoFormat, PixelFormat>();
        foreach (PixelFormat fmt in Enum.GetValues<PixelFormat>())
        {
            if (fmt == PixelFormat.Unknown) continue;

            SpaVideoFormat spa = SpaFormatPod.ToSpaVideoFormat(fmt);
            Assert.IsFalse(seen.TryGetValue(spa, out PixelFormat other),
                $"{fmt} and {other} both map to spa format {spa}");
            seen[spa] = fmt;
        }
    }

    [TestMethod]
    public void AnUnknownSpaVideoFormat_IsReportedAsUnknownRatherThanGuessed()
    {
        // Reinterpreting an unrecognised layout as a known one produces a plausible-looking but
        // wrong image, which is far harder to notice than an unsupported format, so the sentinel
        // is the answer, and it still does not throw.
        Assert.AreEqual(PixelFormat.Unknown, SpaFormatPod.FromSpaVideoFormat((SpaVideoFormat)0xDEADBEEF));
    }

    [TestMethod]
    public void AnUnknownSpaAudioFormat_IsReportedAsUnknownRatherThanGuessed()
    {
        // An unrecognised format must not read as a real one, or a consumer reads four-byte
        // floats out of whatever was actually negotiated. The audio map behaves like the video
        // map above.
        Assert.AreEqual(AudioSampleFormat.Unknown, SpaFormatPod.FromSpaAudioFormat((SpaAudioFormat)0xDEADBEEF));

        // Two the daemon really does negotiate.
        Assert.AreEqual(AudioSampleFormat.S24_32Le, SpaFormatPod.FromSpaAudioFormat(SpaAudioFormat.S24_32Le));
        Assert.AreEqual(AudioSampleFormat.F64Le, SpaFormatPod.FromSpaAudioFormat(SpaAudioFormat.F64Le));
    }

    [TestMethod]
    public void EveryAudioSampleFormat_SurvivesARoundTrip()
    {
        foreach (AudioSampleFormat fmt in Enum.GetValues<AudioSampleFormat>())
        {
            // Unknown is an answer, never a request: there is nothing to ask the daemon for, so
            // offering it is a caller mistake rather than a round trip.
            if (fmt is AudioSampleFormat.Unknown) continue;

            Assert.AreEqual(fmt, SpaFormatPod.FromSpaAudioFormat(SpaFormatPod.ToSpaAudioFormat(fmt)),
                $"{fmt} does not round-trip");
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormatPod.ToSpaAudioFormat(AudioSampleFormat.Unknown));
    }

    // ---------------------------------------------------------------- buffer arithmetic

    /// <summary>Every format with a layout this library knows.</summary>
    /// <remarks>
    /// Unknown is deliberately not one of them. It is what a negotiation reports when the producer
    /// named a format this version does not model, so there is no layout to compute and every size
    /// function refuses it - which is the point of
    /// <see cref="TheArithmeticRefusesAFormatItDoesNotKnow"/>. Sweeping it through the loops below
    /// would be asserting that a guess is available.
    /// </remarks>
    private static IEnumerable<PixelFormat> KnownFormats =>
        Enum.GetValues<PixelFormat>().Where(static f => f != PixelFormat.Unknown);

    [TestMethod]
    public void TheArithmeticRefusesAFormatItDoesNotKnow()
    {
        // Guessing four bytes per pixel for an unmodelled format sizes every buffer and stride
        // derived from it wrongly, and the frame that comes back renders as a tinted, sheared image
        // rather than failing anywhere a caller can see.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormatPod.BytesPerPixel(PixelFormat.Unknown));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormatPod.VideoStride(PixelFormat.Unknown, 640));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormatPod.VideoImageSize(PixelFormat.Unknown, 640, 480));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormatPod.ToSpaVideoFormat(PixelFormat.Unknown));
    }

    [TestMethod]
    public void StrideAlwaysEqualsWidthTimesBytesPerPixel()
    {
        // Two functions computing the same thing independently; if they drift, a consumer walks
        // rows at the wrong pitch and every frame shears.
        foreach (PixelFormat fmt in KnownFormats)
            foreach (int w in (int[])[1, 2, 3, 640, 1920, 4096])
                Assert.AreEqual(w * SpaFormatPod.BytesPerPixel(fmt), SpaFormatPod.VideoStride(fmt, w),
                    $"{fmt} at width {w}: stride and bytes-per-pixel disagree");
    }

    [TestMethod]
    public void TheTwoPlaneCountFunctionsAgree()
    {
        // VideoPlaneCount and PlaneCount are separate implementations of the same fact.
        foreach (PixelFormat fmt in Enum.GetValues<PixelFormat>())
            Assert.AreEqual(SpaFormatPod.PlaneCount(fmt), SpaFormatPod.VideoPlaneCount(fmt),
                $"{fmt}: PlaneCount and VideoPlaneCount disagree");
    }

    [TestMethod]
    [DataRow(PixelFormat.Bgra, 640, 480, 640 * 480 * 4)]
    [DataRow(PixelFormat.Rgba, 1920, 1080, 1920 * 1080 * 4)]
    [DataRow(PixelFormat.Yuyv, 640, 480, 640 * 480 * 2)]
    [DataRow(PixelFormat.Yuv420, 640, 480, 640 * 480 * 3 / 2)]
    [DataRow(PixelFormat.Nv12, 640, 480, 640 * 480 * 3 / 2)]
    public void ImageSize_MatchesTheFormatLayout(PixelFormat fmt, int w, int h, int expected) =>
        Assert.AreEqual(expected, SpaFormatPod.VideoImageSize(fmt, w, h));

    [TestMethod]
    public void ImageSize_IsNeverSmallerThanOneFullPlaneOfRows()
    {
        // The absolute floor for any layout: the primary plane must fit. An allocation below this
        // is an overflow waiting to happen in whoever writes the frame.
        foreach (PixelFormat fmt in KnownFormats)
            foreach ((int w, int h) in (( int, int )[])[(1, 1), (2, 2), (3, 3), (17, 13), (640, 480), (1920, 1080)])
            {
                int size = SpaFormatPod.VideoImageSize(fmt, w, h);
                int primaryPlane = SpaFormatPod.VideoStride(fmt, w) * h;
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
            int size = SpaFormatPod.VideoImageSize(fmt, w, h);
            Assert.IsTrue(size >= required,
                $"{fmt} {w}x{h}: allocates {size} but the layout needs {required}");
        }
    }

    [TestMethod]
    public void ImageSize_RefusesAFrameItCannotAddressRatherThanWrapping()
    {
        // Realistic sizes must still compute.
        foreach (PixelFormat fmt in KnownFormats)
            foreach ((int w, int h) in (( int, int )[])[(7680, 4320), (16384, 16384)])
                Assert.IsTrue(SpaFormatPod.VideoImageSize(fmt, w, h) > 0, $"{fmt} {w}x{h} should compute");

        // 32768 square at 32bpp is exactly 2^32 bytes, which must be refused rather than
        // truncating to a zero-sized buffer.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormatPod.VideoImageSize(PixelFormat.Rgba, 32768, 32768));
    }

    [TestMethod]
    public void ImageSize_RejectsNegativeDimensions()
    {
        // Dimensions come off a negotiated param, so a nonsense value must not reach the arithmetic.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormatPod.VideoImageSize(PixelFormat.Bgra, -1, 480));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormatPod.VideoImageSize(PixelFormat.Bgra, 640, -1));
    }

    [TestMethod]
    public void Stride_ComputesForLargeWidthsAndRefusesImpossibleOnes()
    {
        foreach (PixelFormat fmt in KnownFormats)
            foreach (int w in (int[])[16384, 65536, 268_435_456])
                Assert.IsTrue(SpaFormatPod.VideoStride(fmt, w) > 0,
                    $"{fmt} at width {w}: stride computed as {SpaFormatPod.VideoStride(fmt, w)}");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormatPod.VideoStride(PixelFormat.Bgra, int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SpaFormatPod.VideoStride(PixelFormat.Bgra, -1));
    }

    // ---------------------------------------------------------------- buffer types

    [TestMethod]
    public void BufferTypeMapping_CoversTheThreeRealTypesAndRejectsTheRest()
    {
        Assert.AreEqual(PipeWireBufferType.MemPtr, SpaFormatPod.ToBufferType(SpaDataType.MemPtr));
        Assert.AreEqual(PipeWireBufferType.MemFd, SpaFormatPod.ToBufferType(SpaDataType.MemFd));
        Assert.AreEqual(PipeWireBufferType.DmaBuf, SpaFormatPod.ToBufferType(SpaDataType.DmaBuf));
        Assert.AreEqual(PipeWireBufferType.Unknown, SpaFormatPod.ToBufferType((SpaDataType)0));
        Assert.AreEqual(PipeWireBufferType.Unknown, SpaFormatPod.ToBufferType((SpaDataType)uint.MaxValue));
    }

    [TestMethod]
    public void TheCaptureDataTypeMask_AdvertisesExactlyTheTypesWeCanDecode()
    {
        int mask = SpaFormatPod.VideoCaptureDataTypeMask;

        // Advertising a type we cannot handle makes the producer hand us buffers we then drop.
        foreach (SpaDataType t in (SpaDataType[])[SpaDataType.MemPtr, SpaDataType.MemFd, SpaDataType.DmaBuf])
        {
            Assert.AreNotEqual(0, mask & (1 << (int)t), $"data type {t} must be advertised");
            Assert.AreNotEqual(PipeWireBufferType.Unknown, SpaFormatPod.ToBufferType(t),
                $"data type {t} is advertised but does not map to a known buffer type");
        }
    }

    // ---------------------------------------------------------------- colour metadata

    [TestMethod]
    public void UnknownColourMetadata_MapsToUnknownRatherThanAPlausibleDefault()
    {
        // Guessing BT.709 for an unknown matrix would silently mis-convert colour.
        Assert.AreEqual(VideoColorRange.Unknown, SpaFormatPod.MapColorRange((SpaVideoColorRange)0xDEADBEEF));
        Assert.AreEqual(VideoColorMatrix.Unknown, SpaFormatPod.MapColorMatrix((SpaVideoColorMatrix)0xDEADBEEF));
        Assert.AreEqual(VideoTransferFunction.Unknown, SpaFormatPod.MapTransfer((SpaVideoTransferFunction)0xDEADBEEF));
        Assert.AreEqual(VideoColorPrimaries.Unknown, SpaFormatPod.MapPrimaries((SpaVideoColorPrimaries)0xDEADBEEF));
    }

    [TestMethod]
    public void KnownColourMetadata_MapsToTheMatchingMember()
    {
        Assert.AreEqual(VideoColorRange.Full_0_255,
            SpaFormatPod.MapColorRange(SpaVideoColorRange.Full));
        Assert.AreEqual(VideoColorRange.Limited_16_235,
            SpaFormatPod.MapColorRange(SpaVideoColorRange.Limited));
        Assert.AreEqual(VideoColorMatrix.Bt2020,
            SpaFormatPod.MapColorMatrix(SpaVideoColorMatrix.Bt2020));
        Assert.AreEqual(VideoTransferFunction.Srgb,
            SpaFormatPod.MapTransfer(SpaVideoTransferFunction.Srgb));
        Assert.AreEqual(VideoColorPrimaries.Bt709,
            SpaFormatPod.MapPrimaries(SpaVideoColorPrimaries.Bt709));
    }

    // ---------------------------------------------------------------- param writing

    [TestMethod]
    public void WriteVideoFormat_RefusesABufferTooSmallRatherThanTruncating()
    {
        // A truncated param is a malformed pod the daemon will reject or misparse.
        Span<byte> tiny = stackalloc byte[8];
        try
        {
            _ = SpaFormatPod.WriteVideoFormat(tiny, [PixelFormat.Bgra], 640, 480, 30, fixedSize: true);
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
        int written = SpaFormatPod.WriteVideoFormat(buf, [PixelFormat.Bgra], 1280, 720, 60, fixedSize: true);

        Assert.IsTrue(written > 0 && written <= buf.Length);

        var reader = new SpaPodReader(buf[..written]);
        Assert.IsTrue(reader.EnterObject(out uint objType, out _, out _),
            "what we write must be a well-formed object pod");
        Assert.AreEqual(SpaType.ObjectFormat, (SpaType)objType);
    }

    [TestMethod]
    public void WriteHeaderMetaParam_AndBuffersParam_ProduceParseablePods()
    {
        Span<byte> meta = stackalloc byte[128];
        int m = SpaFormatPod.WriteHeaderMetaParam(meta);
        Assert.IsTrue(m > 0);
        Assert.IsTrue(new SpaPodReader(meta[..m]).EnterObject(out _, out _, out _));

        Span<byte> buffers = stackalloc byte[256];
        int b = SpaFormatPod.WriteVideoBuffersParam(buffers, size: 1024, stride: 64, dataTypes: 1, blocks: 1);
        Assert.IsTrue(b > 0);
        Assert.IsTrue(new SpaPodReader(buffers[..b]).EnterObject(out _, out _, out _));
    }

    [TestMethod]
    public void PlanarFormats_SizeTheWholeFrameNotJustTheLumaPlane()
    {
        // NV12 and I420 carry half again the luma plane in chroma, so stride * height drops a third
        // of every frame.
        foreach (PixelFormat fmt in (PixelFormat[])[PixelFormat.Nv12, PixelFormat.Yuv420])
        {
            Assert.AreEqual(320 * 240 * 3 / 2, SpaFormatPod.VideoImageSize(fmt, 320, 240));
            Assert.IsTrue(SpaFormatPod.VideoImageSize(fmt, 320, 240) > SpaFormatPod.VideoStride(fmt, 320) * 240);
        }
    }

    [TestMethod]
    public void TheDefaultVideoOffer_NamesEveryFormatThisLibrarySupports()
    {
        // A supported format missing from the default offer works only when the caller names it
        // explicitly, which looks like the producer not offering it.
        Span<byte> buf = stackalloc byte[1024];
        int len = SpaFormatPod.WriteVideoFormat(buf, [], 1920, 1080, 30, fixedSize: false);

        Assert.IsTrue(SpaPod.TryParse(buf[..len], out SpaValue? value));
        var format = (SpaObject)value!;
        var choice = (SpaChoice)format[SpaFormat.VideoFormat]!;

        var offered = choice.Alternatives
            .OfType<SpaId>()
            .Select(id => SpaFormatPod.FromSpaVideoFormat((SpaVideoFormat)id.Value))
            .ToHashSet();

        var supported = Enum.GetValues<PixelFormat>().Where(f => f != PixelFormat.Unknown).ToList();

        var missing = supported.Where(f => !offered.Contains(f)).ToList();
        CollectionAssert.AreEqual(Array.Empty<PixelFormat>(), missing,
            $"these supported formats are not offered by default: {string.Join(", ", missing)}");
    }
}
