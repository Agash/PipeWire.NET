using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PipeWire.NET.Generated;
using PipeWire.NET.Spa;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PipeWire.NET.Tests;

[TestClass]
public sealed class SpaPodBuilderTests
{
    [TestMethod]
    public void Build_FormatObject_RoundtripsThroughReader()
    {
        Span<byte> buf = stackalloc byte[512];
        var builder = new SpaPodBuilder(buf);

        // No fluent chaining - each call mutates `builder` directly. Chaining on a
        // ref struct invokes subsequent calls on a returned copy (see TryReadProperty
        // failures committed in the previous revision).
        builder.PushObject(SpaType.ObjectFormat, SpaParam.EnumFormat);
        builder.AddId(SpaFormatVideo.MediaType,    SpaMediaType.Video);
        builder.AddId(SpaFormatVideo.MediaSubtype, SpaMediaSubtype.Raw);
        builder.AddId(SpaFormatVideo.Format,       SpaVideoFormat.BGRA);
        builder.AddRectangle(SpaFormatVideo.Size,  1920, 1080);
        builder.AddFraction(SpaFormatVideo.Framerate, 30, 1);

        ReadOnlySpan<byte> pod = builder.GetPod();
        Assert.IsTrue(pod.Length >= 8 + 8);
        Assert.AreEqual(0, pod.Length & 7, "POD must be 8-byte aligned");

        var reader = new SpaPodReader(pod);
        Assert.IsTrue(reader.EnterObject(out uint objType, out _, out _));
        Assert.AreEqual(SpaType.ObjectFormat, objType);

        bool sawMediaType = false, sawSize = false, sawFramerate = false;

        while (reader.TryReadProperty(out uint key, out var value))
        {
            switch (key)
            {
                case SpaFormatVideo.MediaType:
                    Assert.AreEqual(SpaMediaType.Video, value.ReadId());
                    sawMediaType = true;
                    break;
                case SpaFormatVideo.Format:
                    Assert.AreEqual(SpaVideoFormat.BGRA, value.ReadId());
                    break;
                case SpaFormatVideo.Size:
                    var (w, h) = value.ReadRectangle();
                    Assert.AreEqual(1920u, w);
                    Assert.AreEqual(1080u, h);
                    sawSize = true;
                    break;
                case SpaFormatVideo.Framerate:
                    var (n, d) = value.ReadFraction();
                    Assert.AreEqual(30u, n);
                    Assert.AreEqual(1u, d);
                    sawFramerate = true;
                    break;
            }
        }

        Assert.IsTrue(sawMediaType, "expected MediaType property");
        Assert.IsTrue(sawSize,      "expected Size property");
        Assert.IsTrue(sawFramerate, "expected Framerate property");
    }

    [TestMethod]
    public void Build_ChoiceEnum_RoundtripsThroughUnwrapChoice()
    {
        Span<byte> buf = stackalloc byte[256];
        var builder = new SpaPodBuilder(buf);
        builder.PushObject(SpaType.ObjectFormat, SpaParam.EnumFormat);
        builder.AddChoiceEnum(SpaFormatVideo.Format,
            SpaVideoFormat.BGRA, SpaVideoFormat.RGBA);

        var reader = new SpaPodReader(builder.GetPod());
        Assert.IsTrue(reader.EnterObject(out _, out _, out _));
        Assert.IsTrue(reader.TryReadProperty(out uint key, out var value));
        Assert.AreEqual(SpaFormatVideo.Format, key);

        Assert.IsTrue(value.TryUnwrapChoice(out var firstChoice));
        Assert.AreEqual(SpaVideoFormat.BGRA, firstChoice.ReadId());
    }

    [TestMethod]
    public void GetPod_IsAlwaysOctaligned()
    {
        Span<byte> buf = stackalloc byte[256];
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectFormat, SpaParam.Format);
        b.AddId(SpaFormatVideo.MediaType, SpaMediaType.Video);
        Assert.AreEqual(0, b.GetPod().Length & 7);
    }
}

