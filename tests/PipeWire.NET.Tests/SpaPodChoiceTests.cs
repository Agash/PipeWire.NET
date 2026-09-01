using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Choice and modifier parsing: the paths DMA-BUF negotiation runs on.
/// </summary>
/// <remarks>
/// Both readers peek before they commit and must restore their position when they decline, because
/// the caller falls back to a plain typed read on the same reader. A partial read that leaves the
/// position moved corrupts the value the caller then reads instead - silently, and only for the
/// malformed inputs nobody sends until a different compositor does.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SpaPodChoiceTests
{
    private static byte[] Pod(uint type, ReadOnlySpan<byte> body)
    {
        int padded = (body.Length + 7) & ~7;
        var pod = new byte[8 + padded];
        BitConverter.TryWriteBytes(pod.AsSpan(0, 4), (uint)body.Length);
        BitConverter.TryWriteBytes(pod.AsSpan(4, 4), type);
        body.CopyTo(pod.AsSpan(8));
        return pod;
    }

    /// <summary>A <c>spa_pod_choice</c>: header, then [choiceType][flags][childSize][childType], then values.</summary>
    private static byte[] ChoicePod(uint childType, uint childSize, params byte[][] values)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(0u));          // choiceType (Enum)
        body.AddRange(BitConverter.GetBytes(0u));          // flags
        body.AddRange(BitConverter.GetBytes(childSize));
        body.AddRange(BitConverter.GetBytes(childType));
        foreach (byte[] v in values) body.AddRange(v);
        return Pod(SpaType.Choice, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(body));
    }

    private static byte[] LongPod(long v) => Pod(SpaType.Long, BitConverter.GetBytes(v));

    // ------------------------------------------------------------------ modifiers

    [TestMethod]
    public void APlainLong_ReadsAsASingleFixatedModifier()
    {
        var reader = new SpaPodReader(LongPod(0x0100000000000001L));
        Assert.IsTrue(reader.TryReadModifier(out long first, out int count));
        Assert.AreEqual(0x0100000000000001L, first);
        Assert.AreEqual(1, count, "a plain Long is one modifier, already fixated");
    }

    [TestMethod]
    public void AChoiceOfOneModifier_ReportsCountOne()
    {
        byte[] pod = ChoicePod(SpaType.Long, 8, BitConverter.GetBytes(42L));
        var reader = new SpaPodReader(pod);
        Assert.IsTrue(reader.TryReadModifier(out long first, out int count));
        Assert.AreEqual(42L, first);
        Assert.AreEqual(1, count, "one value means the peer already narrowed to a single modifier");
    }

    [TestMethod]
    public void AChoiceOfSeveralModifiers_ReturnsThePreferredOneAndTheCount()
    {
        // The first value is the preferred modifier; more than one means fixation is still needed.
        byte[] pod = ChoicePod(SpaType.Long, 8,
            BitConverter.GetBytes(0x0100000000000002L),
            BitConverter.GetBytes(0x0100000000000003L),
            BitConverter.GetBytes(0L));

        var reader = new SpaPodReader(pod);
        Assert.IsTrue(reader.TryReadModifier(out long first, out int count));
        Assert.AreEqual(0x0100000000000002L, first, "the first value is the preferred modifier");
        Assert.AreEqual(3, count, "three offered modifiers still need fixating");
    }

    [TestMethod]
    public void AChoiceOfTheWrongChildType_IsDeclinedAndLeavesThePositionAlone()
    {
        // A choice of Int where a Long was expected: declining must not consume anything, because
        // the caller will try a plain read next.
        byte[] pod = ChoicePod(SpaType.Int, 4, BitConverter.GetBytes(7));
        var reader = new SpaPodReader(pod);
        int before = reader.Position;

        Assert.IsFalse(reader.TryReadModifier(out _, out _));
        Assert.AreEqual(before, reader.Position, "a declined read must restore the position");
    }

    [TestMethod]
    public void AChoiceWithTheWrongChildSize_IsDeclined()
    {
        // childType says Long but childSize says 4, which cannot describe a 64-bit modifier.
        byte[] pod = ChoicePod(SpaType.Long, 4, BitConverter.GetBytes(42L));
        var reader = new SpaPodReader(pod);
        Assert.IsFalse(reader.TryReadModifier(out _, out _));
        Assert.AreEqual(0, reader.Position);
    }

    [TestMethod]
    public void ANonChoiceNonLongPod_IsDeclinedWithoutConsuming()
    {
        foreach (uint type in (uint[])[SpaType.Int, SpaType.Id, SpaType.Rectangle, SpaType.Float])
        {
            var reader = new SpaPodReader(Pod(type, new byte[8]));
            Assert.IsFalse(reader.TryReadModifier(out _, out _), $"type {type} is not a modifier");
            Assert.AreEqual(0, reader.Position, $"type {type} left the position moved");
        }
    }

    [TestMethod]
    public void ALongPodTooShortForItsValue_IsDeclined()
    {
        // Declares Long but only carries 4 bytes.
        byte[] pod = Pod(SpaType.Long, new byte[4]);
        var reader = new SpaPodReader(pod);
        Assert.IsFalse(reader.TryReadModifier(out _, out _));
        Assert.AreEqual(0, reader.Position);
    }

    [TestMethod]
    public void AChoiceClaimingMoreValuesThanItCarries_IsDeclined()
    {
        // Hand-build a choice whose declared size promises values that are not there.
        byte[] pod = ChoicePod(SpaType.Long, 8, BitConverter.GetBytes(1L));
        BitConverter.TryWriteBytes(pod.AsSpan(0, 4), 16u + 80u);   // claim ten values

        var reader = new SpaPodReader(pod);
        Assert.IsFalse(reader.TryReadModifier(out _, out _),
            "a choice may not promise more values than the buffer holds");
        Assert.AreEqual(0, reader.Position);
    }

    [TestMethod]
    public void AChoiceWithNoValuesAtAll_IsDeclined()
    {
        byte[] pod = ChoicePod(SpaType.Long, 8);   // header only, zero values
        var reader = new SpaPodReader(pod);
        Assert.IsFalse(reader.TryReadModifier(out _, out _), "a choice of nothing offers no modifier");
    }

    [TestMethod]
    public void ATruncatedChoiceHeader_IsDeclined()
    {
        byte[] full = ChoicePod(SpaType.Long, 8, BitConverter.GetBytes(42L));
        for (int cut = 8; cut < full.Length; cut++)
        {
            var reader = new SpaPodReader(full.AsSpan(0, cut).ToArray());
            // Whatever it decides, it must neither throw nor leave the position moved on refusal.
            if (!reader.TryReadModifier(out _, out _))
                Assert.AreEqual(0, reader.Position, $"cut at {cut} left the position moved");
        }
    }

    // ------------------------------------------------------------------ choice unwrapping

    [TestMethod]
    public void UnwrappingAChoice_YieldsTheFirstValue()
    {
        Span<byte> rect = stackalloc byte[8];
        BitConverter.TryWriteBytes(rect[..4], 1920u);
        BitConverter.TryWriteBytes(rect[4..], 1080u);

        byte[] pod = ChoicePod(SpaType.Rectangle, 8, rect.ToArray());
        var reader = new SpaPodReader(pod);

        Assert.IsTrue(reader.TryUnwrapChoice(out SpaPodReader inner));
        Assert.AreEqual((1920u, 1080u), inner.ReadRectangle());
    }

    [TestMethod]
    public void UnwrappingAPlainValue_IsDeclinedAndLeavesItReadable()
    {
        // This is the fallback the caller depends on: decline, then read the plain value.
        Span<byte> rect = stackalloc byte[8];
        BitConverter.TryWriteBytes(rect[..4], 640u);
        BitConverter.TryWriteBytes(rect[4..], 480u);
        byte[] pod = Pod(SpaType.Rectangle, rect);

        var reader = new SpaPodReader(pod);
        Assert.IsFalse(reader.TryUnwrapChoice(out _), "a plain value is not a choice");
        Assert.AreEqual((640u, 480u), reader.ReadRectangle(),
            "declining must leave the pod fully readable");
    }

    [TestMethod]
    public void UnwrappingATruncatedChoice_IsDeclinedRatherThanOverrunning()
    {
        byte[] full = ChoicePod(SpaType.Rectangle, 8, new byte[8]);
        for (int cut = 8; cut < full.Length; cut++)
        {
            var reader = new SpaPodReader(full.AsSpan(0, cut).ToArray());
            _ = reader.TryUnwrapChoice(out _);   // must not throw at any cut point
        }
    }

    [TestMethod]
    public void UnwrappingAChoiceWhoseChildSizeExceedsTheBuffer_IsDeclined()
    {
        byte[] pod = ChoicePod(SpaType.Rectangle, 8, new byte[8]);
        // childSize is the 3rd u32 of the body, at offset 8 + 8 = 16.
        BitConverter.TryWriteBytes(pod.AsSpan(16, 4), 4096u);

        var reader = new SpaPodReader(pod);
        Assert.IsFalse(reader.TryUnwrapChoice(out _),
            "a child larger than the buffer must not produce a reader over it");
    }

    [TestMethod]
    public void AChoiceOfIds_UnwrapsToTheFirstId()
    {
        byte[] pod = ChoicePod(SpaType.Id, 4,
            BitConverter.GetBytes(7u), BitConverter.GetBytes(9u));

        var reader = new SpaPodReader(pod);
        Assert.IsTrue(reader.TryUnwrapChoice(out SpaPodReader inner));
        Assert.AreEqual(7u, inner.ReadId(), "the first entry is the preferred one");
    }
}
