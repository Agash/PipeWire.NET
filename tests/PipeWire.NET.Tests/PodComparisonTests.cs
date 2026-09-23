using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The pod comparison rules, against the semantics upstream's <c>spa_pod_compare</c> defines.
/// </summary>
/// <remarks>
/// <para>
/// These decide whether a format the daemon offers satisfies a filter, so getting one arm wrong
/// does not crash anything: it negotiates a format nobody asked for, or refuses one that was fine,
/// and the failure surfaces much later as a stream that will not start. They are pure functions
/// over values, which is why they are worth testing exhaustively rather than through a daemon.
/// </para>
/// <para>
/// The unusual arms are upstream's, not ours: rectangles order by area before width, fractions
/// cross-multiply so 1/2 and 2/4 are equal, an enumeration ignores its own default at index 0, and
/// anything without a scalar ordering falls back to comparing encoded bytes.
/// </para>
/// </remarks>
[TestClass]
public sealed class PodComparisonTests
{
    /// <summary>Each scalar type orders by its own value.</summary>
    [TestMethod]
    public void CompareValue_OrdersEachScalarByItsOwnRule()
    {
        Assert.AreEqual(-1, SpaPodCompare.CompareValue(new SpaBool(false), new SpaBool(true)));
        Assert.AreEqual(0, SpaPodCompare.CompareValue(new SpaBool(true), new SpaBool(true)));
        Assert.AreEqual(1, SpaPodCompare.CompareValue(new SpaId(9), new SpaId(2)));
        Assert.AreEqual(-1, SpaPodCompare.CompareValue(new SpaInt(-3), new SpaInt(0)));
        Assert.AreEqual(1, SpaPodCompare.CompareValue(new SpaLong(long.MaxValue), new SpaLong(0)));
        Assert.AreEqual(-1, SpaPodCompare.CompareValue(new SpaFloat(0.5f), new SpaFloat(1.5f)));
        Assert.AreEqual(1, SpaPodCompare.CompareValue(new SpaDouble(2.0), new SpaDouble(1.0)));
        Assert.AreEqual(-1, SpaPodCompare.CompareValue(new SpaString("a"), new SpaString("b")));
        Assert.AreEqual(0, SpaPodCompare.CompareValue(new SpaString("a"), new SpaString("a")));
    }

    /// <summary>Rectangles order by area first, and only then by width.</summary>
    /// <remarks>
    /// Upstream's rule, and not the obvious one: 4x1 and 1x4 have the same area, so the width
    /// decides. A comparison that ordered by width first would call 4x1 the larger of 4x1 and 2x4,
    /// which has three times less area.
    /// </remarks>
    [TestMethod]
    public void CompareValue_OrdersRectanglesByAreaThenWidth()
    {
        Assert.AreEqual(
            -1, SpaPodCompare.CompareValue(new SpaRectangle(4, 1), new SpaRectangle(2, 4)),
            "a rectangle with less area sorted higher");

        Assert.AreEqual(
            1, SpaPodCompare.CompareValue(new SpaRectangle(4, 1), new SpaRectangle(1, 4)),
            "equal areas did not fall through to width");

        Assert.AreEqual(0, SpaPodCompare.CompareValue(new SpaRectangle(64, 64), new SpaRectangle(64, 64)));
    }

    /// <summary>Fractions cross-multiply, so unreduced forms of the same rate are equal.</summary>
    [TestMethod]
    public void CompareValue_ComparesFractionsByValueNotByForm()
    {
        Assert.AreEqual(
            0, SpaPodCompare.CompareValue(new SpaFraction(1, 2), new SpaFraction(2, 4)),
            "the same rate written two ways did not compare equal");

        Assert.AreEqual(1, SpaPodCompare.CompareValue(new SpaFraction(60, 1), new SpaFraction(30, 1)));
        Assert.AreEqual(-1, SpaPodCompare.CompareValue(new SpaFraction(24000, 1001), new SpaFraction(30, 1)));
    }

