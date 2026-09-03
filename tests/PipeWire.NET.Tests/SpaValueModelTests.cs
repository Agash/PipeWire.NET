using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The value model itself: equality, the typed key and id wrappers, and the pod types that the
/// round-trip tests do not reach because no daemon sends them.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SpaValueModelTests
{
    // ------------------------------------------------------------------ structural equality

    [TestMethod]
    public void TwoValuesWithTheSameContents_AreEqualEvenThoughTheirArraysAreNot()
    {
        // The trap this exists for: a record compares ImmutableArray by reference, so two pods with
        // identical contents would be unequal - and comparing a parameter read back against the one
        // written is exactly what a caller does.
        Assert.AreEqual(new SpaBytes([1, 2, 3]), new SpaBytes([1, 2, 3]));
        Assert.AreEqual(new SpaBitmap([9, 8]), new SpaBitmap([9, 8]));
        Assert.AreEqual(
            new SpaArray(SpaType.Int, [new SpaInt(1), new SpaInt(2)]),
            new SpaArray(SpaType.Int, [new SpaInt(1), new SpaInt(2)]));
        Assert.AreEqual(
            new SpaStruct([new SpaInt(1), new SpaString("x")]),
            new SpaStruct([new SpaInt(1), new SpaString("x")]));
        Assert.AreEqual(
            new SpaChoice(SpaChoiceType.Enum, SpaType.Id, [new SpaId(7)]),
            new SpaChoice(SpaChoiceType.Enum, SpaType.Id, [new SpaId(7)]));
        Assert.AreEqual(
            new SpaSequence(1, [new SpaControl(0, 2, new SpaFloat(0.5f))]),
            new SpaSequence(1, [new SpaControl(0, 2, new SpaFloat(0.5f))]));
        Assert.AreEqual(
            new SpaUnknown((SpaType)999, [4, 5]),
            new SpaUnknown((SpaType)999, [4, 5]));
        Assert.AreEqual(
            new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
                [new SpaProperty(1, 0, new SpaFloat(1f))]),
            new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
                [new SpaProperty(1, 0, new SpaFloat(1f))]));
    }

    [TestMethod]
    public void ChildTypeAndItemsMustAgree()
    {
        // The wire format stores these children bare, with one type and one size for all of them,
        // so a mismatch is not something the format can express. Refused where it is written rather
        // than at write time, where the value is truncated or padded to fit and the result is a
        // wrong number rather than an error.
        Assert.ThrowsExactly<ArgumentException>(
            () => new SpaArray(SpaType.Id, [new SpaInt(1)]));
        Assert.ThrowsExactly<ArgumentException>(
            () => new SpaChoice(SpaChoiceType.Enum, SpaType.Long, [new SpaInt(1)]));

        // An empty union declares a type and carries nothing, which is legal and is what a choice
        // with no alternatives parses as.
        _ = new SpaArray(SpaType.Id, []);
        _ = new SpaChoice(SpaChoiceType.None, SpaType.Long, []);
    }

    [TestMethod]
    public void ValuesThatDifferInAnyPart_AreNotEqual()
    {
        Assert.AreNotEqual(new SpaBytes([1, 2]), new SpaBytes([1, 3]));
        Assert.AreNotEqual(new SpaBytes([1, 2]), new SpaBytes([1, 2, 3]));
        Assert.AreNotEqual(new SpaBitmap([1]), new SpaBitmap([2]));

        // Equivalent items, different declared child type: the type is part of what the pod means.
        // Each array carries children of its own declared type, because a union cannot express
        // anything else - see ChildTypeAndItemsMustAgree below.
        Assert.AreNotEqual(
            new SpaArray(SpaType.Int, [new SpaInt(1)]),
            new SpaArray(SpaType.Id, [new SpaId(1)]));

        // Same three values, different kind: the kind is what says how to read the positions, so
        // these two describe different things and must not compare equal.
        Assert.AreNotEqual(
            new SpaChoice(SpaChoiceType.Enum, SpaType.Int, [new SpaInt(1), new SpaInt(0), new SpaInt(9)]),
            new SpaChoice(SpaChoiceType.Range, SpaType.Int, [new SpaInt(1), new SpaInt(0), new SpaInt(9)]));

        Assert.AreNotEqual(
            new SpaObject(SpaType.ObjectProps, SpaParamType.Props, []),
            new SpaObject(SpaType.ObjectProps, SpaParamType.Format, []));

        Assert.AreNotEqual(new SpaUnknown((SpaType)1, [1]), new SpaUnknown((SpaType)2, [1]));
        Assert.AreNotEqual(new SpaSequence(1, []), new SpaSequence(2, []));
    }

    [TestMethod]
    public void EqualValues_HashTheSame_SoTheyWorkAsDictionaryKeys()
    {
        var seen = new Dictionary<SpaValue, string>
        {
            [new SpaArray(SpaType.Float, [new SpaFloat(0.5f), new SpaFloat(0.25f)])] = "volumes",
            [new SpaBytes([7, 7])] = "bytes",
        };

        Assert.AreEqual("volumes",
            seen[new SpaArray(SpaType.Float, [new SpaFloat(0.5f), new SpaFloat(0.25f)])]);
        Assert.AreEqual("bytes", seen[new SpaBytes([7, 7])]);
    }

    [TestMethod]
    public void AnEmptyArrayAndADefaultOne_AreTreatedAsTheSameThing()
    {
        // A parser that produced no items and one that produced an uninitialised array describe the
        // same pod, and a caller should not have to know which it got.
        Assert.AreEqual(new SpaStruct([]), new SpaStruct(default));
        Assert.AreEqual(new SpaStruct([]).GetHashCode(), new SpaStruct(default).GetHashCode());
        Assert.AreEqual(new SpaBytes([]), new SpaBytes(default));
    }

    [TestMethod]
    public void ComparingAgainstNullOrAnotherType_IsFalseRatherThanThrowing()
    {
        var bytes = new SpaBytes([1]);

        Assert.IsFalse(bytes.Equals(null));
        Assert.AreNotEqual<SpaValue>(bytes, new SpaBitmap([1]));
        Assert.AreNotEqual<SpaValue>(new SpaInt(1), new SpaId(1));
    }

    // ------------------------------------------------------------------ the pod types nothing sends

    [TestMethod]
    public void TheRarerPodTypes_StillRoundTrip()
    {
        // Nothing in an audio session sends these, so the daemon tests never reach them - but a pod
        // is a pod, and a caller handed one has to be able to read it.
        SpaValue[] values =
        [
            new SpaBitmap([0b1010_1010, 0b0101_0101]),
            new SpaSequence(1, [new SpaControl(0, 2, new SpaFloat(0.5f)),
                                new SpaControl(480, 2, new SpaFloat(1.0f))]),
            new SpaNestedPod(new SpaInt(42)),
            new SpaStruct([new SpaInt(1), new SpaString("two"), new SpaFloat(3f)]),
        ];

        foreach (SpaValue value in values)
        {
            Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(value), out SpaValue? read),
                $"{value.GetType().Name} did not parse back");
            Assert.AreEqual(value, read, $"{value.GetType().Name} changed on the way through");
        }
    }

    [TestMethod]
    public void APointerRoundTripsItsTypeAndAddress_WithoutEverBeingDereferenced()
    {
        // Meaningful only inside the process that wrote it. The library carries it and does not
        // follow it, which is the only safe thing to do with a pointer off the wire.
        var pointer = new SpaPointer(SpaType.PointerBuffer, 0xDEADBEEF);

        Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(pointer), out SpaValue? read));
        var parsed = (SpaPointer)read!;
        Assert.AreEqual(SpaType.PointerBuffer, parsed.PointerType);
        Assert.AreEqual(0xDEADBEEFul, parsed.Address);
    }

    [TestMethod]
    public void AnEmptyChoice_HasNoDefaultRatherThanThrowing()
    {
        var empty = new SpaChoice(SpaChoiceType.Enum, SpaType.Int, []);
        Assert.IsNull(empty.Default);

        var populated = new SpaChoice(SpaChoiceType.Step, SpaType.Int,
            [new SpaInt(5), new SpaInt(0), new SpaInt(10), new SpaInt(1)]);
        Assert.AreEqual(new SpaInt(5), populated.Default, "the first entry is the default, whatever the kind");
    }

    [TestMethod]
    public void EveryValueReportsItsOwnPodType()
    {
        Assert.AreEqual(SpaType.None, SpaNone.Instance.Type);
        Assert.AreEqual(SpaType.Bool, new SpaBool(true).Type);
        Assert.AreEqual(SpaType.Bitmap, new SpaBitmap([]).Type);
        Assert.AreEqual(SpaType.Sequence, new SpaSequence(0, []).Type);
        Assert.AreEqual(SpaType.Pointer, new SpaPointer(SpaType.None, 0).Type);
        Assert.AreEqual(SpaType.Pod, new SpaNestedPod(new SpaInt(0)).Type);
        Assert.AreEqual(SpaType.Fd, new SpaFd(3).Type);

        // An unknown pod reports the type it actually carried, not a placeholder - that is what lets
        // it be written back unchanged.
        Assert.AreEqual((SpaType)0x4321, new SpaUnknown((SpaType)0x4321, []).Type);
    }

    // ------------------------------------------------------------------ the typed key and id wrappers

    [TestMethod]
    public void AKeyConvertsFromEveryEnumThatCanNameOne()
    {
        // The point of SpaKey: which enum a property key comes from depends on the object it is in,
        // so no single enum can type the parameter. Each of these must reach the same numeric key.
        AssertKey(SpaFormat.VideoSize, (uint)SpaFormat.VideoSize);
        AssertKey(SpaProp.ChannelVolumes, (uint)SpaProp.ChannelVolumes);
        AssertKey(SpaPropInfo.Type, (uint)SpaPropInfo.Type);
        AssertKey(SpaParamBuffers.Size, (uint)SpaParamBuffers.Size);
        AssertKey(SpaParamMeta.Type, (uint)SpaParamMeta.Type);
        AssertKey(SpaParamRoute.Index, (uint)SpaParamRoute.Index);
        AssertKey(SpaParamProfile.Name, (uint)SpaParamProfile.Name);
        AssertKey(SpaParamLatency.MinRate, (uint)SpaParamLatency.MinRate);

        static void AssertKey(SpaKey key, uint expected) => Assert.AreEqual(expected, key.Value);
    }

    [TestMethod]
    public void KeysCompareAgainstTheEnumTheyCameFrom_WithoutACast()
    {
        // The reader hands back a SpaKey; comparing it to the enum a caller means is the whole
        // reason it converts implicitly in both directions.
        SpaKey key = SpaFormat.VideoFormat;

        Assert.IsTrue(key == SpaFormat.VideoFormat);
        Assert.IsFalse(key == SpaFormat.VideoSize);
        Assert.AreEqual((uint)SpaFormat.VideoFormat, (uint)key);
        Assert.AreEqual(SpaFormat.VideoFormat, key.As<SpaFormat>());
    }

    [TestMethod]
    public void AnIdConvertsFromEveryEnumThatCanNameOne_AndReadsBackAsThatEnum()
    {
        SpaIdValue format = SpaVideoFormat.Nv12;
        Assert.AreEqual((uint)SpaVideoFormat.Nv12, format.Value);
        Assert.AreEqual(SpaVideoFormat.Nv12, format.As<SpaVideoFormat>());

        SpaIdValue audio = SpaAudioFormat.S16Le;
        Assert.AreEqual(SpaAudioFormat.S16Le, audio.As<SpaAudioFormat>());

        SpaIdValue media = SpaMediaType.Video;
        Assert.AreEqual(SpaMediaType.Video, media.As<SpaMediaType>());

        SpaIdValue data = SpaDataType.DmaBuf;
        Assert.AreEqual(SpaDataType.DmaBuf, data.As<SpaDataType>());

        SpaIdValue direction = SpaDirection.Output;
        Assert.AreEqual(SpaDirection.Output, direction.As<SpaDirection>());
    }

    [TestMethod]
    public void AnIdTheEnumDoesNotHave_ReadsBackAsSomethingThatMatchesNothing()
    {
        // A newer daemon can send a member this version predates. That must not throw - it is a
        // newer PipeWire, not a corrupt one - and it must not silently equal a real member either.
        SpaIdValue unknown = SpaIdValue.FromRaw(0x7FFF_0001);

        SpaVideoFormat read = unknown.As<SpaVideoFormat>();
        Assert.AreEqual(0x7FFF_0001u, (uint)read);
        Assert.AreNotEqual(SpaVideoFormat.Nv12, read);
        Assert.IsFalse(Enum.IsDefined(read), "it must not land on a real member by accident");
    }

    [TestMethod]
    public void RawKeysAndIds_AreCarriedThroughUnchanged()
    {
        Assert.AreEqual(1234u, SpaKey.FromRaw(1234).Value);
        Assert.AreEqual(5678u, SpaIdValue.FromRaw(5678).Value);
        Assert.AreEqual("1234", SpaKey.FromRaw(1234).ToString());
        Assert.AreEqual("5678", SpaIdValue.FromRaw(5678).ToString());
    }

    [TestMethod]
    public void FindingAPropertyIsByKeyAlone_WhicheverEnumSpelledIt()
    {
        var props = new SpaObject(SpaType.ObjectProps, SpaParamType.Props,
        [
            new SpaProperty((uint)SpaProp.Volume, 0, new SpaFloat(0.5f)),
            new SpaProperty((uint)SpaProp.Mute, SpaPodPropFlag.Readonly, new SpaBool(false)),
        ]);

        Assert.AreEqual(new SpaFloat(0.5f), props[SpaProp.Volume]);
        Assert.AreEqual(SpaPodPropFlag.Readonly, props.Find(SpaProp.Mute)!.Flags);
        Assert.IsNull(props[SpaProp.LatencyOffsetNsec]);
    }

    // ------------------------------------------------------------------ choice arity

    [TestMethod]
    public void ARangeChoiceWithoutItsThreeValues_IsRefused()
    {
        // The kind is what says how to read the positions. A Range holding two entries is missing
        // either its minimum or its maximum and there is nothing in the pod to say which, so the
        // daemon reads whatever is at position 1 as the minimum.
        ArgumentException e = Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new SpaChoice(SpaChoiceType.Range, SpaType.Int, [new SpaInt(1), new SpaInt(0)]));

        StringAssert.Contains(e.Message, "exactly 3");
    }

    [TestMethod]
    public void AStepChoiceWithoutItsFourValues_IsRefused() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new SpaChoice(SpaChoiceType.Step, SpaType.Int,
                [new SpaInt(1), new SpaInt(0), new SpaInt(9)]));

    [TestMethod]
    public void ANoneChoiceCarryingMoreThanOneValue_IsRefused() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new SpaChoice(SpaChoiceType.None, SpaType.Int, [new SpaInt(1), new SpaInt(2)]));

    [TestMethod]
    public void TheChoicesWhoseArityIsNotFixed_TakeAnyCount()
    {
        // The other side of the check: Enum and Flags are lists, so a count rule would refuse pods
        // the daemon sends. An empty choice is refused by the writers, not here.
        _ = new SpaChoice(SpaChoiceType.Enum, SpaType.Int, [new SpaInt(1)]);
        _ = new SpaChoice(SpaChoiceType.Enum, SpaType.Int, [new SpaInt(1), new SpaInt(2), new SpaInt(3)]);
        _ = new SpaChoice(SpaChoiceType.Flags, SpaType.Int, [new SpaInt(1), new SpaInt(2)]);
        _ = new SpaChoice(SpaChoiceType.None, SpaType.Int, [new SpaInt(1)]);
        _ = new SpaChoice(SpaChoiceType.Range, SpaType.Int, [new SpaInt(5), new SpaInt(0), new SpaInt(9)]);
        _ = new SpaChoice(SpaChoiceType.Step, SpaType.Int,
            [new SpaInt(5), new SpaInt(0), new SpaInt(9), new SpaInt(1)]);
    }
}
