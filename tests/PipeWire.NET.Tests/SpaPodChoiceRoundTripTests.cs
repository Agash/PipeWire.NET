using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Every choice the builder can write, read back through the parser.
/// </summary>
/// <remarks>
/// The builder and the parser are two independent encodings of the same wire layout. A round trip
/// catches the class where they disagree without anybody having to predict which field will be
/// wrong next. The daemon is not involved; this is the encoding, not the protocol.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class SpaPodChoiceRoundTripTests
{
    private delegate void BuildOne(ref SpaPodBuilder builder);

    /// <summary>Builds a one-property object and returns that property as the parser sees it.</summary>
    private static SpaProperty RoundTrip(BuildOne build)
    {
        Span<byte> buffer = stackalloc byte[1024];
        var builder = new SpaPodBuilder(buffer);

        builder.PushObject(SpaType.ObjectFormat, SpaParamType.EnumFormat);
        build(ref builder);
        builder.Pop();

        Assert.IsTrue(SpaPod.TryParse(builder.GetPod(), out SpaValue? parsed),
            "the builder wrote a pod the parser rejects");

        var obj = parsed as SpaObject;
        Assert.IsNotNull(obj, "the pod did not read back as an object");
        Assert.AreEqual(1, obj!.Properties.Length, "expected exactly the one property that was written");

        return obj.Properties[0];
    }

    private static SpaChoice AsChoice(SpaProperty property)
    {
        var choice = property.Value as SpaChoice;
        Assert.IsNotNull(choice, $"the property read back as {property.Value.GetType().Name}, not a choice");
        return choice!;
    }

    [TestMethod]
    public void ChoiceEnumOverIds_LeadsWithTheDefaultAndThenEveryValue()
    {
        // Choice(Enum) is {default, alt0, alt1, ...}: the first child is the preferred value and the
        // rest are what may be selected, so the default appears twice. Writing it once leaves a
        // default with no alternatives, which is a negotiation that fails with "no more input
        // formats" rather than an error anybody can read.
        SpaProperty p = RoundTrip((ref SpaPodBuilder b) =>
            b.AddChoiceEnum(SpaFormat.VideoFormat, SpaVideoFormat.Bgra, SpaVideoFormat.Rgba, SpaVideoFormat.Nv12));

        SpaChoice choice = AsChoice(p);
        Assert.AreEqual(SpaChoiceType.Enum, choice.Kind);
        Assert.AreEqual(SpaType.Id, choice.ChildType);

        CollectionAssert.AreEqual(
            new[]
            {
                (uint)SpaVideoFormat.Bgra,
                (uint)SpaVideoFormat.Bgra, (uint)SpaVideoFormat.Rgba, (uint)SpaVideoFormat.Nv12,
            },
            (uint[])[.. choice.Alternatives.Cast<SpaId>().Select(v => v.Value)],
            "the default must lead, and every offered value must follow it in order");
    }

    [TestMethod]
    public void ChoiceEnumOverLongs_LeadsWithTheDefaultAndThenEveryModifier()
    {
        long[] modifiers = [0x0100000000000001L, 0x0100000000000002L, 0L];

        SpaProperty p = RoundTrip((ref SpaPodBuilder b) =>
            b.AddChoiceEnumLong(SpaFormat.VideoModifier, modifiers));

        SpaChoice choice = AsChoice(p);
        Assert.AreEqual(SpaChoiceType.Enum, choice.Kind);
        Assert.AreEqual(SpaType.Long, choice.ChildType);
        CollectionAssert.AreEqual(
            (long[])[modifiers[0], .. modifiers],
            (long[])[.. choice.Alternatives.Cast<SpaLong>().Select(v => v.Value)],
            "a single modifier written once would be a default with nothing to select");
    }

    [TestMethod]
    public void ChoiceEnumOverLongs_CarriesThePropFlagsItWasGiven()
    {
        // DontFixate is how the first negotiation pass says to narrow the set without picking yet.
        // It rides on the property, not the choice, so a round trip is the only thing that checks
        // it survives.
        uint flags = (uint)(SpaPodPropFlag.Mandatory | SpaPodPropFlag.DontFixate);

        SpaProperty p = RoundTrip((ref SpaPodBuilder b) =>
            b.AddChoiceEnumLong(SpaFormat.VideoModifier, [1L, 2L], flags));

        Assert.AreEqual(flags, p.Flags, "the property flags did not survive the round trip");
    }

    [TestMethod]
    public void ChoiceRangeOverInts_ReadsBackAsDefaultMinMax()
    {
        SpaProperty p = RoundTrip((ref SpaPodBuilder b) =>
            b.AddChoiceRangeInt(SpaFormat.VideoFormat, def: 44100, min: 8000, max: 192000));

        SpaChoice choice = AsChoice(p);
        Assert.AreEqual(SpaChoiceType.Range, choice.Kind);
        Assert.AreEqual(SpaType.Int, choice.ChildType);
        CollectionAssert.AreEqual(
            new[] { 44100, 8000, 192000 },
            (int[])[.. choice.Alternatives.Cast<SpaInt>().Select(v => v.Value)],
            "Range is three values in the order default, min, max");
    }

    [TestMethod]
    public void ChoiceFlagsOverInt_CarriesExactlyOneValue()
    {
        // The header documents Flags as carrying its flags in the first value, singular, unlike
        // Range and Enum.
        SpaProperty p = RoundTrip((ref SpaPodBuilder b) =>
            b.AddChoiceFlagsInt(SpaFormat.VideoFormat, 0b1011));

        SpaChoice choice = AsChoice(p);
        Assert.AreEqual(SpaChoiceType.Flags, choice.Kind);
        Assert.AreEqual(SpaType.Int, choice.ChildType);
        Assert.AreEqual(1, choice.Alternatives.Length, "Flags carries one value, not a default and a mask");
        Assert.AreEqual(0b1011, ((SpaInt)choice.Alternatives[0]).Value);
    }

    [TestMethod]
    public void ChoiceRangeOverRectangles_ReadsBackAsDefaultMinMax()
    {
        SpaProperty p = RoundTrip((ref SpaPodBuilder b) =>
            b.AddChoiceRangeRectangle(SpaFormat.VideoSize, 1920, 1080, 320, 240, 3840, 2160));

        SpaChoice choice = AsChoice(p);
        Assert.AreEqual(SpaChoiceType.Range, choice.Kind);
        Assert.AreEqual(SpaType.Rectangle, choice.ChildType);

        ImmutableArray<SpaRectangle> r = [.. choice.Alternatives.Cast<SpaRectangle>()];
        Assert.AreEqual(3, r.Length);
        Assert.AreEqual((1920u, 1080u), (r[0].Width, r[0].Height));
        Assert.AreEqual((320u, 240u), (r[1].Width, r[1].Height));
        Assert.AreEqual((3840u, 2160u), (r[2].Width, r[2].Height));
    }

    [TestMethod]
    public void ChoiceRangeOverFractions_ReadsBackAsDefaultMinMax()
    {
        SpaProperty p = RoundTrip((ref SpaPodBuilder b) =>
            b.AddChoiceRangeFraction(SpaFormat.VideoFramerate, 30, 1, 1, 1, 240, 1));

        SpaChoice choice = AsChoice(p);
        Assert.AreEqual(SpaChoiceType.Range, choice.Kind);
        Assert.AreEqual(SpaType.Fraction, choice.ChildType);

        ImmutableArray<SpaFraction> f = [.. choice.Alternatives.Cast<SpaFraction>()];
        Assert.AreEqual(3, f.Length);
        Assert.AreEqual((30u, 1u), (f[0].Numerator, f[0].Denominator));
        Assert.AreEqual((1u, 1u), (f[1].Numerator, f[1].Denominator));
        Assert.AreEqual((240u, 1u), (f[2].Numerator, f[2].Denominator));
    }

    [TestMethod]
    public void AnyNumberOfModifiers_SurvivesTheRoundTrip()
    {
        // The count goes in the header and the values follow it. An off-by-one there is invisible at
        // one or two values and corrupts everything after the choice at larger counts, so the sizes
        // are swept rather than sampled.
        var random = new Random(20260902);

        for (int count = 1; count <= 40; count++)
        {
            long[] values = [.. Enumerable.Range(0, count).Select(_ => (long)random.Next())];

            SpaProperty p = RoundTrip((ref SpaPodBuilder b) =>
                b.AddChoiceEnumLong(SpaFormat.VideoModifier, values));

            CollectionAssert.AreEqual(
                (long[])[values[0], .. values],
                (long[])[.. AsChoice(p).Alternatives.Cast<SpaLong>().Select(v => v.Value)],
                $"a choice of {count} values did not survive");
        }
    }

    [TestMethod]
    public void AChoicePropertyAmongOthers_LeavesTheOthersReadable()
    {
        // A choice is variable-length in the middle of a run of fixed-length properties. If its size
        // is written wrong the parser resumes mid-pod, and what breaks is the property after it
        // rather than the choice itself.
        Span<byte> buffer = stackalloc byte[1024];
        var builder = new SpaPodBuilder(buffer);

        builder.PushObject(SpaType.ObjectFormat, SpaParamType.EnumFormat);
        builder.AddId(SpaFormat.MediaType, SpaMediaType.Video);
        builder.AddChoiceEnum(SpaFormat.VideoFormat, SpaVideoFormat.Bgra, SpaVideoFormat.Nv12);
        builder.AddRectangle(SpaFormat.VideoSize, 1920, 1080);
        builder.AddChoiceRangeFraction(SpaFormat.VideoFramerate, 30, 1, 1, 1, 240, 1);
        builder.AddId(SpaFormat.MediaSubtype, SpaMediaSubtype.Raw);
        builder.Pop();

        Assert.IsTrue(SpaPod.TryParse(builder.GetPod(), out SpaValue? parsed));
        var obj = (SpaObject)parsed!;

        Assert.AreEqual(5, obj.Properties.Length, "a property was lost, so a choice reported the wrong size");

        var size = obj.Properties[2].Value as SpaRectangle;
        Assert.IsNotNull(size, "the property after a choice did not read back as itself");
        Assert.AreEqual((1920u, 1080u), (size!.Width, size.Height));

        var subtype = obj.Properties[4].Value as SpaId;
        Assert.IsNotNull(subtype, "the property after the second choice did not read back as itself");
        Assert.AreEqual((uint)SpaMediaSubtype.Raw, subtype!.Value);
    }
}