    /// <summary>Two values of different types fall back to comparing their encodings.</summary>
    /// <remarks>
    /// The default arm can only answer equal or not equal, which is upstream's own limit. What it
    /// must not do is claim an ordering it cannot have, so the only assertion available is that
    /// unlike things are unequal and identical things are equal.
    /// </remarks>
    [TestMethod]
    public void CompareValue_FallsBackToBytesForTypesWithNoOrdering()
    {
        Assert.AreEqual(
            1, SpaPodCompare.CompareValue(new SpaInt(1), new SpaLong(1)),
            "two different types compared equal");

        var one = new SpaArray(SpaType.Int, [new SpaInt(1), new SpaInt(2)]);
        var same = new SpaArray(SpaType.Int, [new SpaInt(1), new SpaInt(2)]);
        var other = new SpaArray(SpaType.Int, [new SpaInt(1), new SpaInt(3)]);

        Assert.AreEqual(0, SpaPodCompare.CompareValue(one, same), "identical arrays compared unequal");
        Assert.AreEqual(1, SpaPodCompare.CompareValue(one, other));
    }

    /// <summary>A step of zero or less is invalid rather than a step nothing satisfies.</summary>
    /// <remarks>
    /// -1 and 0 mean different things to the caller: "this choice is malformed" and "this value is
    /// not on the grid". Collapsing them would make a daemon sending a zero step look like a
    /// negotiation failure rather than a protocol error.
    /// </remarks>
    [TestMethod]
    public void IsStepOf_SeparatesAnInvalidStepFromAMissedOne()
    {
        Assert.AreEqual(1, SpaPodCompare.IsStepOf(new SpaInt(48000), new SpaInt(100)));
        Assert.AreEqual(0, SpaPodCompare.IsStepOf(new SpaInt(48001), new SpaInt(100)));
        Assert.AreEqual(-1, SpaPodCompare.IsStepOf(new SpaInt(48000), new SpaInt(0)));
        Assert.AreEqual(-1, SpaPodCompare.IsStepOf(new SpaInt(48000), new SpaInt(-2)));

        Assert.AreEqual(1, SpaPodCompare.IsStepOf(new SpaLong(1000), new SpaLong(250)));
        Assert.AreEqual(0, SpaPodCompare.IsStepOf(new SpaLong(1001), new SpaLong(250)));
        Assert.AreEqual(-1, SpaPodCompare.IsStepOf(new SpaLong(1000), new SpaLong(0)));

        Assert.AreEqual(1, SpaPodCompare.IsStepOf(new SpaRectangle(64, 32), new SpaRectangle(16, 16)));
        Assert.AreEqual(0, SpaPodCompare.IsStepOf(new SpaRectangle(65, 32), new SpaRectangle(16, 16)));
        Assert.AreEqual(-1, SpaPodCompare.IsStepOf(new SpaRectangle(64, 32), new SpaRectangle(0, 16)));
        Assert.AreEqual(-1, SpaPodCompare.IsStepOf(new SpaRectangle(64, 32), new SpaRectangle(16, 0)));

        Assert.AreEqual(
            -1, SpaPodCompare.IsStepOf(new SpaFloat(1f), new SpaFloat(0.5f)),
            "a type with no step rule reported a step");
    }

    /// <summary>A range is inclusive at both ends, and a step applies inside it.</summary>
    [TestMethod]
    public void IsInRange_IsInclusiveAndHonoursAStep()
    {
        SpaValue min = new SpaInt(10), max = new SpaInt(20);

        Assert.AreEqual(1, SpaPodCompare.IsInRange(new SpaInt(10), min, max, null));
        Assert.AreEqual(1, SpaPodCompare.IsInRange(new SpaInt(20), min, max, null));
        Assert.AreEqual(0, SpaPodCompare.IsInRange(new SpaInt(9), min, max, null));
        Assert.AreEqual(0, SpaPodCompare.IsInRange(new SpaInt(21), min, max, null));

        Assert.AreEqual(1, SpaPodCompare.IsInRange(new SpaInt(15), min, max, new SpaInt(5)));
        Assert.AreEqual(0, SpaPodCompare.IsInRange(new SpaInt(16), min, max, new SpaInt(5)));
        Assert.AreEqual(-1, SpaPodCompare.IsInRange(new SpaInt(15), min, max, new SpaInt(0)));
    }

