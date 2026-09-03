using System.Buffers.Binary;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>The pod layer refusing malformed input, and failing in the documented way when it does.</summary>
/// <remarks>
/// <para>
/// Two separate contracts are pinned here. The parser's is that malformed bytes come back as
/// <see langword="false"/>: it runs inside native callbacks where an escaping exception ends the
/// process, so "reads a prefix and reports success" and "throws something the caller does not catch"
/// are both failures of the same rule.
/// </para>
/// <para>
/// The builder's is the mirror image. It writes into a caller's buffer, so what it leaves in the
/// padding goes on the wire, and running out of room has to name the builder rather than surface as
/// a span offset from somewhere inside a slice.
/// </para>
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SpaPodHardeningTests
{
    /// <summary>A pod header followed by exactly the body bytes given.</summary>
    private static byte[] Pod(SpaType type, ReadOnlySpan<byte> body)
    {
        byte[] pod = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(0, 4), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(4, 4), (uint)type);
        body.CopyTo(pod.AsSpan(8));
        return pod;
    }

    private static byte[] U32(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] all = new byte[parts.Sum(static p => p.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(all, offset);
            offset += part.Length;
        }

        return all;
    }

    // - Builder -

    [TestMethod]
    public void PaddingWrittenIntoADirtyBuffer_IsZeroedRatherThanLeftAsItWas()
    {
        // The padding is part of the pod that goes to the daemon. A builder that only moves its
        // cursor past it sends whatever the buffer last held, which makes the bytes depend on the
        // caller's allocation history and leaks it.
        Span<byte> buffer = new byte[64];
        buffer.Fill(0xAB);

        var b = new SpaPodBuilder(buffer);
        b.AddInt(1);                                  // 8-byte header + 4-byte body, 4 bytes of pad
        ReadOnlySpan<byte> pod = b.GetPod();

        Assert.AreEqual(16, pod.Length, "an Int pod occupies a header, four bytes and four of pad");
        for (int i = 12; i < 16; i++)
            Assert.AreEqual(0, pod[i], $"pad byte {i} kept the buffer's previous contents");
    }

    [TestMethod]
    public void APodLargerThanItsBuffer_FailsAsTheBuilderRatherThanAsASpanOffset()
    {
        // ArgumentOutOfRangeException from inside a Slice names an offset nobody can act on. The
        // caller's actual problem is the buffer size, so that is what the message has to say.
        InvalidOperationException ex =
            Assert.ThrowsExactly<InvalidOperationException>(WriteTwoLongsIntoRoomForOne);

        Assert.IsTrue(ex.Message.Contains("SpaPodBuilder", StringComparison.Ordinal),
            $"the message must name the builder: {ex.Message}");

        static void WriteTwoLongsIntoRoomForOne()
        {
            var b = new SpaPodBuilder(new byte[16]);
            b.AddLong(1);
            b.AddLong(2);
        }
    }

    [TestMethod]
    public void PopWithNoOpenObject_IsRefusedRatherThanPatchingWhateverPrecedesTheStack()
    {
        // The index goes negative otherwise and a size is back-patched over memory the builder does
        // not own, which is silent corruption rather than a caller error.
        Assert.ThrowsExactly<InvalidOperationException>(PopWithNothingOpen);

        static void PopWithNothingOpen()
        {
            var b = new SpaPodBuilder(new byte[64]);
            b.Pop();
        }
    }

    [TestMethod]
    public void AChoiceWithNothingToChooseFrom_IsRefusedAtTheBuilder()
    {
        // A header claiming alternatives it does not carry. Our own reader rejects it and a peer
        // reading it has nothing to select, so it must not be written in the first place.
        Assert.ThrowsExactly<ArgumentException>(EmptyLongChoice);
        Assert.ThrowsExactly<ArgumentException>(EmptyIdChoice);

        static void EmptyLongChoice()
        {
            var b = new SpaPodBuilder(new byte[64]);
            b.AddChoiceEnumLong(SpaFormat.VideoModifier, []);
        }

        static void EmptyIdChoice()
        {
            var b = new SpaPodBuilder(new byte[64]);
            b.AddChoiceEnum(SpaFormat.VideoFormat, []);
        }
    }

    // - Parser -

    [TestMethod]
    public void AnArrayBodyThatIsNotAWholeNumberOfChildren_IsRefused()
    {
        // Dividing the remainder away parses a prefix and reports success, so bytes the producer
        // described as nothing become bytes nobody looked at.
        byte[] body = Concat(U32(4, (uint)SpaType.Int), U32(1), [0xDE, 0xAD]);
        Assert.IsFalse(SpaPod.TryParse(Pod(SpaType.Array, body), out SpaValue? value));
        Assert.IsNull(value);
    }

    [TestMethod]
    public void AnArrayWhoseChildSizeDisagreesWithItsChildType_IsRefusedBeforeAnythingIsSized()
    {
        // Int is four bytes. A declared child size of one makes the count one per byte, and the
        // builder is sized from that count before the first child fails to parse.
        byte[] body = Concat(U32(1, (uint)SpaType.Int), new byte[64]);
        Assert.IsFalse(SpaPod.TryParse(Pod(SpaType.Array, body), out SpaValue? value));
        Assert.IsNull(value);
    }

    [TestMethod]
    public void AnArrayThatTilesExactly_IsStillAccepted()
    {
        // The other side of the boundary, so the two checks above cannot pass by refusing all arrays.
        byte[] body = Concat(U32(4, (uint)SpaType.Int), U32(7, 8, 9));
        Assert.IsTrue(SpaPod.TryParse(Pod(SpaType.Array, body), out SpaValue? value));
        var array = (SpaArray)value!;
        Assert.AreEqual(3, array.Items.Length);
    }

    [TestMethod]
    public void AStructOrSequenceEndingMidField_IsRefusedTheWayAnObjectAlreadyWas()
    {
        // The walk stops when fewer than a header remains, so leftovers mean a body that ended in
        // the middle of something. Accepting it dropped the tail and looked like a shorter message.
        byte[] structBody = Concat(Pod(SpaType.Int, U32(1)), [1, 2, 3]);
        Assert.IsFalse(SpaPod.TryParse(Pod(SpaType.Struct, structBody), out SpaValue? _));

        byte[] sequenceBody = Concat(U32(0, 0), U32(0, 0), Pod(SpaType.Int, U32(1)), [1, 2, 3]);
        Assert.IsFalse(SpaPod.TryParse(Pod(SpaType.Sequence, sequenceBody), out SpaValue? _));
    }

    [TestMethod]
    public void AWellFormedStructAndSequence_AreStillAccepted()
    {
        byte[] structBody = Pod(SpaType.Int, U32(1));
        Assert.IsTrue(SpaPod.TryParse(Pod(SpaType.Struct, structBody), out SpaValue? parsedStruct));
        Assert.AreEqual(1, ((SpaStruct)parsedStruct!).Fields.Length);

        byte[] sequenceBody = Concat(U32(0, 0), U32(0, 0), Pod(SpaType.Int, U32(1)));
        Assert.IsTrue(SpaPod.TryParse(Pod(SpaType.Sequence, sequenceBody), out SpaValue? parsedSequence));
        Assert.AreEqual(1, ((SpaSequence)parsedSequence!).Controls.Length);
    }

    // - Reader -

    [TestMethod]
    public void AShortChildInAChoice_ThrowsTheTypeTheCallersCatch()
    {
        // This is the crash. A choice declaring one byte per child hands the unwrapped reader a
        // one-byte body; the typed read then slices four out of it and the exception that escapes
        // is ArgumentOutOfRangeException. Format parsing runs on the loop thread and catches only
        // InvalidOperationException, so the wrong type there ends the process rather than the frame.
        byte[] body = Concat(U32((uint)SpaChoiceType.Enum, 0, 1, (uint)SpaType.Id), [0x01]);
        Assert.ThrowsExactly<InvalidOperationException>(() => ReadIdFromShortChoice(body));

        static void ReadIdFromShortChoice(byte[] choicePod)
        {
            var reader = new SpaPodReader(Pod(SpaType.Choice, choicePod));
            Assert.IsTrue(reader.TryUnwrapChoice(out SpaPodReader inner));
            _ = inner.ReadId();
        }
    }

    [TestMethod]
    public void AModifierChoiceThatIsARange_IsNotReadAsASetOfModifiers()
    {
        // Range is { default, min, max }. Read as an Enum it reports a minimum and a maximum as two
        // further modifiers on offer, which starts a fixation round against values that are bounds.
        byte[] values = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(values.AsSpan(0, 8), 0x100);
        BinaryPrimitives.WriteInt64LittleEndian(values.AsSpan(8, 8), 0);
        BinaryPrimitives.WriteInt64LittleEndian(values.AsSpan(16, 8), 0xFFFF);

        byte[] body = Concat(U32((uint)SpaChoiceType.Range, 0, 8, (uint)SpaType.Long), values);
        var reader = new SpaPodReader(Pod(SpaType.Choice, body));

        Assert.IsFalse(reader.TryReadModifier(out _, out _));
        Assert.AreEqual(0, reader.Position, "a declined read must leave the position where it was");
    }

    [TestMethod]
    public void AModifierChoiceWhoseValuesDoNotFillTheBody_IsRefused()
    {
        byte[] body = Concat(U32((uint)SpaChoiceType.Enum, 0, 8, (uint)SpaType.Long), new byte[12]);
        var reader = new SpaPodReader(Pod(SpaType.Choice, body));

        Assert.IsFalse(reader.TryReadModifier(out _, out _));
    }

    [TestMethod]
    public void AModifierEnumChoice_IsStillRead()
    {
        byte[] values = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(values.AsSpan(0, 8), 0x123);
        BinaryPrimitives.WriteInt64LittleEndian(values.AsSpan(8, 8), 0x123);

        byte[] body = Concat(U32((uint)SpaChoiceType.Enum, 0, 8, (uint)SpaType.Long), values);
        var reader = new SpaPodReader(Pod(SpaType.Choice, body));

        Assert.IsTrue(reader.TryReadModifier(out long first, out int count));
        Assert.AreEqual(0x123, first);
        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public void PropertiesAreBoundedByTheObject_NotByTheBuffer()
    {
        // An object nested in a struct is followed by its siblings. Walking to the end of the buffer
        // reads those as further properties of this object, with keys and types taken from whatever
        // the sibling happens to be.
        byte[] objectBody = Concat(
            U32((uint)SpaType.ObjectFormat, (uint)SpaParamType.Format),
            U32((uint)SpaFormat.VideoFormat, 0),
            Pod(SpaType.Id, U32(1)));

        byte[] sibling = Pod(SpaType.Int, U32(0xDEAD));
        byte[] buffer = Concat(Pod(SpaType.Object, objectBody), sibling);

        var reader = new SpaPodReader(buffer);
        Assert.IsTrue(reader.EnterObject(out _, out _, out _));

        int properties = 0;
        while (reader.TryReadProperty(out _, out _))
            properties++;

        Assert.AreEqual(1, properties, "the sibling pod was read as a property of the object");
    }

    // - Value model -

    [TestMethod]
    public void AStringCarryingANul_IsRefusedWhereItIsConstructed()
    {
        // The wire form is NUL-terminated, so such a string does not survive its own round trip: it
        // comes back cut at the NUL and every reader past the daemon sees something else.
        Assert.ThrowsExactly<ArgumentException>(() => _ = new SpaString("before\0after"));
        Assert.ThrowsExactly<ArgumentException>(AddValueWithANul);

        static void AddValueWithANul()
        {
            Span<byte> scratch = new byte[128];
            Span<spa_dict_item> items = new spa_dict_item[4];
            var b = new SpaDictBuilder(scratch, items);
            b.Add("key", "before\0after");
        }
    }

    [TestMethod]
    public void FloatsCompareByTheirBits_BecauseThatIsWhatTheWireCarries()
    {
        // Reading a parameter back and comparing it against what was written is the one thing this
        // equality is for, and IEEE rules give the wrong answer at both ends: two NaNs differ, and
        // the two zeroes do not.
        Assert.AreEqual(new SpaFloat(float.NaN), new SpaFloat(float.NaN));
        Assert.AreNotEqual(new SpaFloat(0f), new SpaFloat(-0f));
        Assert.AreEqual(new SpaDouble(double.NaN), new SpaDouble(double.NaN));
        Assert.AreNotEqual(new SpaDouble(0d), new SpaDouble(-0d));
    }

    [TestMethod]
    public void AnIdReadAsAWronglySizedEnum_NamesBothTypesRatherThanTheReinterpret()
    {
        // The id side reached BitCast and threw a NotSupportedException naming neither type.
        Assert.ThrowsExactly<ArgumentException>(() => _ = SpaIdValue.FromRaw(1).As<ByteWide>());
    }

    private enum ByteWide : byte { Zero }
}
