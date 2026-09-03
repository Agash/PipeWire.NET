using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The enumeration filter matcher, against upstream <c>spa_pod_filter</c> semantics.
/// </summary>
/// <remarks>
/// Pure managed matching over parsed values, so these run anywhere: no daemon, no GPU.
/// </remarks>
[TestClass]
public sealed class SpaPodFilterTests
{
    private static SpaObject Obj(params SpaProperty[] props) =>
        new(SpaType.ObjectProps, SpaParamType.Props, [.. props]);

    private static SpaProperty Prop(uint key, SpaValue value, uint flags = 0) =>
        new(key, flags, value);

    private static SpaChoice Enum(params SpaValue[] alts) =>
        new(SpaChoiceType.Enum, alts[0].Type, [.. alts]);

    private static SpaChoice Range(SpaValue def, SpaValue min, SpaValue max) =>
        new(SpaChoiceType.Range, def.Type, [def, min, max]);

    private static SpaChoice Step(SpaValue def, SpaValue min, SpaValue max, SpaValue step) =>
        new(SpaChoiceType.Step, def.Type, [def, min, max, step]);

    private static SpaChoice Flags(SpaValue value) =>
        new(SpaChoiceType.Flags, value.Type, [value]);

    [TestMethod]
    public void ScalarEquality_MatchesAndMismatches()
    {
        Assert.IsTrue(SpaPodFilter.Matches(Obj(Prop(1, new SpaInt(7))), Obj(Prop(1, new SpaInt(7)))));
        Assert.IsFalse(SpaPodFilter.Matches(Obj(Prop(1, new SpaInt(7))), Obj(Prop(1, new SpaInt(8)))));
        Assert.IsFalse(SpaPodFilter.Matches(
            Obj(Prop(1, new SpaInt(7))), Obj(Prop(1, new SpaString("7")))));
    }

    [TestMethod]
    public void EnumChoices_IntersectOnAnySharedAlternative()
    {
        SpaObject candidate = Obj(Prop(1, Enum(new SpaInt(1), new SpaInt(2))));

        Assert.IsTrue(SpaPodFilter.Matches(candidate, Obj(Prop(1, new SpaInt(2)))));
        Assert.IsTrue(SpaPodFilter.Matches(
            candidate, Obj(Prop(1, Enum(new SpaInt(2), new SpaInt(3))))));
        Assert.IsFalse(SpaPodFilter.Matches(
            candidate, Obj(Prop(1, Enum(new SpaInt(3), new SpaInt(4))))));
    }

    [TestMethod]
    public void RangeFilters_ContainOrReject()
    {
        SpaObject candidate = Obj(Prop(1, new SpaInt(48000)));

        Assert.IsTrue(SpaPodFilter.Matches(
            candidate, Obj(Prop(1, Range(new SpaInt(44100), new SpaInt(8000), new SpaInt(96000))))));
        Assert.IsFalse(SpaPodFilter.Matches(
            candidate, Obj(Prop(1, Range(new SpaInt(44100), new SpaInt(8000), new SpaInt(44100))))));
    }

    [TestMethod]
    public void StepFilters_ApplyUpstreamModuloSemantics()
    {
        // Upstream tests value % step == 0, not (value - min): 48000 % 3000 == 0 passes, and a
        // value inside the range still fails when it is not a multiple of the step.
        SpaObject candidate = Obj(Prop(1, new SpaInt(48000)));

        Assert.IsTrue(SpaPodFilter.Matches(candidate,
            Obj(Prop(1, Step(new SpaInt(0), new SpaInt(0), new SpaInt(96000), new SpaInt(3000))))));
        Assert.IsFalse(SpaPodFilter.Matches(candidate,
            Obj(Prop(1, Step(new SpaInt(0), new SpaInt(0), new SpaInt(96000), new SpaInt(7000))))));

        // A zero step is invalid upstream: no match rather than a divide by zero.
        Assert.IsFalse(SpaPodFilter.Matches(candidate,
            Obj(Prop(1, Step(new SpaInt(0), new SpaInt(0), new SpaInt(96000), new SpaInt(0))))));
    }

