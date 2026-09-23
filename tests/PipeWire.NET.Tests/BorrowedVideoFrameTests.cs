using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Media;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The allocation-free pull shape. What is worth pinning is not the field copying but the two
/// properties that justify it existing beside the owning form: it carries raw pool descriptors
/// rather than duplicates, and it is a value type that allocates nothing.
/// </summary>
[TestClass]
public sealed class BorrowedVideoFrameTests
{
    private static BorrowedVideoFrame Nv12(long fd0 = 7, long fd1 = 8)
    {
        ReadOnlySpan<BorrowedVideoPlane> planes =
        [
            new BorrowedVideoPlane(fd0, 0, 1920, 1920 * 1080),
            new BorrowedVideoPlane(fd1, 1920 * 1080, 1920, 1920 * 540),
        ];

        return new BorrowedVideoFrame(
            planes,
            1920,
            1920,
            1080,
            PixelFormat.Nv12,
            DrmFormat.FromPixelFormat(PixelFormat.Nv12),
            DrmFormatModifier.Linear,
            sequenceNumber: 3,
            presentationTimestampNs: 111,
            queuedTimeNs: 555,
            graphTimeNs: 222,
            streamPositionNs: 333,
            delayNs: 44,
            crop: null,
            transform: SpaMetaVideotransformValue.None
        );
    }

    [TestMethod]
    public void ItCarriesOnePlanePerDescriptor()
    {
        BorrowedVideoFrame frame = Nv12();

        Assert.AreEqual(2, frame.PlaneCount);
        Assert.IsTrue(frame.IsFdBacked);
        Assert.AreEqual(7, frame[0].Fd);
        Assert.AreEqual(8, frame[1].Fd);
        // The UV plane sits at an offset inside the same allocation, not at zero.
        Assert.AreEqual(0u, frame[0].Offset);
        Assert.AreEqual(1920u * 1080u, frame[1].Offset);
    }

    /// <summary>
    /// A descriptor keeps the full width <c>spa_data.fd</c> carries.
    /// </summary>
    /// <remarks>
    /// This plane's descriptor was an <c>int</c>, populated from the frame's <c>long</c> by a
    /// <c>checked((int)...)</c> cast on the per-frame path. Linux hands out small descriptor
    /// numbers, so the narrowing never bit in practice - it would simply have thrown
    /// <see cref="OverflowException"/> inside a stream callback on the first host that did
    /// otherwise. <c>spa_data.fd</c> is <c>int64_t</c>, and this is the same field.
    /// </remarks>
    [TestMethod]
    public void APlaneDescriptor_KeepsTheFullWidthSpaCarries()
    {
        const long WiderThanInt = (long)int.MaxValue + 1;

        BorrowedVideoFrame frame = Nv12(fd0: WiderThanInt, fd1: 8);

        Assert.AreEqual(WiderThanInt, frame[0].Fd, "the descriptor was narrowed to 32 bits");
    }

    /// <summary>
    /// The pair an importer binds with. A frame that carried planes but no fourcc would be useless
    /// to the consumer this shape exists for.
    /// </summary>
    [TestMethod]
    public void ItCarriesTheFourccAndModifierAnImporterNeeds()
    {
        BorrowedVideoFrame frame = Nv12();

        Assert.AreEqual(DrmFormat.FromVideoFormat(SpaVideoFormat.Nv12), frame.DrmFourcc);
        Assert.AreNotEqual(DrmFormat.Invalid, frame.DrmFourcc);
        Assert.AreEqual(DrmFormatModifier.Linear, frame.Modifier);
    }

    [TestMethod]
    public void ItCarriesTheTimestampsAudioIsAlignedAgainst()
    {
        BorrowedVideoFrame frame = Nv12();

        Assert.AreEqual(111L, frame.PresentationTimestampNs);
        Assert.AreEqual(555L, frame.QueuedTimeNs);
        Assert.AreEqual(222L, frame.GraphTimeNs);
        Assert.AreEqual(333L, frame.StreamPositionNs);
        Assert.AreEqual(44L, frame.DelayNs);
        Assert.AreEqual(3UL, frame.SequenceNumber);
    }

    /// <summary>
    /// A value type: assigning one copies it, so a consumer holding a frame is unaffected by the
    /// next one arriving. This is what makes the unlocked hand-off safe once the copy is taken.
    /// </summary>
    [TestMethod]
    public void CopyingAFrameCopiesItsPlanes()
    {
        BorrowedVideoFrame first = Nv12(fd0: 7);
        BorrowedVideoFrame copy = first;
        BorrowedVideoFrame second = Nv12(fd0: 99);

        Assert.AreEqual(7, copy[0].Fd, "the copy must not track a later frame");
        Assert.AreEqual(99, second[0].Fd);
    }

    [TestMethod]
    public void TheDefaultFrameCarriesNothing()
    {
        BorrowedVideoFrame frame = default;

        Assert.AreEqual(0, frame.PlaneCount);
        Assert.IsFalse(frame.IsFdBacked);
    }

    [TestMethod]
    public void IndexingPastThePlaneCountThrows()
    {
        BorrowedVideoFrame frame = Nv12();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = frame[2]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = frame[-1]);
    }

    /// <summary>
    /// Four is the practical maximum (packed 1, NV12 2, I420 3, plus one modifier aux plane). A
    /// producer offering more is truncated rather than overflowing the inline storage.
    /// </summary>
    [TestMethod]
    public void MorePlanesThanTheInlineStorageAreTruncated()
    {
        ReadOnlySpan<BorrowedVideoPlane> many =
        [
            new(1, 0, 16, 16),
            new(2, 0, 16, 16),
            new(3, 0, 16, 16),
            new(4, 0, 16, 16),
            new(5, 0, 16, 16),
        ];

        BorrowedVideoFrame frame = new(
            many,
            16,
            4,
            4,
            PixelFormat.Bgra,
            0,
            DrmFormatModifier.Invalid,
            0,
            null,
            null,
            null,
            null,
            0,
            null,
            SpaMetaVideotransformValue.None
        );

        Assert.AreEqual(4, frame.PlaneCount);
        Assert.AreEqual(4, frame[3].Fd);
    }
}
