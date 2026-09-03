using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// The conversion and boundary layer: media-class parsing, sample-format arithmetic, property-bag
/// building and the stack/pool boundary in <see cref="SpaDictBuilder"/>. All pure, so all of it is
/// testable without a daemon, and all of it sits between a caller's input and a native call.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class ConversionBoundaryTests
{
    // ---------------------------------------------------------------- media class

    [TestMethod]
    [DataRow("Audio/Source", PipeWireMediaKind.Audio, PipeWireMediaFlow.Source)]
    [DataRow("Audio/Source/Virtual", PipeWireMediaKind.Audio, PipeWireMediaFlow.Source)]
    [DataRow("Audio/Sink", PipeWireMediaKind.Audio, PipeWireMediaFlow.Sink)]
    [DataRow("Audio/Duplex", PipeWireMediaKind.Audio, PipeWireMediaFlow.Duplex)]
    [DataRow("Video/Source", PipeWireMediaKind.Video, PipeWireMediaFlow.Source)]
    [DataRow("Video/Source/Virtual", PipeWireMediaKind.Video, PipeWireMediaFlow.Source)]
    [DataRow("Video/Sink", PipeWireMediaKind.Video, PipeWireMediaFlow.Sink)]
    [DataRow("Midi/Bridge", PipeWireMediaKind.Midi, PipeWireMediaFlow.Duplex)]
    public void MediaClass_ParsesKindAndFlowStructurally(
        string raw, PipeWireMediaKind kind, PipeWireMediaFlow flow)
    {
        Assert.AreEqual(kind, PipeWireMediaClass.ParseKind(raw));
        Assert.AreEqual(flow, PipeWireMediaClass.ParseFlow(raw));
    }

    [TestMethod]
    [DataRow("Stream/Output/Audio", PipeWireMediaKind.Audio, PipeWireMediaFlow.Source)]
    [DataRow("Stream/Output/Video", PipeWireMediaKind.Video, PipeWireMediaFlow.Source)]
    [DataRow("Stream/Input/Audio", PipeWireMediaKind.Audio, PipeWireMediaFlow.Sink)]
    [DataRow("Stream/Input/Video", PipeWireMediaKind.Video, PipeWireMediaFlow.Sink)]
    public void StreamClasses_NameTheirMediumLastAndInvertDirection(
        string raw, PipeWireMediaKind kind, PipeWireMediaFlow flow)
    {
        // "Output" is the application's direction; from the graph it is a source to read from.
        Assert.AreEqual(kind, PipeWireMediaClass.ParseKind(raw));
        Assert.AreEqual(flow, PipeWireMediaClass.ParseFlow(raw));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("/")]
    [DataRow("///")]
    [DataRow("audio/source")]        // media.class is case-sensitive
    [DataRow("Telephony/Gateway")]
    public void UnrecognisedOrMalformedClasses_ReportUnknownRatherThanGuessing(string? raw)
    {
        Assert.AreEqual(PipeWireMediaKind.Unknown, PipeWireMediaClass.ParseKind(raw));
        Assert.AreEqual(PipeWireMediaFlow.Unknown, PipeWireMediaClass.ParseFlow(raw));
    }

    [TestMethod]
    public void EachSegmentIsReadIndependently()
    {
        // A medium we cannot read does not make the direction unreadable, and the reverse holds
        // too. Reporting what each segment actually says beats discarding the whole string.
        Assert.AreEqual(PipeWireMediaKind.Unknown, PipeWireMediaClass.ParseKind(" Audio/Source"));
        Assert.AreEqual(PipeWireMediaFlow.Source, PipeWireMediaClass.ParseFlow(" Audio/Source"));

        Assert.AreEqual(PipeWireMediaKind.Audio, PipeWireMediaClass.ParseKind("Audio/Wat"));
        Assert.AreEqual(PipeWireMediaFlow.Unknown, PipeWireMediaClass.ParseFlow("Audio/Wat"));
    }

    [TestMethod]
    public void StructuralParsing_HandlesClassesNothingEnumerates()
    {
        // The point of parsing rather than matching a list: a value we have never seen still yields
        // whatever it actually states.
        Assert.AreEqual(PipeWireMediaKind.Video, PipeWireMediaClass.ParseKind("Video/Duplex"));
        Assert.AreEqual(PipeWireMediaFlow.Duplex, PipeWireMediaClass.ParseFlow("Video/Duplex"));
        Assert.AreEqual(PipeWireMediaKind.Audio, PipeWireMediaClass.ParseKind("Audio/Source/Virtual/Something"));
    }

    [TestMethod]
    public void AMediumWithNoDirection_ReportsTheMediumAndAnUnknownFlow()
    {
        Assert.AreEqual(PipeWireMediaKind.Audio, PipeWireMediaClass.ParseKind("Audio"));
        Assert.AreEqual(PipeWireMediaFlow.Unknown, PipeWireMediaClass.ParseFlow("Audio"));
    }

    [TestMethod]
    public void AStreamWithNoMedium_ReportsUnknownKind()
    {
        Assert.AreEqual(PipeWireMediaKind.Unknown, PipeWireMediaClass.ParseKind("Stream/Output"));
        Assert.AreEqual(PipeWireMediaFlow.Source, PipeWireMediaClass.ParseFlow("Stream/Output"));
    }

    // ---------------------------------------------------------------- sample formats

    [TestMethod]
    [DataRow(AudioSampleFormat.U8, 1)]
    [DataRow(AudioSampleFormat.S16Le, 2)]
    [DataRow(AudioSampleFormat.S24Le, 3)]
    [DataRow(AudioSampleFormat.S32Le, 4)]
    [DataRow(AudioSampleFormat.F32Le, 4)]
    public void BytesPerSample_MatchesTheFormatWidth(AudioSampleFormat fmt, int expected) =>
        Assert.AreEqual(expected, fmt.BytesPerSample());

    [TestMethod]
    public void EveryUsableSampleFormat_HasANonZeroWidth()
    {
        // FrameCount divides by this, so a zero would be a divide-by-zero on a real capture.
        foreach (AudioSampleFormat fmt in Enum.GetValues<AudioSampleFormat>())
        {
            if (fmt is AudioSampleFormat.Unknown) continue;

            Assert.IsTrue(fmt.BytesPerSample() > 0, $"{fmt} reports a width of {fmt.BytesPerSample()}");
        }
    }

    [TestMethod]
    public void AnUnknownFormat_HasNoWidthAndStillDoesNotDivideByZero()
    {
        // Zero is the honest width for a format whose layout is unknown, and it is only safe
        // because FrameCount treats a non-positive stride as no frames rather than dividing. The
        // two belong together: widening the enum requires keeping that guard.
        Assert.AreEqual(0, AudioSampleFormat.Unknown.BytesPerSample());

        var frame = new AudioFrame(new byte[64], 48000, 2, AudioSampleFormat.Unknown, 0);
        Assert.AreEqual(0, frame.FrameCount, "an unknown format must report no frames, not throw");
    }

    [TestMethod]
    [DataRow(AudioSampleFormat.S16Le, 2, 480)]     // 1920 bytes / (2ch * 2B)
    [DataRow(AudioSampleFormat.F32Le, 2, 240)]     // 1920 / (2 * 4)
    [DataRow(AudioSampleFormat.U8, 1, 1920)]       // 1920 / (1 * 1)
    [DataRow(AudioSampleFormat.S24Le, 2, 320)]     // 1920 / (2 * 3)
    public void FrameCount_DividesBytesByChannelsAndWidth(
        AudioSampleFormat fmt, int channels, int expected)
    {
        var frame = new AudioFrame(new byte[1920], 48000, channels, fmt, 0, 0, 0, 0, 0);
        Assert.AreEqual(expected, frame.FrameCount);
    }

    [TestMethod]
    public void FrameCount_TruncatesAPartialFrameRatherThanRoundingUp()
    {
        // 5 bytes of stereo S16 is one whole frame plus a dangling byte.
        var frame = new AudioFrame(new byte[5], 48000, 2, AudioSampleFormat.S16Le, 0, 0, 0, 0, 0);
        Assert.AreEqual(1, frame.FrameCount);
    }

    [TestMethod]
    public void AnEmptyAudioFrame_ReportsNoFramesRatherThanThrowing()
    {
        var frame = new AudioFrame([], 48000, 2, AudioSampleFormat.F32Le, 0, 0, 0, 0, 0);
        Assert.AreEqual(0, frame.FrameCount);
    }

    // ---------------------------------------------------------------- stream properties

    [TestMethod]
    public void StreamProperties_BuildOnlyWhatWasSet()
    {
        var props = new StreamProperties(StreamMediaType.Video, StreamCategory.Capture);
        Assert.IsTrue(props.Values.Count > 0, "media type and category must always be present");
        Assert.IsFalse(props.Values.ContainsKey("node.name"), "nothing else should be invented");
    }

    [TestMethod]
    public void StreamProperties_WithersAreChainableAndAllLand()
    {
        StreamProperties props = new StreamProperties(StreamMediaType.Audio, StreamCategory.Playback)
            .WithRole("Music")
            .WithNodeName("my_node")
            .WithNodeDescription("My Node")
            .WithTargetObject("some_sink")
            .With("custom.key", "custom value");

        Assert.AreEqual("Music", props.Values["media.role"]);
        Assert.AreEqual("my_node", props.Values["node.name"]);
        Assert.AreEqual("My Node", props.Values["node.description"]);
        Assert.AreEqual("custom value", props.Values["custom.key"]);
    }

    [TestMethod]
    public void StreamProperties_LastWriteWinsForTheSameKey()
    {
        StreamProperties props = new StreamProperties(StreamMediaType.Audio, StreamCategory.Capture)
            .WithNodeName("first")
            .WithNodeName("second");

        Assert.AreEqual("second", props.Values["node.name"]);
    }

    [TestMethod]
    public void StreamProperties_RejectNullAndEmptyRatherThanWritingThemNative()
    {
        var props = new StreamProperties(StreamMediaType.Video, StreamCategory.Capture);
        Assert.ThrowsExactly<ArgumentNullException>(() => props.WithRole(null!));
        Assert.ThrowsExactly<ArgumentException>(() => props.WithNodeName(""));
        Assert.ThrowsExactly<ArgumentNullException>(() => props.With(null!, "v"));
    }

    [TestMethod]
    public void StreamProperties_AcceptMultiByteAndLongValues()
    {
        // These become C strings, so the byte length is what matters, not the char count.
        string emoji = string.Concat(Enumerable.Repeat("\U0001F3B5", 200));
        StreamProperties props = new StreamProperties(StreamMediaType.Audio, StreamCategory.Playback)
            .WithNodeDescription(emoji);

        Assert.AreEqual(emoji, props.Values["node.description"]);
    }

    // ---------------------------------------------------------------- SpaDictBuilder
    //
    // SpaDictBuilder is a ref struct over stack memory, so it cannot be captured in the lambda that
    // Assert.ThrowsExactly needs. The try/Assert.Fail/catch pairs below are that constraint, not a
    // swallowed exception: reaching Assert.Fail means the expected throw did not happen.

    [TestMethod]
    public void SpaDictBuilder_CountsWhatWasAdded()
    {
        Span<byte> scratch = stackalloc byte[256];
        Span<spa_dict_item> items = stackalloc spa_dict_item[4];
        var b = new SpaDictBuilder(scratch, items);

        Assert.AreEqual(0, b.Count);
        b.Add("a"u8, "1"u8);
        b.Add("b"u8, 42u);
        b.Add("c"u8, "value");
        Assert.AreEqual(3, b.Count);
        Assert.AreEqual(3u, b.Build().n_items);
    }

    [TestMethod]
    public void SpaDictBuilder_BuildAlwaysReportsUnsortedFlags()
    {
        // SPA_DICT_FLAG_SORTED is bit 0. A stray set bit tells PipeWire it may binary-search an
        // unsorted array, which silently misses properties.
        Span<byte> scratch = stackalloc byte[64];
        Span<spa_dict_item> items = stackalloc spa_dict_item[2];
        var b = new SpaDictBuilder(scratch, items);
        b.Add("z"u8, "1"u8);
        b.Add("a"u8, "2"u8);          // deliberately out of order
        Assert.AreEqual(0u, b.Build().flags);
    }

    [TestMethod]
    public void SpaDictBuilder_RefusesMoreItemsThanTheCallerSized()
    {
        Span<byte> scratch = stackalloc byte[256];
        Span<spa_dict_item> items = stackalloc spa_dict_item[2];
        var b = new SpaDictBuilder(scratch, items);
        b.Add("a"u8, "1"u8);
        b.Add("b"u8, "2"u8);

        // A silent drop here would send an incomplete property set to the daemon.
        try
        {
            b.Add("c"u8, "3"u8);
            Assert.Fail("adding past the item buffer must throw, not drop the item");
        }
        catch (InvalidOperationException) { }
    }

    [TestMethod]
    public void SpaDictBuilder_RefusesToOverrunTheScratchBuffer()
    {
        Span<byte> scratch = stackalloc byte[16];
        Span<spa_dict_item> items = stackalloc spa_dict_item[4];
        var b = new SpaDictBuilder(scratch, items);

        try
        {
            b.Add("a-fairly-long-key"u8, "and-a-fairly-long-value"u8);
            Assert.Fail("exhausting the scratch must throw rather than write past it");
        }
        catch (InvalidOperationException) { }
    }

    [TestMethod]
    public void SpaDictBuilder_FitsExactlyWhenTheScratchIsExactlyBigEnough()
    {
        // "k" + NUL + "v" + NUL = 4 bytes. An off-by-one in Reserve shows up right here.
        Span<byte> scratch = stackalloc byte[4];
        Span<spa_dict_item> items = stackalloc spa_dict_item[1];
        var b = new SpaDictBuilder(scratch, items);
        b.Add("k"u8, "v"u8);
        Assert.AreEqual(1, b.Count);
    }

    [TestMethod]
    public void SpaDictBuilder_OneByteShortThrows()
    {
        Span<byte> scratch = stackalloc byte[3];
        Span<spa_dict_item> items = stackalloc spa_dict_item[1];
        var b = new SpaDictBuilder(scratch, items);
        try
        {
            b.Add("k"u8, "v"u8);
            Assert.Fail("4 bytes are needed for k\\0v\\0; 3 must not be accepted");
        }
        catch (InvalidOperationException) { }
    }

    [TestMethod]
    public void SpaDictBuilder_FormatsTheFullUnsignedRange()
    {
        // uint.MaxValue is 10 digits; the reserve is 11 with the terminator.
        Span<byte> scratch = stackalloc byte[64];
        Span<spa_dict_item> items = stackalloc spa_dict_item[2];
        var b = new SpaDictBuilder(scratch, items);
        b.Add("min"u8, 0u);
        b.Add("max"u8, uint.MaxValue);
        Assert.AreEqual(2, b.Count);
    }

    [TestMethod]
    public void SpaDictBuilder_AcceptsAnEmptyValue()
    {
        Span<byte> scratch = stackalloc byte[32];
        Span<spa_dict_item> items = stackalloc spa_dict_item[1];
        var b = new SpaDictBuilder(scratch, items);
        b.Add("k"u8, ""u8);
        Assert.AreEqual(1, b.Count);
    }

    [TestMethod]
    public void SpaDictBuilder_RejectsANullManagedValue()
    {
        Span<byte> scratch = stackalloc byte[32];
        Span<spa_dict_item> items = stackalloc spa_dict_item[1];
        var b = new SpaDictBuilder(scratch, items);
        try
        {
            b.Add("k"u8, (string)null!);
            Assert.Fail("a null value would become a null char* the daemon then strlen's");
        }
        catch (ArgumentNullException) { }
    }

    [TestMethod]
    public void SpaDictBuilder_EmptyBuildIsUsable()
    {
        Span<byte> scratch = stackalloc byte[8];
        Span<spa_dict_item> items = stackalloc spa_dict_item[1];
        var b = new SpaDictBuilder(scratch, items);
        spa_dict dict = b.Build();
        Assert.AreEqual(0u, dict.n_items);
        Assert.AreEqual(0u, dict.flags);
    }

    // ---------------------------------------------------------------- frame snapshots

    [TestMethod]
    public void AClonedVideoFrame_KeepsItsBytesAndMetadata()
    {
        byte[] pixels = [1, 2, 3, 4, 5, 6, 7, 8];
        var frame = new VideoFrame(
            pixels, stride: 4, width: 2, height: 2, format: PixelFormat.Bgra,
            sequenceNumber: 9, presentationTimeNs: 100, captureClockNs: 200,
            mediaClockNs: 300, delayNs: 5);

        OwnedVideoFrame owned = frame.Clone();

        CollectionAssert.AreEqual(pixels, owned.Pixels.ToArray());
        Assert.AreEqual(4, owned.Stride);
        Assert.AreEqual(2, owned.Width);
        Assert.AreEqual(2, owned.Height);
        Assert.AreEqual(PixelFormat.Bgra, owned.Format);
        Assert.AreEqual(9ul, owned.SequenceNumber);
        Assert.AreEqual(100L, owned.PresentationTimeNs);
        Assert.AreEqual(200L, owned.CaptureClockNs);
        Assert.AreEqual(300L, owned.MediaClockNs);
        Assert.AreEqual(5L, owned.DelayNs);

        // A copy, not a view: mutating the source afterwards must not move the snapshot.
        pixels[0] = 99;
        Assert.AreEqual(1, owned.Pixels[0]);
    }

    [TestMethod]
    public void CloningAnFdBackedFrame_RefusesRatherThanKeepingNothing()
    {
        // A DMA-BUF frame has no host bytes: Data is empty and the descriptors die with the
        // buffer. A byte copy would keep an empty frame that reads as valid, so Clone refuses
        // and points at the descriptor duplication that actually keeps something.
        var frame = new VideoFrame(
            [], stride: 0, width: 2, height: 2, format: PixelFormat.Bgra,
            sequenceNumber: 9, bufferType: PipeWireBufferType.DmaBuf, fd: 42);

        // A ref struct cannot cross a lambda boundary, so the throw is asserted by hand.
        try
        {
            frame.Clone();
            Assert.Fail("cloning an fd-backed frame must refuse");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [TestMethod]
    public void AClonedAudioChunk_KeepsItsSamplesAndMetadata()
    {
        byte[] samples = [10, 20, 30, 40];
        var chunk = new AudioFrame(
            samples, sampleRate: 48000, channels: 2, format: AudioSampleFormat.F32Le,
            sequenceNumber: 4, presentationTimeNs: -1, captureClockNs: 700, delayNs: 2);

        OwnedAudioFrame owned = chunk.Clone();

        CollectionAssert.AreEqual(samples, owned.Samples.ToArray());
        Assert.AreEqual(48000, owned.SampleRate);
        Assert.AreEqual(2, owned.Channels);
        Assert.AreEqual(AudioSampleFormat.F32Le, owned.Format);
        Assert.AreEqual(4ul, owned.SequenceNumber);
        Assert.IsNull(owned.PresentationTimeNs);
        Assert.AreEqual(700L, owned.CaptureClockNs);
        Assert.AreEqual(2L, owned.DelayNs);

        samples[0] = 99;
        Assert.AreEqual(10, owned.Samples[0]);
    }

    [TestMethod]
    public void OwnedFrames_CompareByValue()
    {
        var first = new OwnedVideoFrame(
            ImmutableArray.Create<byte>(1, 2), 2, 1, 1, PixelFormat.Rgba, 0,
            default, null, null, null, 0);
        var same = new OwnedVideoFrame(
            ImmutableArray.Create<byte>(1, 2), 2, 1, 1, PixelFormat.Rgba, 0,
            default, null, null, null, 0);
        var different = new OwnedVideoFrame(
            ImmutableArray.Create<byte>(1, 3), 2, 1, 1, PixelFormat.Rgba, 0,
            default, null, null, null, 0);

        Assert.AreEqual(first, same);
        Assert.AreEqual(first.GetHashCode(), same.GetHashCode());
        Assert.AreNotEqual(first, different);

        var audio = new OwnedAudioFrame(
            ImmutableArray.Create<byte>(1), 48000, 1, AudioSampleFormat.S16Le, 0,
            null, null, null, 0);
        var audioSame = new OwnedAudioFrame(
            ImmutableArray.Create<byte>(1), 48000, 1, AudioSampleFormat.S16Le, 0,
            null, null, null, 0);
        Assert.AreEqual(audio, audioSame);
        Assert.AreEqual(audio.GetHashCode(), audioSame.GetHashCode());
    }

    [TestMethod]
    public void DuplicatingAHostMemoryFrame_ReportsNoDescriptor()
    {
        // No libc call on this path: a frame with no fd answers -1 without duplicating.
        var frame = new VideoFrame(
            [1, 2, 3, 4], stride: 4, width: 1, height: 1, format: PixelFormat.Bgra,
            sequenceNumber: 0);
        Assert.IsFalse(frame.IsFdBacked);
        Assert.AreEqual(-1, frame.DuplicateFd());
    }

    [TestMethod]
    public void ReadFloatArray_ReturnsEmptyWhenThePropertyIsAbsent()
    {
        // A node with no channel volumes has no array to read, which is not an error.
        Assert.AreEqual(0, PipeWireNodeControl.ReadFloatArray(null, SpaProp.ChannelVolumes).Length);

        var props = new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
            [new SpaProperty(SpaProp.Volume, 0, new SpaFloat(0.5f))]);
        Assert.AreEqual(0, PipeWireNodeControl.ReadFloatArray(props, SpaProp.ChannelVolumes).Length);

        var withVolumes = new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
            [new SpaProperty(SpaProp.ChannelVolumes, 0,
                new SpaArray(SpaType.Float, [new SpaFloat(0.5f), new SpaFloat(0.25f)]))]);
        CollectionAssert.AreEqual(
            new[] { 0.5f, 0.25f },
            PipeWireNodeControl.ReadFloatArray(withVolumes, SpaProp.ChannelVolumes).ToArray());
    }

    [TestMethod]
    public void SyncBuffersParam_DeclaresSyncBlocksAndMandatoryMetaType()
    {
        // Explicit sync rides two extra data blocks plus a mandatory metaType: a peer that cannot
        // carry timeline metadata must refuse rather than silently accept unordered buffers.
        Span<byte> buf = stackalloc byte[256];
        int len = SpaFormatPod.WriteVideoBuffersParam(buf, size: 1024, stride: 256,
            dataTypes: 1 << (int)SpaDataType.DmaBuf, blocks: 1, syncDataBlocks: 2);

        Assert.IsTrue(SpaPod.TryParse(buf[..len], out SpaValue? value));
        var o = (SpaObject)value!;
        Assert.AreEqual(3, ((SpaInt)o[(uint)SpaParamBuffers.Blocks]!).Value);

        SpaProperty? metaType = o.Find((uint)SpaParamBuffers.MetaType);
        Assert.IsNotNull(metaType, "the sync buffers param must carry a metaType");
        Assert.AreNotEqual(0u, metaType.Flags & SpaPodPropFlag.Mandatory,
            "the metaType must be mandatory, not advisory");
        Assert.AreEqual(1 << (int)SpaMetaType.SyncTimeline, ((SpaInt)metaType.Value).Value);
    }

    [TestMethod]
    public void SyncTimelineMetaParam_RoundTripsItsContract()
    {
        Span<byte> buf = stackalloc byte[64];
        int len = SpaFormatPod.WriteSyncTimelineMetaParam(buf);

        Assert.IsTrue(SpaPod.TryParse(buf[..len], out SpaValue? value));
        var o = (SpaObject)value!;
        Assert.AreEqual(SpaType.ObjectParamMeta, o.ObjectType);
        Assert.AreEqual(SpaParamType.Meta, o.ObjectId);
    }
}