    [TestMethod]
    public void RangeAgainstRange_OverlapsOrNot()
    {
        SpaObject candidate = Obj(Prop(1, Range(new SpaInt(0), new SpaInt(100), new SpaInt(200))));

        Assert.IsTrue(SpaPodFilter.Matches(candidate,
            Obj(Prop(1, Range(new SpaInt(0), new SpaInt(150), new SpaInt(300))))));
        Assert.IsFalse(SpaPodFilter.Matches(candidate,
            Obj(Prop(1, Range(new SpaInt(0), new SpaInt(201), new SpaInt(300))))));
    }

    [TestMethod]
    public void FlagsFilters_AndThePair()
    {
        SpaObject candidate = Obj(Prop(1, Flags(new SpaInt(0b1100))));

        Assert.IsTrue(SpaPodFilter.Matches(candidate, Obj(Prop(1, Flags(new SpaInt(0b1010))))));
        Assert.IsTrue(SpaPodFilter.Matches(candidate, Obj(Prop(1, new SpaInt(0b1000)))));
        Assert.IsFalse(SpaPodFilter.Matches(candidate, Obj(Prop(1, Flags(new SpaInt(0b0011))))));

        // Anything else against Flags is ENOTSUP upstream: no match, not an error.
        Assert.IsFalse(SpaPodFilter.Matches(candidate,
            Obj(Prop(1, Range(new SpaInt(0), new SpaInt(0), new SpaInt(9999))))));
    }

    [TestMethod]
    public void NestedObjects_RecursePerProperty()
    {
        SpaObject candidate = Obj(Prop(1, Obj(Prop(2, new SpaInt(5)), Prop(3, new SpaInt(6)))));

        Assert.IsTrue(SpaPodFilter.Matches(candidate, Obj(Prop(1, Obj(Prop(2, new SpaInt(5)))))));
        Assert.IsFalse(SpaPodFilter.Matches(candidate, Obj(Prop(1, Obj(Prop(2, new SpaInt(9)))))));
    }

    [TestMethod]
    public void MandatoryProperties_MustExistOnBothSides()
    {
        SpaObject candidate = Obj(Prop(1, new SpaInt(5)));

        // A mandatory filter constraint the candidate lacks fails.
        Assert.IsFalse(SpaPodFilter.Matches(candidate,
            Obj(Prop(1, new SpaInt(5)), Prop(2, new SpaInt(6), SpaPodPropFlag.Mandatory))));

        // And a mandatory candidate property the filter does not constrain fails too.
        SpaObject demanding = Obj(
            Prop(1, new SpaInt(5)),
            Prop(2, new SpaInt(6), SpaPodPropFlag.Mandatory));
        Assert.IsFalse(SpaPodFilter.Matches(demanding, Obj(Prop(1, new SpaInt(5)))));

        // Non-mandatory extras on either side are fine.
        Assert.IsTrue(SpaPodFilter.Matches(demanding,
            Obj(Prop(1, new SpaInt(5)), Prop(2, new SpaInt(6)))));
    }

    [TestMethod]
    public void Fractions_CompareByValueNotRepresentation()
    {
        // 1/2 equals 2/4 upstream (cross-multiplied): representation must not decide the match.
        Assert.IsTrue(SpaPodFilter.Matches(
            Obj(Prop(1, new SpaFraction(1, 2))), Obj(Prop(1, new SpaFraction(2, 4)))));
        Assert.IsFalse(SpaPodFilter.Matches(
            Obj(Prop(1, new SpaFraction(1, 2))), Obj(Prop(1, new SpaFraction(1, 3)))));
    }

    [TestMethod]
    public void MalformedChoices_MatchNothingRatherThanThrowing()
    {
        SpaObject candidate = Obj(Prop(1, new SpaInt(5)));

        // An empty choice carries no alternatives to intersect or bound: no match.
        Assert.IsFalse(SpaPodFilter.Matches(candidate,
            Obj(Prop(1, new SpaChoice(SpaChoiceType.Range, SpaType.Int, [])))));

        // And a wrongly-sized one cannot even be built: the arity is checked where the caller is
        // still in scope, so the matcher never has to guess what positions mean.
        Assert.ThrowsExactly<ArgumentException>(() => new SpaChoice(
            SpaChoiceType.Range, SpaType.Int, [new SpaInt(5), new SpaInt(0)]));
    }

