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
        // floats out of whatever was actually negotiated.
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

        // Every member is mapped or explicitly unknown: a quiet default here mistranslates
        // colour, so the map is total by test rather than by inspection.
        foreach ((SpaVideoColorMatrix spa, VideoColorMatrix expected) in new[]
        {
            (SpaVideoColorMatrix.Unknown, VideoColorMatrix.Unknown),
            (SpaVideoColorMatrix.Rgb, VideoColorMatrix.Rgb),
            (SpaVideoColorMatrix.Fcc, VideoColorMatrix.Unknown),
            (SpaVideoColorMatrix.Bt709, VideoColorMatrix.Bt709),
            (SpaVideoColorMatrix.Bt601, VideoColorMatrix.Bt601),
            (SpaVideoColorMatrix.Smpte240M, VideoColorMatrix.Unknown),
            (SpaVideoColorMatrix.Bt2020, VideoColorMatrix.Bt2020),
        })
            Assert.AreEqual(expected, SpaFormatPod.MapColorMatrix(spa), $"matrix {spa}");

        foreach ((SpaVideoTransferFunction spa, VideoTransferFunction expected) in new[]
        {
            (SpaVideoTransferFunction.Unknown, VideoTransferFunction.Unknown),
            (SpaVideoTransferFunction.Gamma22, VideoTransferFunction.Gamma22),
            (SpaVideoTransferFunction.Bt709, VideoTransferFunction.Bt709),
            (SpaVideoTransferFunction.Srgb, VideoTransferFunction.Srgb),
            (SpaVideoTransferFunction.Bt2020_12, VideoTransferFunction.Bt2020_12),
            (SpaVideoTransferFunction.Gamma10, VideoTransferFunction.Unknown),
            (SpaVideoTransferFunction.Smpte2084, VideoTransferFunction.Unknown),
        })
            Assert.AreEqual(expected, SpaFormatPod.MapTransfer(spa), $"transfer {spa}");

        foreach ((SpaVideoColorPrimaries spa, VideoColorPrimaries expected) in new[]
        {
            (SpaVideoColorPrimaries.Bt709, VideoColorPrimaries.Bt709),
            (SpaVideoColorPrimaries.Bt2020, VideoColorPrimaries.Bt2020),
        })
            Assert.AreEqual(expected, SpaFormatPod.MapPrimaries(spa), $"primaries {spa}");
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

    // ---------------------------------------------------------------- explicit sync and hostile formats

    [TestMethod]
    public void WriteSyncTimelineMetaParam_NamesTheTimeline()
    {
        // The opt-in half of explicit sync: without this param the producer never attaches the
        // meta the reader looks for below.
        Span<byte> buf = stackalloc byte[128];
        int written = SpaFormatPod.WriteSyncTimelineMetaParam(buf);
        Assert.IsTrue(written > 0);

        Assert.IsTrue(SpaPod.TryParse(buf[..written], out SpaValue? value));
        var obj = (SpaObject)value!;
        Assert.AreEqual(SpaType.ObjectParamMeta, obj.ObjectType);
        Assert.AreEqual(SpaParamType.Meta, obj.ObjectId);
        Assert.AreEqual(
            (uint)SpaMetaType.SyncTimeline, ((SpaId)obj[SpaParamMeta.Type]!).Value);
    }

    [TestMethod]
    public unsafe void TryFindSyncTimeline_ReadsThePointsOffTheBuffer()
    {
        spa_meta_sync_timeline native = new()
        {
            flags = 1, acquire_point = 10, release_point = 20,
        };
        spa_meta meta = new()
        {
            type = (uint)SpaMetaType.SyncTimeline,
            size = (uint)sizeof(spa_meta_sync_timeline),
            data = &native,
        };
        spa_buffer buf = new() { n_metas = 1, metas = &meta };

        Assert.IsTrue(SpaFormatPod.TryFindSyncTimeline(&buf, out SpaFormatPod.SyncTimeline found));
        Assert.AreEqual(1u, found.Flags);
        Assert.AreEqual(10ul, found.AcquirePoint);
        Assert.AreEqual(20ul, found.ReleasePoint);
    }

    [TestMethod]
    public unsafe void TryFindSyncTimeline_RefusesAnUndersizedTimeline()
    {
        // The right type with fewer bytes than the struct is a truncated meta, not a short one.
        ulong point = 10;
        spa_meta meta = new()
        {
            type = (uint)SpaMetaType.SyncTimeline,
            size = 4,
            data = &point,
        };
        spa_buffer buf = new() { n_metas = 1, metas = &meta };

        Assert.IsFalse(SpaFormatPod.TryFindSyncTimeline(&buf, out _));
    }

    [TestMethod]
    public unsafe void AVideoFormatPropertyOfTheWrongType_IsSkippedRatherThanAdopted()
    {
        // A producer may send a property whose type does not match its key. Reading it would
        // adopt nonsense geometry; the previous negotiation is kept instead.
        Span<byte> buf = stackalloc byte[256];
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectFormat, SpaParamType.Format);
        b.AddInt(SpaFormat.VideoSize, 123);
        b.AddId(SpaFormat.VideoFormat, SpaVideoFormat.Bgra);
        ReadOnlySpan<byte> pod = b.GetPod();

        var start = new SpaFormatPod.VideoFormatInfo(PixelFormat.Rgba, 640, 480, default);
        SpaFormatPod.VideoFormatInfo parsed;
        fixed (byte* p = pod)
            parsed = SpaFormatPod.ParseVideoFormat((spa_pod*)p, start);

        Assert.AreEqual(640, parsed.Width);
        Assert.AreEqual(480, parsed.Height);
        Assert.AreEqual(PixelFormat.Bgra, parsed.Format);
    }

    [TestMethod]
    public unsafe void AnAudioFormatPropertyOfTheWrongType_IsSkippedRatherThanAdopted()
    {
        Span<byte> buf = stackalloc byte[256];
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectFormat, SpaParamType.Format);
        b.AddInt(SpaFormat.AudioFormat, 123);
        b.AddInt(SpaFormat.AudioRate, 48000);
        ReadOnlySpan<byte> pod = b.GetPod();

        var start = new SpaFormatPod.AudioFormatInfo(AudioSampleFormat.F32Le, 44100, 1);
        SpaFormatPod.AudioFormatInfo parsed;
        fixed (byte* p = pod)
            parsed = SpaFormatPod.ParseAudioFormat((spa_pod*)p, start);

        Assert.AreEqual(AudioSampleFormat.F32Le, parsed.Format);
        Assert.AreEqual(48000, parsed.SampleRate);
    }

    // ---------------------------------------------------------------- explicit-sync fd lookup

    [TestMethod]
    public unsafe void TryFindSyncDataFds_ReadsAcquireAndRelease()
    {
        // Located by type, not position: the MemPtr plane in the middle must not shift the
        // acquire/release pairing. Mirrors the producer layout: planes first, sync blocks last.
        spa_data acquire = new() { type = (uint)SpaDataType.SyncObj, fd = 11 };
        spa_data ignored = new() { type = (uint)SpaDataType.MemPtr, fd = 33 };
        spa_data release = new() { type = (uint)SpaDataType.SyncObj, fd = 22 };
        spa_data* three = stackalloc spa_data[3] { acquire, ignored, release };
        spa_buffer buf = new() { n_datas = 3, datas = three };

        Assert.IsTrue(SpaFormatPod.TryFindSyncDataFds(&buf, out int acquireFd, out int releaseFd));
        Assert.AreEqual(11, acquireFd);
        Assert.AreEqual(22, releaseFd);
    }

    [TestMethod]
    public unsafe void TryFindSyncDataFds_ReturnsFalseWhenFewerThanTwoSyncObjs()
    {
        // One timeline is not a pair: the outs are reset so a caller cannot mistake a stale
        // acquire for a usable handshake.
        spa_data acquire = new() { type = (uint)SpaDataType.SyncObj, fd = 11 };
        spa_data plane = new() { type = (uint)SpaDataType.MemPtr, fd = 33 };
        spa_data* two = stackalloc spa_data[2] { acquire, plane };
        spa_buffer buf = new() { n_datas = 2, datas = two };

        Assert.IsFalse(SpaFormatPod.TryFindSyncDataFds(&buf, out int acquireFd, out int releaseFd));
        Assert.AreEqual(-1, acquireFd);
        Assert.AreEqual(-1, releaseFd);
    }

    [TestMethod]
    public unsafe void TryFindSyncDataFds_ReturnsFalseOnNullBufferOrDatas()
    {
        // A buffer with nowhere to look is not explicit-sync, not a crash.
        Assert.IsFalse(SpaFormatPod.TryFindSyncDataFds(null, out _, out _));

        spa_buffer buf = new() { n_datas = 2, datas = null };
        Assert.IsFalse(SpaFormatPod.TryFindSyncDataFds(&buf, out _, out _));
    }

    [TestMethod]
    public unsafe void TryFindSyncDataFds_RejectsAnUnusableFd()
    {
        // A negative fd is a closed timeline, not acquire fd -1 with a second block following.
        spa_data closed = new() { type = (uint)SpaDataType.SyncObj, fd = -1 };
        spa_data release = new() { type = (uint)SpaDataType.SyncObj, fd = 22 };
        spa_data* two = stackalloc spa_data[2] { closed, release };
        spa_buffer buf = new() { n_datas = 2, datas = two };

        Assert.IsFalse(SpaFormatPod.TryFindSyncDataFds(&buf, out _, out _));
    }
}
