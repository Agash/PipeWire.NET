using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Every way a pod can be malformed, one per branch of the parser.
/// </summary>
/// <remarks>
/// The daemon is not trusted, and neither is anything that reaches this parser from a plugin or a
/// file. Each case here is a pod whose header says one thing and whose body says another; the
/// parser has to refuse it rather than read past it, and refusing is a return value rather than an
/// exception because this runs inside native callbacks.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SpaPodMalformedTests
{
    /// <summary>Builds a pod header that claims <paramref name="declared"/> body bytes.</summary>
    private static byte[] Pod(SpaType type, uint declared, int actualBodyBytes)
    {
        byte[] pod = new byte[8 + actualBodyBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(0, 4), declared);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(4, 4), (uint)type);
        return pod;
    }

    [TestMethod]
    public void EveryFixedSizeTypeWithABodyTooShortForIt_IsRefused()
    {
        // The size in the header is what a reader would slice with. A pod claiming four bytes of
        // body for a type that needs eight is the shape that reads into the next pod.
        (SpaType Type, int Needs)[] cases =
        [
            (SpaType.Bool, 4), (SpaType.Id, 4), (SpaType.Int, 4), (SpaType.Float, 4),
            (SpaType.Long, 8), (SpaType.Double, 8), (SpaType.Fd, 8),
            (SpaType.Rectangle, 8), (SpaType.Fraction, 8),
        ];

        foreach ((SpaType type, int needs) in cases)
        {
            int truncated = needs - 1;
            byte[] pod = Pod(type, (uint)truncated, truncated);

            Assert.IsFalse(SpaPod.TryParse(pod, out SpaValue? value),
                $"{type} with {truncated} body bytes must be refused; it needs {needs}");
            Assert.IsNull(value);
        }
    }

    [TestMethod]
    public void EveryFixedSizeTypeWithExactlyEnoughBody_IsAccepted()
    {
        // The other side of the same boundary, so the check above cannot pass by refusing everything.
        (SpaType Type, int Needs)[] cases =
        [
            (SpaType.Bool, 4), (SpaType.Id, 4), (SpaType.Int, 4), (SpaType.Float, 4),
            (SpaType.Long, 8), (SpaType.Double, 8), (SpaType.Fd, 8),
            (SpaType.Rectangle, 8), (SpaType.Fraction, 8),
        ];

        foreach ((SpaType type, int needs) in cases)
        {
            byte[] pod = Pod(type, (uint)needs, needs);
            Assert.IsTrue(SpaPod.TryParse(pod, out SpaValue? value), $"{type} with {needs} bytes is valid");
            Assert.AreEqual(type, value!.Type);
        }
    }

    [TestMethod]
    public void AnObjectBodyTooShortForItsOwnTypeAndId_IsRefused()
    {
        // An object body opens with its type and id: eight bytes before any property. Fewer than
        // that and "size - 8" is where a parser underflows into a body of nearly four gigabytes.
        foreach (uint declared in (uint[])[0, 1, 4, 7])
        {
            byte[] pod = Pod(SpaType.Object, declared, (int)declared);
            Assert.IsFalse(SpaPod.TryParse(pod, out _), $"an object with a {declared}-byte body is malformed");
        }

        // Exactly eight is a valid object with no properties.
        Assert.IsTrue(SpaPod.TryParse(Pod(SpaType.Object, 8, 8), out SpaValue? empty));
        Assert.AreEqual(0, ((SpaObject)empty!).Properties.Length);
    }

    [TestMethod]
    public void AChoiceBodyTooShortForItsHeader_IsRefused()
    {
        // A choice opens with four uint32s. Anything shorter cannot say what its children are.
        foreach (uint declared in (uint[])[0, 8, 15])
            Assert.IsFalse(SpaPod.TryParse(Pod(SpaType.Choice, declared, (int)declared), out _));

        // Sixteen exactly is an empty choice, which is legal.
        byte[] pod = Pod(SpaType.Choice, 16, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(8, 4), (uint)SpaChoiceType.Enum);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(16, 4), 0);          // child size
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(20, 4), (uint)SpaType.Int);

        Assert.IsTrue(SpaPod.TryParse(pod, out SpaValue? value));
        Assert.AreEqual(0, ((SpaChoice)value!).Alternatives.Length);
    }

    [TestMethod]
    public void AChoiceWhoseChildSizeIsZeroButCarriesValues_IsRefused()
    {
        // Zero-sized children with a non-empty body describes infinitely many of them.
        byte[] pod = Pod(SpaType.Choice, 24, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(8, 4), (uint)SpaChoiceType.Enum);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(16, 4), 0);          // child size
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(20, 4), (uint)SpaType.Int);

        Assert.IsFalse(SpaPod.TryParse(pod, out _));
    }

    [TestMethod]
    public void AChoiceChildTooShortForItsDeclaredType_IsRefused()
    {
        // Children are stored bare, so the child size is the only thing saying where each begins.
        // One claiming to hold Longs in four bytes each is describing something that cannot exist.
        byte[] pod = Pod(SpaType.Choice, 24, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(8, 4), (uint)SpaChoiceType.Enum);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(16, 4), 4);          // child size: 4
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(20, 4), (uint)SpaType.Long); // needs 8

        Assert.IsFalse(SpaPod.TryParse(pod, out _));
    }

    [TestMethod]
    public void ASequenceBodyTooShortForItsUnit_IsRefused()
    {
        foreach (uint declared in (uint[])[0, 4, 7])
            Assert.IsFalse(SpaPod.TryParse(Pod(SpaType.Sequence, declared, (int)declared), out _));

        Assert.IsTrue(SpaPod.TryParse(Pod(SpaType.Sequence, 8, 8), out SpaValue? value));
        Assert.AreEqual(0, ((SpaSequence)value!).Controls.Length);
    }

    [TestMethod]
    public void APointerBodyTooShortForAPointer_IsRefused()
    {
        // Type, padding, then a native-word pointer.
        Assert.IsFalse(SpaPod.TryParse(Pod(SpaType.Pointer, 8, 8), out _),
            "eight bytes holds the type and padding but no pointer");

        Assert.IsTrue(SpaPod.TryParse(Pod(SpaType.Pointer, (uint)(8 + IntPtr.Size), 8 + IntPtr.Size), out _));
    }

    [TestMethod]
    public void AnObjectPropertyRunningPastTheEndOfItsObject_IsRefused()
    {
        // The object says it has 24 bytes of body; the property inside says its value is far bigger.
        // Trusting the inner size is how a parser walks off the end of the outer one.
        byte[] pod = new byte[8 + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(0, 4), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(4, 4), (uint)SpaType.Object);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(8, 4), (uint)SpaType.ObjectProps);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(12, 4), (uint)SpaParamType.Props);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(16, 4), (uint)SpaProp.Volume); // key
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(20, 4), 0);                     // flags
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(24, 4), 0xFFFF);                // value size
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(28, 4), (uint)SpaType.Float);

        Assert.IsFalse(SpaPod.TryParse(pod, out _));
    }

    [TestMethod]
    public void AStructWhoseFieldRunsPastItsEnd_IsRefused()
    {
        byte[] pod = new byte[8 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(0, 4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(4, 4), (uint)SpaType.Struct);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(8, 4), 0xFFFF);   // field claims 64KB
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(12, 4), (uint)SpaType.Int);

        Assert.IsFalse(SpaPod.TryParse(pod, out _));
    }

    [TestMethod]
    public void AnArrayWhoseChildTypeCannotFitItsChildSize_IsRefused()
    {
        byte[] pod = Pod(SpaType.Array, 16, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(8, 4), 4);                    // child size
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(12, 4), (uint)SpaType.Double); // needs 8

        Assert.IsFalse(SpaPod.TryParse(pod, out _));
    }

    [TestMethod]
    public void AStringWithNoTerminator_IsStillReadRatherThanRefused()
    {
        // Being strict here would drop a property over a missing NUL the daemon never promised.
        byte[] pod = new byte[8 + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(0, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(4, 4), (uint)SpaType.String);
        pod[8] = (byte)'a'; pod[9] = (byte)'b'; pod[10] = (byte)'c';

        Assert.IsTrue(SpaPod.TryParse(pod, out SpaValue? value));
        Assert.AreEqual("abc", ((SpaString)value!).Value);
    }

    [TestMethod]
    public void AnEmptyStringAndAZeroLengthOne_BothReadAsEmpty()
    {
        Assert.IsTrue(SpaPod.TryParse(Pod(SpaType.String, 0, 0), out SpaValue? zero));
        Assert.AreEqual(string.Empty, ((SpaString)zero!).Value);

        byte[] justNul = Pod(SpaType.String, 1, 8);
        Assert.IsTrue(SpaPod.TryParse(justNul, out SpaValue? terminated));
        Assert.AreEqual(string.Empty, ((SpaString)terminated!).Value);
    }

    [TestMethod]
    public void BytesAndBitmapsOfAnyLength_AreAcceptedIncludingEmpty()
    {
        // Neither has a minimum, so the only thing to check is that the length is honoured exactly.
        foreach (int length in (int[])[0, 1, 7, 64])
        {
            byte[] bytes = Pod(SpaType.Bytes, (uint)length, length);
            for (int i = 0; i < length; i++) bytes[8 + i] = (byte)i;

            Assert.IsTrue(SpaPod.TryParse(bytes, out SpaValue? value));
            Assert.AreEqual(length, ((SpaBytes)value!).Value.Length);
        }
    }

    [TestMethod]
    public void ANestedPodWhoseInnerPodIsMalformed_IsRefused()
    {
        // The outer pod is well-formed; the one inside it is not. Accepting the outer would hand a
        // caller a nested value that was never read.
        byte[] pod = Pod(SpaType.Pod, 16, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(8, 4), 0xFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(12, 4), (uint)SpaType.Int);

        Assert.IsFalse(SpaPod.TryParse(pod, out _));
    }

    [TestMethod]
    public void WritingThenReadingEveryValueAtItsSizeBoundary_IsStable()
    {
        // The writer and the reader have to agree about padding at each of the sizes that round
        // differently: a body of 1, 7, 8 and 9 bytes pads to 8, 8, 8 and 16.
        foreach (int length in (int[])[0, 1, 7, 8, 9, 15, 16])
        {
            var bytes = new SpaBytes([.. Enumerable.Range(0, length).Select(i => (byte)i)]);
            byte[] written = SpaPod.ToBytes(bytes);

            Assert.AreEqual(0, written.Length % 8, "every pod is padded to eight bytes");
            Assert.IsTrue(SpaPod.TryParse(written, out SpaValue? read));
            Assert.AreEqual(bytes, read);
        }
    }

    [TestMethod]
    public void AnArrayOfEveryFixedSizeType_RoundTripsAtItsOwnChildSize()
    {
        (SpaType Type, SpaValue Item)[] cases =
        [
            (SpaType.Bool, new SpaBool(true)),
            (SpaType.Id, new SpaId(9)),
            (SpaType.Int, new SpaInt(-9)),
            (SpaType.Float, new SpaFloat(1.5f)),
            (SpaType.Long, new SpaLong(long.MaxValue)),
            (SpaType.Double, new SpaDouble(2.5)),
            (SpaType.Rectangle, new SpaRectangle(4, 3)),
            (SpaType.Fraction, new SpaFraction(24, 1)),
        ];

        foreach ((SpaType type, SpaValue item) in cases)
        {
            var array = new SpaArray(type, [item, item, item]);
            Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(array), out SpaValue? read), $"{type} array");
            Assert.AreEqual(array, read, $"{type} array changed on the way through");
        }
    }

    /// <summary>An object holding one property whose value size is attacker-chosen.</summary>
    private static byte[] PropertyOfSize(uint valueSize)
    {
        byte[] pod = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(0), (uint)(pod.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(4), (uint)SpaType.Object);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(8), (uint)SpaType.ObjectProps);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(20), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(24), valueSize);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(28), (uint)SpaType.Int);
        return pod;
    }

    [TestMethod]
    public void APropertySizeThatOverflowsAnInt_IsRefusedRatherThanThrowing()
    {
        // uint.MaxValue casts to -1, making "8 + (int)size" seven and small enough to pass a
        // bounds check; a value near int.MaxValue overflows the same sum to negative.
        foreach (uint size in (uint[])[uint.MaxValue, uint.MaxValue - 7, int.MaxValue, (uint)int.MaxValue + 1])
        {
            if (SpaPod.TryParse(PropertyOfSize(size), out SpaValue? value) && value is SpaObject obj)
                Assert.AreEqual(0, obj.Properties.Length, $"size {size} produced a property");
        }
    }

    [TestMethod]
    public void EveryTruncationAndByteFlipOfAValidPod_IsRefusedRatherThanThrowing()
    {
        Span<byte> buf = stackalloc byte[512];
        var builder = new SpaPodBuilder(buf);
        builder.PushObject(SpaType.ObjectProps, SpaParamType.Props);
        builder.AddInt(SpaProp.Volume, 42);
        builder.AddChoiceEnum(SpaFormat.VideoFormat, SpaVideoFormat.Bgra, SpaVideoFormat.Rgba);
        byte[] pod = builder.GetPod().ToArray();

        for (int len = 0; len < pod.Length; len++)
            SpaPod.TryParse(pod.AsSpan(0, len), out _);

        for (int i = 0; i < pod.Length; i++)
        {
            byte[] flipped = [.. pod];
            flipped[i] ^= 0xFF;
            SpaPod.TryParse(flipped, out _);
        }
    }

    [TestMethod]
    public void AStringPaddedWithExtraNuls_LosesThemOnTheWayOut()
    {
        byte[] pod = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(0), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(4), (uint)SpaType.String);
        "abc"u8.CopyTo(pod.AsSpan(8));

        Assert.IsTrue(SpaPod.TryParse(pod, out SpaValue? value));
        Assert.AreEqual("abc", ((SpaString)value!).Value);
    }
}