    [TestMethod]
    public void StructValues_PairFieldsInOrder()
    {
        static SpaStruct Pair(SpaValue a, SpaValue b) => new([a, b]);

        Assert.IsTrue(SpaPodFilter.Matches(
            Obj(Prop(1, Pair(new SpaInt(1), new SpaInt(2)))),
            Obj(Prop(1, Pair(new SpaInt(1), new SpaInt(2))))));
        Assert.IsFalse(SpaPodFilter.Matches(
            Obj(Prop(1, Pair(new SpaInt(1), new SpaInt(2)))),
            Obj(Prop(1, Pair(new SpaInt(1), new SpaInt(3))))));
        Assert.IsFalse(SpaPodFilter.Matches(
            Obj(Prop(1, Pair(new SpaInt(1), new SpaInt(2)))),
            Obj(Prop(1, new SpaStruct([new SpaInt(1)])))));
        Assert.IsFalse(SpaPodFilter.Matches(
            Obj(Prop(1, new SpaInt(1))),
            Obj(Prop(1, Pair(new SpaInt(1), new SpaInt(1))))));
    }

    [TestMethod]
    public void CandidateRanges_MatchScalarsInsideThem()
    {
        SpaObject candidate = Obj(Prop(1, Range(new SpaInt(0), new SpaInt(10), new SpaInt(20))));

        Assert.IsTrue(SpaPodFilter.Matches(candidate, Obj(Prop(1, new SpaInt(15)))));
        Assert.IsFalse(SpaPodFilter.Matches(candidate, Obj(Prop(1, new SpaInt(25)))));
    }

    [TestMethod]
    public void LongFlags_AndLikeInts()
    {
        SpaObject candidate = Obj(Prop(1, Flags(new SpaLong(0b1100))));

        Assert.IsTrue(SpaPodFilter.Matches(candidate, Obj(Prop(1, Flags(new SpaLong(0b1010))))));
        Assert.IsFalse(SpaPodFilter.Matches(candidate, Obj(Prop(1, Flags(new SpaLong(0b0011))))));
    }

    [TestMethod]
    public void StepFilters_SupportLongSteps()
    {
        // Longs step like ints upstream: the multiple passes, the non-multiple fails, and a zero
        // step matches nothing rather than dividing by zero.
        SpaObject candidate = Obj(Prop(1, new SpaLong(20)));

        Assert.IsTrue(SpaPodFilter.Matches(candidate, Obj(Prop(1, Step(
            new SpaLong(0), new SpaLong(0), new SpaLong(100), new SpaLong(10))))));
        Assert.IsFalse(SpaPodFilter.Matches(candidate, Obj(Prop(1, Step(
            new SpaLong(0), new SpaLong(0), new SpaLong(100), new SpaLong(7))))));
        Assert.IsFalse(SpaPodFilter.Matches(candidate, Obj(Prop(1, Step(
            new SpaLong(0), new SpaLong(0), new SpaLong(100), new SpaLong(0))))));
    }

    [TestMethod]
    public void StepFilters_SupportRectangleSteps()
    {
        // Both dimensions must be multiples of the step, as upstream requires.
        SpaObject candidate = Obj(Prop(1, new SpaRectangle(640, 480)));

        Assert.IsTrue(SpaPodFilter.Matches(candidate, Obj(Prop(1, Step(
            new SpaRectangle(0, 0), new SpaRectangle(0, 0), new SpaRectangle(1920, 1080),
            new SpaRectangle(320, 240))))));
        Assert.IsFalse(SpaPodFilter.Matches(candidate, Obj(Prop(1, Step(
            new SpaRectangle(0, 0), new SpaRectangle(0, 0), new SpaRectangle(1920, 1080),
            new SpaRectangle(320, 241))))));
    }

    [TestMethod]
    public void StepFilters_RejectStepsOverNonSteppableTypes()
    {
        // Doubles have no step semantics upstream: the bounds admit, the step refuses.
        SpaObject candidate = Obj(Prop(1, new SpaDouble(4.0)));

        Assert.IsFalse(SpaPodFilter.Matches(candidate, Obj(Prop(1, Step(
            new SpaDouble(0), new SpaDouble(0), new SpaDouble(10), new SpaDouble(2))))));
    }