    /// <summary>Each kind of choice accepts what upstream says it accepts.</summary>
    /// <remarks>
    /// The index-0 rule is the one worth pinning: every choice carries its default there, and an
    /// enumeration that also counted index 0 would accept a default the daemon never listed as an
    /// option. Range and step read their bounds from indexes 1 and 2 for the same reason.
    /// </remarks>
    [TestMethod]
    public void IsValidChoice_FollowsTheRuleForEachKind()
    {
        ImmutableArray<SpaValue> enumeration =
            [new SpaInt(44100), new SpaInt(48000), new SpaInt(96000)];

        Assert.IsTrue(SpaPodCompare.IsValidChoice(new SpaInt(48000), enumeration, SpaChoiceType.Enum));
        Assert.IsTrue(SpaPodCompare.IsValidChoice(new SpaInt(96000), enumeration, SpaChoiceType.Enum));
        Assert.IsFalse(
            SpaPodCompare.IsValidChoice(new SpaInt(44100), enumeration, SpaChoiceType.Enum),
            "an enumeration accepted its own default, which upstream does not list as a member");

        ImmutableArray<SpaValue> none = [new SpaInt(48000)];
        Assert.IsTrue(SpaPodCompare.IsValidChoice(new SpaInt(48000), none, SpaChoiceType.None));
        Assert.IsFalse(SpaPodCompare.IsValidChoice(new SpaInt(44100), none, SpaChoiceType.None));
        Assert.IsFalse(SpaPodCompare.IsValidChoice(new SpaInt(48000), [], SpaChoiceType.None));

        ImmutableArray<SpaValue> range = [new SpaInt(48000), new SpaInt(8000), new SpaInt(192000)];
        Assert.IsTrue(SpaPodCompare.IsValidChoice(new SpaInt(48000), range, SpaChoiceType.Range));
        Assert.IsFalse(SpaPodCompare.IsValidChoice(new SpaInt(4000), range, SpaChoiceType.Range));
        Assert.IsFalse(
            SpaPodCompare.IsValidChoice(new SpaInt(48000), [new SpaInt(48000)], SpaChoiceType.Range),
            "a range without bounds accepted a value anyway");

        ImmutableArray<SpaValue> step =
            [new SpaInt(48000), new SpaInt(8000), new SpaInt(192000), new SpaInt(8000)];
        Assert.IsTrue(SpaPodCompare.IsValidChoice(new SpaInt(48000), step, SpaChoiceType.Step));
        Assert.IsFalse(SpaPodCompare.IsValidChoice(new SpaInt(48001), step, SpaChoiceType.Step));
        Assert.IsFalse(SpaPodCompare.IsValidChoice(new SpaInt(48000), range, SpaChoiceType.Step));

        Assert.IsTrue(
            SpaPodCompare.IsValidChoice(new SpaInt(7), [], SpaChoiceType.Flags),
            "flags refused a value, but any combination of flags is allowed");

        Assert.IsFalse(SpaPodCompare.IsValidChoice(new SpaInt(1), none, (SpaChoiceType)999));
    }

    /// <summary>Intersecting flags yields the common bits, or nothing when there are none.</summary>
    /// <remarks>
    /// Null rather than a zero value: an empty intersection means the two sides agree on nothing,
    /// which is a failed negotiation, whereas a flags value of zero is a legal value meaning "no
    /// flags set". Returning the latter for the former would offer a format nobody supports.
    /// </remarks>
    [TestMethod]
    public void AndFlags_AnswersNothingWhenTheSidesShareNoBits()
    {
        Assert.AreEqual(new SpaInt(0b0100), SpaPodCompare.AndFlags(new SpaInt(0b1100), new SpaInt(0b0110)));
        Assert.AreEqual(new SpaLong(0b0100), SpaPodCompare.AndFlags(new SpaLong(0b1100), new SpaLong(0b0110)));

        Assert.IsNull(SpaPodCompare.AndFlags(new SpaInt(0b1000), new SpaInt(0b0001)));
        Assert.IsNull(SpaPodCompare.AndFlags(new SpaLong(0b1000), new SpaLong(0b0001)));
        Assert.IsNull(
            SpaPodCompare.AndFlags(new SpaInt(0b1100), new SpaLong(0b0110)),
            "two different types were intersected as though they were the same one");
        Assert.IsNull(SpaPodCompare.AndFlags(new SpaFloat(1f), new SpaFloat(1f)));
    }
}
