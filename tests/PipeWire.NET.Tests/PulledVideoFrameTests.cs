using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Covers the pull path's frame, which owns what it carries. The push path's
/// <see cref="VideoFrame"/> borrows from the pool and is checked elsewhere.
/// </summary>
[TestClass]
public sealed class PulledVideoFrameTests
{
    private static PulledVideoFrame HostFrame(
        ImmutableArray<byte>? pixels = null,
        PixelFormat format = PixelFormat.Bgra,
        long? presentationTimeNs = 1234) =>
        new(
            pixels: pixels ?? [1, 2, 3, 4],
            planes: ImmutableArray<PulledVideoPlane>.Empty,
            stride: 4,
            width: 1,
            height: 1,
            format: format,
            drmFourcc: DrmFormat.FromPixelFormat(format),
            modifier: DrmFormatModifier.Invalid,
            sequenceNumber: 7,
            bufferType: PipeWireBufferType.MemPtr,
            color: default,
            presentationTimeNs: presentationTimeNs,
            captureClockNs: 5678,
            mediaClockNs: 9012,
            delayNs: 42,
            crop: new VideoRegion(1, 2, 3, 4),
            transform: SpaMetaVideotransformValue.Rotate90);

    [TestMethod]
    public void AHostFrameCarriesItsBytesAndNoDescriptors()
    {
        using PulledVideoFrame frame = HostFrame();

        Assert.IsFalse(frame.IsFdBacked);
        Assert.IsTrue(frame.Planes.IsEmpty);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, frame.Pixels.ToArray());
    }

    /// <summary>
    /// The whole point of the type: everything a consumer needs survives the cycle, including the
    /// timestamp it aligns audio against and the fourcc/modifier pair an importer binds with.
    /// </summary>
    [TestMethod]
    public void AFrameKeepsTheMetadataAConsumerPullsItFor()
    {
        using PulledVideoFrame frame = HostFrame();

        Assert.AreEqual(1234L, frame.PresentationTimeNs);
        Assert.AreEqual(5678L, frame.CaptureClockNs);
        Assert.AreEqual(9012L, frame.MediaClockNs);
        Assert.AreEqual(42L, frame.DelayNs);
        Assert.AreEqual(7UL, frame.SequenceNumber);
        Assert.AreEqual(new VideoRegion(1, 2, 3, 4), frame.Crop);
        Assert.AreEqual(SpaMetaVideotransformValue.Rotate90, frame.Transform);
        Assert.AreEqual(DrmFormatModifier.Invalid, frame.Modifier);
    }

    /// <summary>The fourcc must be the one an importer would use, not merely non-zero.</summary>
    [TestMethod]
    public void AFrameReportsTheFourccForItsFormat()
    {
        using PulledVideoFrame frame = HostFrame(format: PixelFormat.Nv12);

        Assert.AreEqual(DrmFormat.FromVideoFormat(SpaVideoFormat.Nv12), frame.DrmFourcc);
    }

    [TestMethod]
    public void AFrameWithNoProducerTimestampReportsNull()
    {
        using PulledVideoFrame frame = HostFrame(presentationTimeNs: null);

        Assert.IsNull(frame.PresentationTimeNs);
    }

    /// <summary>
    /// Disposal has to be idempotent: the capture path disposes a displaced frame while the
    /// consumer that took it may dispose it too, and closing a descriptor twice in a process that
    /// keeps opening them can close an unrelated file that reused the number.
    /// </summary>
    /// <summary>
    /// The dmabuf shape: planes present, and no pixel copy. This is the property the zero-copy path
    /// depends on - the frame carries descriptors to GPU memory, so copying the bytes would both
    /// defeat the point and be impossible, since there are no host bytes to copy.
    /// </summary>
    [TestMethod]
    public void ADmaBufFrameCarriesDescriptorsAndCopiesNoPixels()
    {
        // An invalid descriptor: SafeHandle skips ReleaseHandle for one, so this constructs and
        // disposes on any platform while still exercising the plane-carrying path.
        using PulledVideoFrame frame = new(
            pixels: ImmutableArray<byte>.Empty,
            planes: [new PulledVideoPlane(new SafeDescriptorHandle(), 0, 1920 * 4, 1920 * 1080 * 4)],
            stride: 1920 * 4,
            width: 1920,
            height: 1080,
            format: PixelFormat.Bgra,
            drmFourcc: DrmFormat.FromPixelFormat(PixelFormat.Bgra),
            modifier: DrmFormatModifier.Linear,
            sequenceNumber: 1,
            bufferType: PipeWireBufferType.DmaBuf,
            color: default,
            presentationTimeNs: 1,
            captureClockNs: null,
            mediaClockNs: null,
            delayNs: 0,
            crop: null,
            transform: SpaMetaVideotransformValue.None);

        Assert.IsTrue(frame.IsFdBacked);
        Assert.IsTrue(frame.Pixels.IsEmpty, "a dmabuf frame must not carry a pixel copy");
        Assert.AreEqual(1, frame.Planes.Length);
        Assert.AreEqual(1920u * 1080u * 4u, frame.Planes[0].Size);
        Assert.AreEqual(DrmFormatModifier.Linear, frame.Modifier);
    }

    /// <summary>A multi-plane format keeps one descriptor per plane, since an importer binds each.</summary>
    [TestMethod]
    public void AMultiPlaneFrameKeepsOneDescriptorPerPlane()
    {
        using PulledVideoFrame frame = new(
            pixels: ImmutableArray<byte>.Empty,
            planes:
            [
                new PulledVideoPlane(new SafeDescriptorHandle(), 0, 1920, 1920 * 1080),
                new PulledVideoPlane(new SafeDescriptorHandle(), 1920 * 1080, 1920, 1920 * 540),
            ],
            stride: 1920,
            width: 1920,
            height: 1080,
            format: PixelFormat.Nv12,
            drmFourcc: DrmFormat.FromPixelFormat(PixelFormat.Nv12),
            modifier: DrmFormatModifier.Linear,
            sequenceNumber: 2,
            bufferType: PipeWireBufferType.DmaBuf,
            color: default,
            presentationTimeNs: null,
            captureClockNs: null,
            mediaClockNs: null,
            delayNs: 0,
            crop: null,
            transform: SpaMetaVideotransformValue.None);

        Assert.AreEqual(2, frame.Planes.Length);
        // The UV plane sits at an offset inside the same allocation, not at zero.
        Assert.AreEqual(0u, frame.Planes[0].Offset);
        Assert.AreEqual(1920u * 1080u, frame.Planes[1].Offset);
    }

    [TestMethod]
    public void DisposingTwiceIsHarmless()
    {
        PulledVideoFrame frame = HostFrame();

        frame.Dispose();
        frame.Dispose();
    }
}