    [TestMethod]
    public void RangeFilters_CompareLongsAndFloatsByValue()
    {
        Assert.IsTrue(SpaPodFilter.Matches(
            Obj(Prop(1, new SpaLong(50))),
            Obj(Prop(1, Range(new SpaLong(0), new SpaLong(0), new SpaLong(100))))));
        Assert.IsFalse(SpaPodFilter.Matches(
            Obj(Prop(1, new SpaLong(150))),
            Obj(Prop(1, Range(new SpaLong(0), new SpaLong(0), new SpaLong(100))))));
        Assert.IsTrue(SpaPodFilter.Matches(
            Obj(Prop(1, new SpaFloat(1.5f))),
            Obj(Prop(1, Range(new SpaFloat(0), new SpaFloat(0), new SpaFloat(2))))));
        Assert.IsFalse(SpaPodFilter.Matches(
            Obj(Prop(1, new SpaFloat(2.5f))),
            Obj(Prop(1, Range(new SpaFloat(0), new SpaFloat(0), new SpaFloat(2))))));
    }

    [TestMethod]
    public void RangeFilters_CompareStringsOrdinally()
    {
        // Ordinal, like upstream's memcmp-flavoured ordering: "b" sits between, "z" does not.
        SpaObject candidate = Obj(Prop(1, new SpaString("b")));

        Assert.IsTrue(SpaPodFilter.Matches(candidate, Obj(Prop(1, Range(
            new SpaString("a"), new SpaString("a"), new SpaString("c"))))));
        Assert.IsFalse(SpaPodFilter.Matches(
            Obj(Prop(1, new SpaString("z"))), Obj(Prop(1, Range(
                new SpaString("a"), new SpaString("a"), new SpaString("c"))))));
    }

    [TestMethod]
    public void FlagsFilters_RejectNonIntegerFlagTypes()
    {
        // Only Int/Long can flag upstream; a float flag pair fails the match, not the build.
        SpaObject candidate = Obj(Prop(1, Flags(new SpaFloat(1.5f))));

        Assert.IsFalse(SpaPodFilter.Matches(candidate, Obj(Prop(1, new SpaFloat(1.5f)))));
    }

    [TestMethod]
    public void RangeAgainstRange_ComparesRectanglesByArea()
    {
        // Same-area rectangles tie on area and fall back to width, as upstream does.
        SpaObject candidate = Obj(Prop(1, Range(
            new SpaRectangle(4, 6), new SpaRectangle(2, 12), new SpaRectangle(8, 3))));

        Assert.IsTrue(SpaPodFilter.Matches(candidate, Obj(Prop(1, Range(
            new SpaRectangle(4, 6), new SpaRectangle(3, 8), new SpaRectangle(6, 4))))));
        Assert.IsFalse(SpaPodFilter.Matches(candidate, Obj(Prop(1, Range(
            new SpaRectangle(4, 6), new SpaRectangle(9, 9), new SpaRectangle(10, 10))))));
    }

    [TestMethod]
    public void MixedTypeRanges_CannotEvenBeBuilt()
    {
        // A range over mixed types is meaningless, and it never reaches the matcher: children
        // must cohere with the declared child type at construction, so the matcher's mixed-type
        // arm is unreachable defense rather than a live path.
        Assert.ThrowsExactly<ArgumentException>(() => new SpaChoice(SpaChoiceType.Range, SpaType.Int,
            [new SpaInt(5), new SpaInt(0), new SpaString("x")]));
    }

    [TestMethod]
    public void BoolAndIdScalars_CompareByValue()
    {
        Assert.IsTrue(SpaPodFilter.Matches(
            Obj(Prop(1, new SpaBool(true))), Obj(Prop(1, new SpaBool(true)))));
        Assert.IsFalse(SpaPodFilter.Matches(
            Obj(Prop(1, new SpaBool(true))), Obj(Prop(1, new SpaBool(false)))));
        Assert.IsTrue(SpaPodFilter.Matches(
            Obj(Prop(2, new SpaId(7))), Obj(Prop(2, new SpaId(7)))));
        Assert.IsFalse(SpaPodFilter.Matches(
            Obj(Prop(2, new SpaId(7))), Obj(Prop(2, new SpaId(8)))));
    }
}
