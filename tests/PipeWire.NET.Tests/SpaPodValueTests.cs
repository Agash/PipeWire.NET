using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The public POD tree: parsing has to be total against anything a daemon might send, and writing
/// has to produce bytes the daemon will accept back.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SpaPodValueTests
{
    private static SpaObject Props(params SpaProperty[] properties) =>
        new(SpaType.ObjectProps, SpaParamType.Props, [.. properties]);

    [TestMethod]
    public void EveryPrimitive_SurvivesAWriteAndAReadBack()
    {
        // Round-tripping is the only check that matters for a wire format: a writer and a reader
        // that agree on a wrong layout would pass any test written against either alone, so the
        // sizes below are asserted separately in TheEncodedSizes_MatchTheWireFormat.
        SpaValue[] values =
        [
            SpaNone.Instance,
            new SpaBool(true),
            new SpaId(42),
            new SpaInt(-7),
            new SpaLong(long.MinValue),
            new SpaFloat(0.75f),
            new SpaDouble(double.Epsilon),
            new SpaString("hello"),
            new SpaBytes([1, 2, 3]),
            new SpaRectangle(1920, 1080),
            new SpaFraction(30000, 1001),
            new SpaFd(9),
        ];

        foreach (SpaValue value in values)
        {
            byte[] bytes = SpaPod.ToBytes(value);
            Assert.IsTrue(SpaPod.TryParse(bytes, out SpaValue? read), $"{value} did not parse back");
            Assert.AreEqual(value, read, $"{value} changed on the way through");
        }
    }

    [TestMethod]
    public void AnObjectOfPropertiesRoundTrips_KeepingOrderAndFlags()
    {
        SpaObject original = Props(
            new SpaProperty((uint)SpaProp.Volume, 0, new SpaFloat(0.5f)),
            new SpaProperty((uint)SpaProp.Mute, SpaPodPropFlag.Mandatory, new SpaBool(true)),
            new SpaProperty((uint)SpaProp.ChannelVolumes, 0,
                new SpaArray(SpaType.Float, [new SpaFloat(0.25f), new SpaFloat(0.75f)])));

        Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(original), out SpaValue? read));
        var parsed = (SpaObject)read!;

        Assert.AreEqual(SpaType.ObjectProps, parsed.ObjectType);
        Assert.AreEqual(SpaParamType.Props, parsed.ObjectId);
        Assert.AreEqual(3, parsed.Properties.Length, "properties must not be reordered or lost");
        Assert.AreEqual(SpaPodPropFlag.Mandatory, parsed.Find((uint)SpaProp.Mute)!.Flags);
        Assert.AreEqual(new SpaFloat(0.5f), parsed[(uint)SpaProp.Volume]);

        var channels = (SpaArray)parsed[(uint)SpaProp.ChannelVolumes]!;
        Assert.AreEqual(SpaType.Float, channels.ChildType);
        CollectionAssert.AreEqual(
            new[] { 0.25f, 0.75f },
            channels.Items.Cast<SpaFloat>().Select(f => f.Value).ToArray());
    }

    [TestMethod]
    public void AChoiceRoundTrips_AndItsFirstAlternativeIsTheDefault()
    {
        var choice = new SpaChoice(SpaChoiceType.Range, SpaType.Int,
            [new SpaInt(48000), new SpaInt(8000), new SpaInt(192000)]);

        Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(choice), out SpaValue? read));
        var parsed = (SpaChoice)read!;

        Assert.AreEqual(SpaChoiceType.Range, parsed.Kind);
        Assert.AreEqual(new SpaInt(48000), parsed.Default,
            "whatever the kind, the first alternative is the default or current value");
        Assert.AreEqual(3, parsed.Alternatives.Length);
    }

    [TestMethod]
    public void NestedObjectsRoundTrip_WhichIsWhatARouteNeeds()
    {
        // A device route carries its volume as a Props object nested inside the Route object, so
        // nesting is not a curiosity here - it is how the hardware mixer is written.
        var route = new SpaObject(SpaType.ObjectParamRoute, SpaParamType.Route,
        [
            new SpaProperty((uint)SpaParamRoute.Index, 0, new SpaInt(3)),
            new SpaProperty((uint)SpaParamRoute.Device, 0, new SpaInt(1)),
            new SpaProperty((uint)SpaParamRoute.Props, 0,
                Props(new SpaProperty((uint)SpaProp.Mute, 0, new SpaBool(false)))),
        ]);

        Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(route), out SpaValue? read));
        var parsed = (SpaObject)read!;
        var props = (SpaObject)parsed[(uint)SpaParamRoute.Props]!;

        Assert.AreEqual(new SpaBool(false), props[(uint)SpaProp.Mute]);
    }

    [TestMethod]
    public void TheEncodedSizes_MatchTheWireFormat()
    {
        // Every pod is an 8-byte header plus a body padded to 8. Getting this wrong is not visible
        // in a round-trip, because both ends would be wrong together.
        Assert.AreEqual(8, SpaPod.GetByteCount(SpaNone.Instance), "a none has no body");
        Assert.AreEqual(16, SpaPod.GetByteCount(new SpaInt(1)), "4 bytes of body, padded to 8");
        Assert.AreEqual(16, SpaPod.GetByteCount(new SpaLong(1)));
        Assert.AreEqual(16, SpaPod.GetByteCount(new SpaRectangle(1, 2)));
        // "hello" plus its NUL is 6, padded to 8.
        Assert.AreEqual(16, SpaPod.GetByteCount(new SpaString("hello")));
    }

    [TestMethod]
    public void AStringLosesNothing_IncludingWhenItIsEmptyOrNotAscii()
    {
        foreach (string text in (string[])["", "a", "ünïcödé", new string('x', 100)])
        {
            Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(new SpaString(text)), out SpaValue? read));
            Assert.AreEqual(text, ((SpaString)read!).Value);
        }
    }

    // ------------------------------------------------------------------ hostile input

    [TestMethod]
    public void APodDeclaringMoreBodyThanTheBufferHolds_IsRefused()
    {
        // The size field comes from the daemon. Trusting it is how a parser reads past its buffer.
        byte[] pod = new byte[16];
        BitConverter.TryWriteBytes(pod.AsSpan(0, 4), 4096u);
        BitConverter.TryWriteBytes(pod.AsSpan(4, 4), (uint)SpaType.Int);

        Assert.IsFalse(SpaPod.TryParse(pod, out SpaValue? value));
        Assert.IsNull(value);
    }

    [TestMethod]
    public void ATruncatedHeader_IsRefusedRatherThanRead()
    {
        foreach (int length in (int[])[0, 1, 7])
            Assert.IsFalse(SpaPod.TryParse(new byte[length], out _), $"{length} bytes is not a pod");
    }

    [TestMethod]
    public void AnArrayWhoseChildSizeIsZero_DoesNotLoopForever()
    {
        // A zero child size with a non-empty body describes infinitely many children. The parser has
        // to refuse it; dividing by it or looping on it would hang the loop thread.
        byte[] pod = new byte[8 + 8 + 8];
        BitConverter.TryWriteBytes(pod.AsSpan(0, 4), 16u);
        BitConverter.TryWriteBytes(pod.AsSpan(4, 4), (uint)SpaType.Array);
        BitConverter.TryWriteBytes(pod.AsSpan(8, 4), 0u);                    // child size
        BitConverter.TryWriteBytes(pod.AsSpan(12, 4), (uint)SpaType.Int);    // child type

        Assert.IsFalse(SpaPod.TryParse(pod, out _));
    }

    [TestMethod]
    public void APodNestedBeyondTheDepthLimit_IsRefusedRatherThanOverflowingTheStack()
    {
        // A stack overflow cannot be caught, so the limit is the only defence. Build a chain of
        // structs deeper than it allows.
        SpaValue nested = new SpaInt(1);
        for (int i = 0; i < SpaPod.MaxDepth + 4; i++)
            nested = new SpaStruct([nested]);

        byte[] bytes = SpaPod.ToBytes(nested);
        Assert.IsFalse(SpaPod.TryParse(bytes, out _), "nesting past the limit must be refused");
    }

    [TestMethod]
    public void AnUnknownPodType_IsKeptRatherThanDiscardingTheObjectAroundIt()
    {
        // A newer daemon may send a type this version predates. Refusing it would throw away the
        // properties that ARE understood, which is worse than not decoding one of them.
        byte[] pod = new byte[16];
        BitConverter.TryWriteBytes(pod.AsSpan(0, 4), 4u);
        BitConverter.TryWriteBytes(pod.AsSpan(4, 4), 0xDEADu);
        BitConverter.TryWriteBytes(pod.AsSpan(8, 4), 0x1234u);

        Assert.IsTrue(SpaPod.TryParse(pod, out SpaValue? value));
        var unknown = (SpaUnknown)value!;
        Assert.AreEqual((SpaType)0xDEAD, unknown.UnknownType);
        CollectionAssert.AreEqual(new byte[] { 0x34, 0x12, 0, 0 }, unknown.Body.ToArray(),
            "the bytes are kept so the value can be written back unchanged");

        // And writing it back reproduces the original.
        CollectionAssert.AreEqual(pod, SpaPod.ToBytes(unknown));
    }

    [TestMethod]
    public void AnObjectWithARepeatedKey_ResolvesToTheFirstAsSpaDoes()
    {
        SpaObject duplicated = Props(
            new SpaProperty((uint)SpaProp.Volume, 0, new SpaFloat(0.1f)),
            new SpaProperty((uint)SpaProp.Volume, 0, new SpaFloat(0.9f)));

        Assert.IsTrue(SpaPod.TryParse(SpaPod.ToBytes(duplicated), out SpaValue? read));
        Assert.AreEqual(new SpaFloat(0.1f), ((SpaObject)read!)[(uint)SpaProp.Volume]);
    }

    [TestMethod]
    public void AskingForAKeyAnObjectDoesNotHave_IsNullRatherThanAnError()
    {
        SpaObject props = Props(new SpaProperty((uint)SpaProp.Volume, 0, new SpaFloat(1f)));

        Assert.IsNull(props[(uint)SpaProp.Mute]);
        Assert.IsNull(props.Find((uint)SpaProp.Mute));
    }

    [TestMethod]
    public void TryWrite_RefusesABufferThatIsTooShortInsteadOfOverrunningIt()
    {
        var value = new SpaString("a longer string than fits");
        int needed = SpaPod.GetByteCount(value);

        Assert.IsFalse(SpaPod.TryWrite(value, new byte[needed - 1], out int written));
        Assert.AreEqual(0, written);
        Assert.IsTrue(SpaPod.TryWrite(value, new byte[needed], out written));
        Assert.AreEqual(needed, written);
    }
}