[TestClass]
public sealed class VideoFrameTests
{
    [TestMethod]
    public void Ctor_PreservesAllFields()
    {
        ReadOnlySpan<byte> pixels = stackalloc byte[16];
        var frame = new VideoFrame(pixels, stride: 64, width: 4, height: 4,
                                   format: PixelFormat.Bgra, sequenceNumber: 42);
        Assert.AreEqual(64, frame.Stride);
        Assert.AreEqual(4, frame.Width);
        Assert.AreEqual(4, frame.Height);
        Assert.AreEqual(PixelFormat.Bgra, frame.Format);
        Assert.AreEqual(42UL, frame.SequenceNumber);
        Assert.AreEqual(16, frame.Data.Length);
    }
}

[TestClass]
public sealed class GeneratedAbiTests
{
    // Hand-verified against libpipewire-0.3-dev 1.0.5 on x86_64 Linux.
    // If PipeWire bumps any of these struct sizes, regenerate bindings and update these.

    [TestMethod] public unsafe void SpaChunk_Size()       => Assert.AreEqual(16, sizeof(spa_chunk));
    [TestMethod] public unsafe void SpaData_Size()        => Assert.AreEqual(40, sizeof(spa_data));
    [TestMethod] public unsafe void SpaBuffer_Size()      => Assert.AreEqual(24, sizeof(spa_buffer));
    [TestMethod] public unsafe void SpaPod_Size()         => Assert.AreEqual(8,  sizeof(spa_pod));
    [TestMethod] public unsafe void SpaRectangle_Size()   => Assert.AreEqual(8,  sizeof(spa_rectangle));
    [TestMethod] public unsafe void SpaFraction_Size()    => Assert.AreEqual(8,  sizeof(spa_fraction));
    [TestMethod] public unsafe void SpaHook_Size()        => Assert.AreEqual(48, sizeof(spa_hook));

    [TestMethod]
    public unsafe void PwBuffer_HasExpectedShape()
    {
        // struct pw_buffer { spa_buffer* buffer; void* user_data; uint64_t size; uint64_t requested; uint64_t time; }
        // 5 x 8 bytes = 40 on 64-bit Linux. Verified against libpipewire-0.3-dev 1.0.5 on x86_64.
        Assert.AreEqual(40, sizeof(pw_buffer));
    }

    [TestMethod]
    public unsafe void PwStreamEvents_VersionFieldIsAtOffsetZero()
    {
        pw_stream_events ev = default;
        nint baseAddr = (nint)(&ev);
        nint verAddr  = (nint)(&ev.version);
        Assert.AreEqual(0, (int)(verAddr - baseAddr));
    }

    [TestMethod]
    public void GeneratedEnums_HaveExpectedConstants()
    {
        // Convert.To* reads the underlying value at runtime, so these verify the generated
        // enum constants rather than comparing two compile-time constants.
        Assert.AreEqual(0u, Convert.ToUInt32(spa_direction.SPA_DIRECTION_INPUT));
        Assert.AreEqual(1u, Convert.ToUInt32(spa_direction.SPA_DIRECTION_OUTPUT));

        Assert.AreEqual(-1, Convert.ToInt32(pw_stream_state.PW_STREAM_STATE_ERROR));
        Assert.AreEqual(0, Convert.ToInt32(pw_stream_state.PW_STREAM_STATE_UNCONNECTED));
        Assert.AreEqual(3, Convert.ToInt32(pw_stream_state.PW_STREAM_STATE_STREAMING));
    }

    [TestMethod]
    public unsafe void SpaMetaHeader_HasExpectedSize()
    {
        // flags(u32) + offset(u32) + pts(i64) + dts_offset(i64) + seq(u64) = 32 bytes on 64-bit.
        Assert.AreEqual(32, sizeof(spa_meta_header));
    }
}

[TestClass]
public sealed class MetadataMappingTests
{
    [TestMethod]
    public void ColorMappers_MapSpaValuesCorrectly()
    {
        // SPA numbering is non-contiguous with our public enums; mapping is explicit.
        Assert.AreEqual(VideoColorRange.Limited_16_235,
            SpaFormat.MapColorRange((uint)spa_video_color_range.SPA_VIDEO_COLOR_RANGE_16_235));
        Assert.AreEqual(VideoColorMatrix.Bt709,
            SpaFormat.MapColorMatrix((uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_BT709));
        Assert.AreEqual(VideoColorMatrix.Bt2020,
            SpaFormat.MapColorMatrix((uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_BT2020));
        Assert.AreEqual(VideoTransferFunction.Srgb,
            SpaFormat.MapTransfer((uint)spa_video_transfer_function.SPA_VIDEO_TRANSFER_SRGB));
        Assert.AreEqual(VideoColorPrimaries.Bt2020,
            SpaFormat.MapPrimaries((uint)spa_video_color_primaries.SPA_VIDEO_COLOR_PRIMARIES_BT2020));
        Assert.AreEqual(VideoColorMatrix.Unknown, SpaFormat.MapColorMatrix(9999));
    }

    [TestMethod]
    public void ToBufferType_MapsDataTypes()
    {
        Assert.AreEqual(PipeWireBufferType.MemPtr, SpaFormat.ToBufferType((uint)spa_data_type.SPA_DATA_MemPtr));
        Assert.AreEqual(PipeWireBufferType.MemFd,  SpaFormat.ToBufferType((uint)spa_data_type.SPA_DATA_MemFd));
        Assert.AreEqual(PipeWireBufferType.DmaBuf, SpaFormat.ToBufferType((uint)spa_data_type.SPA_DATA_DmaBuf));
        Assert.AreEqual(PipeWireBufferType.Unknown, SpaFormat.ToBufferType(9999));
    }

    [TestMethod]
    public unsafe void ParseVideoFormat_ReadsColorMetadata()
    {
        // Build a Format object carrying color props, then parse it back.
        Span<byte> buf = stackalloc byte[512];
        var b = new SpaPodBuilder(buf);
        b.PushObject(SpaType.ObjectFormat, SpaParam.Format);
        b.AddId(SpaFormatVideo.MediaType,    SpaMediaType.Video);
        b.AddId(SpaFormatVideo.MediaSubtype, SpaMediaSubtype.Raw);
        b.AddId(SpaFormatVideo.Format,       SpaVideoFormat.BGRA);
        b.AddRectangle(SpaFormatVideo.Size,  1920, 1080);
        b.AddId(SpaFormatVideo.ColorRange,       (uint)spa_video_color_range.SPA_VIDEO_COLOR_RANGE_16_235);
        b.AddId(SpaFormatVideo.ColorMatrix,      (uint)spa_video_color_matrix.SPA_VIDEO_COLOR_MATRIX_BT709);
        b.AddId(SpaFormatVideo.TransferFunction, (uint)spa_video_transfer_function.SPA_VIDEO_TRANSFER_SRGB);
        b.AddId(SpaFormatVideo.ColorPrimaries,   (uint)spa_video_color_primaries.SPA_VIDEO_COLOR_PRIMARIES_BT709);
        ReadOnlySpan<byte> pod = b.GetPod();

        SpaFormat.VideoFormatInfo info;
        fixed (byte* p = pod)
            info = SpaFormat.ParseVideoFormat((spa_pod*)p,
                new SpaFormat.VideoFormatInfo(PixelFormat.Bgra, 0, 0, VideoColorInfo.Unknown));

        Assert.AreEqual(PixelFormat.Bgra, info.Format);
        Assert.AreEqual(1920, info.Width);
        Assert.AreEqual(1080, info.Height);
        Assert.AreEqual(VideoColorRange.Limited_16_235, info.Color.Range);
        Assert.AreEqual(VideoColorMatrix.Bt709,         info.Color.Matrix);
        Assert.AreEqual(VideoTransferFunction.Srgb,     info.Color.Transfer);
        Assert.AreEqual(VideoColorPrimaries.Bt709,      info.Color.Primaries);
    }

    [TestMethod]
    public unsafe void VideoBuffersParam_AdvertisesDmaBuf()
    {
        // The capture must offer SPA_DATA_DmaBuf (plus host memory) so a GPU producer can hand
        // us zero-copy buffers. Verify the pod we send actually carries that flag - headless.
        Span<byte> buf = stackalloc byte[256];
        int len = SpaFormat.WriteVideoBuffersParam(buf, size: 1920 * 1080 * 4, stride: 1920 * 4,
            dataTypes: SpaFormat.VideoCaptureDataTypeMask);

        var reader = new SpaPodReader(buf[..len]);
        Assert.IsTrue(reader.EnterObject(out uint objType, out _, out _));
        Assert.AreEqual(SpaType.ObjectParamBuffers, objType);

        int? dataTypeMask = null;
        while (reader.TryReadProperty(out uint key, out var value))
        {
            if (key == SpaParamBuffers.DataType)
                dataTypeMask = value.TryUnwrapChoice(out var inner) ? inner.ReadInt() : value.ReadInt();
        }

        Assert.IsNotNull(dataTypeMask, "buffers param must contain a dataType property");
        int dmaBufBit = 1 << (int)spa_data_type.SPA_DATA_DmaBuf;
        int memPtrBit = 1 << (int)spa_data_type.SPA_DATA_MemPtr;
        Assert.AreEqual(dmaBufBit, dataTypeMask!.Value & dmaBufBit, "must advertise DMA-BUF");
        Assert.AreEqual(memPtrBit, dataTypeMask!.Value & memPtrBit, "must also advertise host memory fallback");
    }

    [TestMethod]
    public void VideoFrame_CarriesMetadata()
    {
        ReadOnlySpan<byte> px = stackalloc byte[16];
        var frame = new VideoFrame(px, 64, 4, 4, PixelFormat.Bgra, 7,
            bufferType: PipeWireBufferType.DmaBuf, fd: 42, mapOffset: 0,
            presentationTimeNs: 123_456,
            color: new VideoColorInfo(VideoColorRange.Full_0_255, VideoColorMatrix.Rgb,
                                      VideoTransferFunction.Srgb, VideoColorPrimaries.Bt709));
        Assert.AreEqual(PipeWireBufferType.DmaBuf, frame.BufferType);
        Assert.IsTrue(frame.IsFdBacked);
        Assert.AreEqual(42, frame.Fd);
        Assert.AreEqual(123_456, frame.PresentationTimeNs);
        Assert.AreEqual(VideoColorMatrix.Rgb, frame.Color.Matrix);
    }
}

[TestClass]
public sealed class ModifierNegotiationTests
{
    // Two sample DRM format modifiers (values are opaque 64-bit tokens; the test only checks the wire
    // round-trip, not their meaning). Linear plus an AMD GFX9 tiled-ish token exercise multi-value.
    private const long ModLinear = 0;
    private const long ModTiled  = 0x0300_0000_0000_0001L;

    [TestMethod]
    public void WriteVideoFormat_FirstPass_OffersLongChoiceWithMandatoryDontFixate()
    {
        Span<byte> buf = stackalloc byte[512];
        ReadOnlySpan<PixelFormat> fmts = stackalloc[] { PixelFormat.Yuv420 };
        ReadOnlySpan<long> mods = stackalloc[] { ModTiled, ModLinear };
        int len = SpaFormat.WriteVideoFormat(buf, fmts, 1920, 1080, 30, fixedSize: false, modifiers: mods);

        var reader = new SpaPodReader(buf[..len]);
        Assert.IsTrue(reader.EnterObject(out _, out _, out _));

        bool sawModifier = false;
        while (reader.TryReadProperty(out uint key, out uint flags, out var value))
        {
            if (key != SpaFormatVideo.Modifier) continue;
            sawModifier = true;

            // First pass MUST advertise MANDATORY|DONT_FIXATE so the producer narrows the modifier
            // set to what it supports without collapsing it to a single value (the two-step handshake).
            Assert.AreEqual(SpaPodPropFlag.Mandatory, flags & SpaPodPropFlag.Mandatory, "MANDATORY must be set");
            Assert.AreEqual(SpaPodPropFlag.DontFixate, flags & SpaPodPropFlag.DontFixate, "DONT_FIXATE must be set");

            Assert.IsTrue(value.TryReadModifier(out long first, out int count));
            // SPA Choice Enum body is { default, ...allowed }: the first value is the default AND must also
            // appear in the allowed set, so the preferred modifier is written twice. For [Tiled, Linear] the
            // wire is [Tiled(default), Tiled, Linear] = 3 longs. (A single modifier written once would leave
            // the allowed set empty - the consumer then has nothing to select, the dmabuf negotiation bug.)
            Assert.AreEqual(3, count, "default is repeated into the allowed set, then both modifiers follow");
            Assert.AreEqual(ModTiled, first, "the first offered modifier is the preferred one (the default)");
        }
        Assert.IsTrue(sawModifier, "EnumFormat must carry a modifier property when modifiers are offered");
    }

    [TestMethod]
    public void WriteVideoFormat_FixatePass_ClearsDontFixate()
    {
        Span<byte> buf = stackalloc byte[512];
        ReadOnlySpan<PixelFormat> fmts = stackalloc[] { PixelFormat.Yuv420 };
        ReadOnlySpan<long> mods = stackalloc[] { ModTiled };
        int len = SpaFormat.WriteVideoFormat(buf, fmts, 1920, 1080, 30, fixedSize: false,
            modifiers: mods, fixateModifier: true);

        var reader = new SpaPodReader(buf[..len]);
        Assert.IsTrue(reader.EnterObject(out _, out _, out _));
        while (reader.TryReadProperty(out uint key, out uint flags, out _))
        {
            if (key != SpaFormatVideo.Modifier) continue;
            // The fixate pass keeps MANDATORY but drops DONT_FIXATE, telling the producer to settle
            // on the single modifier we now offer.
            Assert.AreEqual(SpaPodPropFlag.Mandatory, flags & SpaPodPropFlag.Mandatory);
            Assert.AreEqual(0u, flags & SpaPodPropFlag.DontFixate, "fixate pass must NOT set DONT_FIXATE");
        }
    }

    [TestMethod]
    public unsafe void ParseVideoFormat_MultiModifierChoice_FlagsFixationNeeded()
    {
        Span<byte> buf = stackalloc byte[512];
        ReadOnlySpan<PixelFormat> fmts = stackalloc[] { PixelFormat.Yuv420 };
        ReadOnlySpan<long> mods = stackalloc[] { ModTiled, ModLinear };
        int len = SpaFormat.WriteVideoFormat(buf, fmts, 1920, 1080, 30, fixedSize: false, modifiers: mods);

        SpaFormat.VideoFormatInfo info;
        fixed (byte* p = buf)
            info = SpaFormat.ParseVideoFormat((spa_pod*)p,
                new SpaFormat.VideoFormatInfo(PixelFormat.Yuv420, 0, 0, VideoColorInfo.Unknown));

        Assert.AreEqual((ulong)ModTiled, info.Modifier, "preferred modifier is the first offered");
        Assert.IsTrue(info.ModifierNeedsFixation, "more than one modifier => still needs fixation");
    }

    [TestMethod]
    public unsafe void ParseVideoFormat_SingleModifier_IsFixated()
    {
        Span<byte> buf = stackalloc byte[512];
        ReadOnlySpan<PixelFormat> fmts = stackalloc[] { PixelFormat.Yuv420 };
        ReadOnlySpan<long> mods = stackalloc[] { ModTiled };
        int len = SpaFormat.WriteVideoFormat(buf, fmts, 1920, 1080, 30, fixedSize: false,
            modifiers: mods, fixateModifier: true);

        SpaFormat.VideoFormatInfo info;
        fixed (byte* p = buf)
            info = SpaFormat.ParseVideoFormat((spa_pod*)p,
                new SpaFormat.VideoFormatInfo(PixelFormat.Yuv420, 0, 0, VideoColorInfo.Unknown));

        Assert.AreEqual((ulong)ModTiled, info.Modifier);
        Assert.IsFalse(info.ModifierNeedsFixation, "a single modifier is already fixated");
    }

    [TestMethod]
    public void VideoFrame_CarriesModifierAndPlanes()
    {
        ReadOnlySpan<byte> px = default;
        ReadOnlySpan<VideoPlane> planes = stackalloc VideoPlane[]
        {
            new VideoPlane(Fd: 7, Offset: 0,        Stride: 1920, Size: 1920 * 1080),
            new VideoPlane(Fd: 7, Offset: 1920*1080, Stride: 1920, Size: 1920 * 1080 / 2),
        };
        var frame = new VideoFrame(px, 1920, 1920, 1080, PixelFormat.Yuv420, 1,
            bufferType: PipeWireBufferType.DmaBuf, fd: 7,
            modifier: (ulong)ModTiled, planes: planes);

        Assert.AreEqual((ulong)ModTiled, frame.Modifier);
        Assert.AreEqual(2, frame.Planes.Length);
        Assert.AreEqual(1920 * 1080u, frame.Planes[1].Offset);
        Assert.IsTrue(frame.IsFdBacked);
    }
}

[TestClass]
public sealed class DmaBufOutputTests
{
    [TestMethod]
    public void PlaneCount_MatchesFormatLayout()
    {
        Assert.AreEqual(1, SpaFormat.PlaneCount(PixelFormat.Bgra));
        Assert.AreEqual(1, SpaFormat.PlaneCount(PixelFormat.Yuyv));
        Assert.AreEqual(2, SpaFormat.PlaneCount(PixelFormat.Nv12), "NV12 = Y + interleaved UV");
        Assert.AreEqual(3, SpaFormat.PlaneCount(PixelFormat.Yuv420), "I420 = Y + U + V");
    }

    [TestMethod]
    public void Nv12_RoundtripsThroughSpaFormat()
    {
        uint spa = SpaFormat.ToSpaVideoFormat(PixelFormat.Nv12);
        Assert.AreEqual(SpaVideoFormat.NV12, spa);
        Assert.AreEqual(PixelFormat.Nv12, SpaFormat.FromSpaVideoFormat(spa));
    }

    [TestMethod]
    public void WriteVideoBuffersParam_DmaBufNv12_RequestsTwoBlocks()
    {
        // A dmabuf producer for NV12 must request one block per plane (2) so each plane gets its own
        // spa_data (fd/offset/stride), and advertise the DMA-BUF data type only.
        int blocks = SpaFormat.PlaneCount(PixelFormat.Nv12);
        Span<byte> buf = stackalloc byte[256];
        int len = SpaFormat.WriteVideoBuffersParam(buf,
            size: SpaFormat.VideoImageSize(PixelFormat.Nv12, 1920, 1080),
            stride: SpaFormat.VideoStride(PixelFormat.Nv12, 1920),
            dataTypes: 1 << (int)SpaType.DataDmaBuf, blocks: blocks);

        var reader = new SpaPodReader(buf[..len]);
        Assert.IsTrue(reader.EnterObject(out uint objType, out _, out _));
        Assert.AreEqual(SpaType.ObjectParamBuffers, objType);

        int? sawBlocks = null, sawDataType = null;
        while (reader.TryReadProperty(out uint key, out var value))
        {
            if (key == SpaParamBuffers.Blocks)
                sawBlocks = value.TryUnwrapChoice(out var i) ? i.ReadInt() : value.ReadInt();
            else if (key == SpaParamBuffers.DataType)
                sawDataType = value.TryUnwrapChoice(out var i) ? i.ReadInt() : value.ReadInt();
        }

        Assert.AreEqual(2, sawBlocks, "NV12 dmabuf must request 2 blocks");
        int dmaBufBit = 1 << (int)spa_data_type.SPA_DATA_DmaBuf;
        Assert.IsNotNull(sawDataType);
        Assert.AreEqual(dmaBufBit, sawDataType!.Value & dmaBufBit, "must advertise DMA-BUF");
    }
}

[TestClass]
public sealed class NativeLibraryResolutionTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public unsafe void PwInit_DoesNotThrow_OnLinux()
    {
        // Pure library-load smoke test. Does not require the PipeWire daemon.
        // Verifies the [LibraryImport] resolver + DllImport call chain works end-to-end.
        Native.pw_init(null, null);
        Native.pw_deinit();
    }

    [TestMethod]
    [TestCategory("Integration")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public unsafe void PwMainLoop_CanCreateAndDestroy()
    {
        Native.pw_init(null, null);
        try
        {
            pw_main_loop* loop = Native.pw_main_loop_new(null);
            Assert.IsTrue(loop is not null);
            Native.pw_main_loop_destroy(loop);
        }
        finally
        {
            Native.pw_deinit();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public unsafe void PwProperties_RoundtripsKeyValue()
    {
        Native.pw_init(null, null);
        try
        {
            ReadOnlySpan<byte> kv = "media.role=Camera\0"u8;
            pw_properties* props;
            fixed (byte* p = kv) props = Native.pw_properties_new_string((sbyte*)p);

            Assert.IsTrue(props is not null);

            ReadOnlySpan<byte> key = "media.role\0"u8;
            sbyte* val;
            fixed (byte* k = key) val = Native.pw_properties_get(props, (sbyte*)k);

            string? gotValue = val is null ? null : Marshal.PtrToStringUTF8((nint)val);
            Assert.AreEqual("Camera", gotValue);

            Native.pw_properties_free(props);
        }
        finally
        {
            Native.pw_deinit();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task PipeWireContext_ConnectsToRunningDaemon()
    {
        await using var ctx = new PipeWireContext();
        await ctx.StartAsync();
        Assert.IsNotNull(ctx);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task AudioOutputToCapture_DeliversFrames_EndToEnd()
    {
        // Full round-trip against a live daemon using only this library: publish a
        // virtual audio source, capture it back, and assert real buffers flow with
        // negotiated format. Exercises pw_stream_new/connect, format negotiation,
        // the process callback, and buffer dequeue/queue on both directions.
        await using var ctx = new PipeWireContext();
        await ctx.StartAsync();

        const int rate = 48000, channels = 2;
        await using var output = new PipeWireAudioOutput(ctx, "PipeWire.NET.Test.Source",
            sampleRate: rate, channels: channels, format: AudioSampleFormat.F32Le);

        int produced = 0;
        output.FillSamples += (_, samples, _, _, _) =>
        {
            samples.Clear();                 // silence is fine; we only assert flow
            Interlocked.Increment(ref produced);
            return samples.Length;
        };
        output.Connect();

        // AudioFrame is a ref struct and can't be a TResult - capture scalar facts.
        var captured = new TaskCompletionSource<(int Rate, int Channels)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var capture = new PipeWireAudioCapture(ctx, "PipeWire.NET.Test.Sink");
        capture.FrameReady += (_, frame) => captured.TrySetResult((frame.SampleRate, frame.Channels));
        capture.Connect(sampleRate: rate, channels: channels, format: AudioSampleFormat.F32Le,
            targetObjectName: "PipeWire.NET.Test.Source");

        (int Rate, int Channels) got = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(produced > 0, "output stream should have been pulled for samples");
        Assert.AreEqual(channels, got.Channels);
        Assert.AreEqual(rate, got.Rate);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task VideoOutputToCapture_DeliversFrames_EndToEnd()
    {
        // Video round-trip with BYTE-EXACT content verification. The producer fills every
        // pixel with a sentinel marker; the consumer must receive those exact bytes.
        // BGRA->BGRA at identical size links with no format converter, so the daemon
        // passes the buffer through unchanged - proving real pixel data crosses the graph,
        // not merely that "a buffer of the right shape arrived".
        const byte marker = 0xAB;
        const int width = 320, height = 240;

        await using var ctx = new PipeWireContext();
        await ctx.StartAsync();

        await using var output = new PipeWireVideoOutput(ctx, "PipeWire.NET.Test.VideoSource",
            width: width, height: height, format: PixelFormat.Bgra, frameRate: 30);

        int produced = 0;
        output.FillFrame += (_, pixels, _, _, _, _) =>
        {
            pixels.Fill(marker);                 // known pattern, not zeros
            Interlocked.Increment(ref produced);
            return true;
        };
        output.Connect();

        // Copy enough captured bytes out of the ref-struct frame to verify content.
        var captured = new TaskCompletionSource<(int W, int H, PixelFormat Fmt, PipeWireBufferType Buf, byte[] Head)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var capture = new PipeWireVideoCapture(ctx, "PipeWire.NET.Test.VideoSink");
        capture.FrameReady += (_, frame) =>
        {
            // Ignore the initial empty/black frames some negotiations emit; wait for real data.
            if (frame.Data.Length < 256) return;
            byte[] head = frame.Data[..256].ToArray();
            captured.TrySetResult((frame.Width, frame.Height, frame.Format, frame.BufferType, head));
        };
        capture.Connect(preferredFormats: stackalloc[] { PixelFormat.Bgra },
            targetObjectName: "PipeWire.NET.Test.VideoSource");

        var got = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(produced > 0, "output stream should have been pulled for frames");
        Assert.AreEqual(width, got.W);
        Assert.AreEqual(height, got.H);
        Assert.AreEqual(PixelFormat.Bgra, got.Fmt);
        Assert.AreNotEqual(PipeWireBufferType.Unknown, got.Buf);

        // The load-bearing assertion: every sampled byte equals the marker we produced.
        foreach (byte b in got.Head)
            Assert.AreEqual(marker, b, "captured pixel data must match the produced marker byte-for-byte");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task VideoRoundTrip_PreservesAlphaChannel()
    {
        // Write a per-channel BGRA pattern with a NON-opaque alpha
        // and assert all four channels - alpha included - arrive byte-exact. Proves the alpha
        // channel is carried through the graph, not silently dropped to opaque.
        // BGRA byte order in memory is B,G,R,A.
        byte[] pixel = [0x11, 0x22, 0x33, 0x80];   // B,G,R,A=0x80 (semi-transparent)
        const int width = 64, height = 64;

        await using var ctx = new PipeWireContext();
        await ctx.StartAsync();

        await using var output = new PipeWireVideoOutput(ctx, "PipeWire.NET.Test.AlphaSource",
            width: width, height: height, format: PixelFormat.Bgra, frameRate: 30);
        output.FillFrame += (_, pixels, _, _, _, _) =>
        {
            for (int i = 0; i + 4 <= pixels.Length; i += 4)
            {
                pixels[i + 0] = pixel[0];
                pixels[i + 1] = pixel[1];
                pixels[i + 2] = pixel[2];
                pixels[i + 3] = pixel[3];
            }
            return true;
        };
        output.Connect();

        var captured = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var capture = new PipeWireVideoCapture(ctx, "PipeWire.NET.Test.AlphaSink");
        capture.FrameReady += (_, frame) =>
        {
            if (frame.Data.Length < 4) return;
            captured.TrySetResult(frame.Data[..4].ToArray());
        };
        capture.Connect(preferredFormats: stackalloc[] { PixelFormat.Bgra },
            targetObjectName: "PipeWire.NET.Test.AlphaSource");

        byte[] firstPixel = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(pixel, firstPixel,
            "BGRA pixel including the 0x80 alpha byte must survive the round-trip");
        Assert.AreEqual(0x80, firstPixel[3], "alpha channel must be preserved (not forced opaque)");
    }
}
