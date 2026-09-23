using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Narrowing a served parameter to what a filter allows - the projection half of
/// <c>spa_pod_filter</c>.
/// </summary>
/// <remarks>
/// <para>
/// These run without a daemon on purpose. The intersection rules are where format negotiation is
/// actually decided, and getting one wrong does not throw: the two sides agree on a format one of
/// them cannot produce, and the failure appears much later as a stream that connects and then
/// carries nothing.
/// </para>
/// <para>
/// The companion to <c>SpaPodFilter</c>, which answers only whether a candidate survives. A boolean
/// is enough to decide whether to offer a parameter; it is not enough to say what to offer.
/// </para>
/// </remarks>
[TestClass]
public sealed class SpaPodProjectionTests
{
    private static SpaObject Format(params (SpaKey Key, SpaValue Value)[] properties) =>
        new(
            SpaType.ObjectFormat,
            SpaParamType.EnumFormat,
            [.. properties.Select(p => new SpaPodProperty(p.Key, 0, p.Value))]
        );

    private static SpaChoice Enum(params int[] values) =>
        new(
            SpaChoiceType.Enum,
            SpaType.Int,
            [new SpaInt(values[0]), .. values.Select(v => (SpaValue)new SpaInt(v))]
        );

    private static SpaChoice Range(int def, int min, int max) =>
        new(SpaChoiceType.Range, SpaType.Int, [new SpaInt(def), new SpaInt(min), new SpaInt(max)]);

    private static SpaValue? ValueOf(SpaObject? o, SpaKey key) => o?.Find(key)?.Value;

    private static int[] Members(SpaChoice c) =>
        [.. c.Alternatives.Skip(1).OfType<SpaInt>().Select(i => i.Value).Distinct()];

    /// <summary>Two enumerations narrow to the members they share.</summary>
    /// <remarks>
    /// The case that makes a boolean matcher insufficient: both sides "match", but the answer is
    /// neither side's own list. Compared as a set: upstream copies a value once per equal pair it
    /// finds, defaults included, so a value that is both a default and a member appears more than
    /// once (filter.h 118-131), which changes nothing about what the answer allows.
    /// </remarks>
    [TestMethod]
    public void TwoEnumerations_NarrowToTheirIntersection()
    {
        SpaObject mine = Format((SpaFormat.AudioRate, Enum(44100, 48000, 96000)));
        SpaObject theirs = Format((SpaFormat.AudioRate, Enum(48000, 96000, 192000)));

        SpaObject? got = SpaPodProjection.Project(mine, theirs);

        Assert.IsNotNull(got, "two overlapping enumerations produced no intersection");

        var choice = ValueOf(got, SpaFormat.AudioRate) as SpaChoice;
        Assert.IsNotNull(choice, "the narrowed rate is not a choice");

        CollectionAssert.AreEquivalent(
            new[] { 48000, 96000 },
            Members(choice!),
            "the intersection kept the wrong members"
        );
    }

    /// <summary>When several values survive, the peer's preference leads.</summary>
    /// <remarks>
    /// Fixation takes the default, so this is what decides which format a negotiation settles on.
    /// Upstream walks the filter's values first (filter.h 118-131): the filter is the peer, and its
    /// order wins. A projection that led with its own preference would fixate on its own favourite
    /// whenever the peer could also take it - agreeing on a format the peer liked less.
    /// </remarks>
    [TestMethod]
    public void AmongSeveralSurvivors_TheFiltersPreferenceLeads()
    {
        SpaObject mine = Format((SpaFormat.AudioRate, Enum(96000, 48000, 96000)));
        SpaObject theirs = Format((SpaFormat.AudioRate, Enum(48000, 48000, 96000)));

        var choice =
            ValueOf(SpaPodProjection.Project(mine, theirs), SpaFormat.AudioRate) as SpaChoice;

        Assert.IsNotNull(choice);
        Assert.AreEqual(
            new SpaInt(48000),
            choice!.Alternatives[0],
            "the candidate's preference led instead of the filter's"
        );
    }

    /// <summary>A value copied exactly once collapses to a fixed value; one copied twice does not.</summary>
    /// <remarks>
    /// Upstream collapses to a single value only when exactly one copy was made (filter.h 246-257).
    /// A survivor that is also both sides' default is copied more than once, so the answer stays an
    /// enumeration - of one distinct value - and is fixated later. Asserting a collapse in that case
    /// would be asserting behaviour upstream does not have.
    /// </remarks>
    [TestMethod]
    public void ASurvivorCopiedOnce_CollapsesToAFixedValue_AndOneCopiedTwiceDoesNot()
    {
        SpaObject fixedMine = Format((SpaFormat.AudioRate, new SpaInt(48000)));
        SpaObject theirs = Format((SpaFormat.AudioRate, Enum(96000, 96000, 48000)));

        Assert.AreEqual(
            new SpaInt(48000),
            ValueOf(SpaPodProjection.Project(fixedMine, theirs), SpaFormat.AudioRate),
            "one survivor copied once should be a fixed value"
        );

        SpaObject mine = Format((SpaFormat.AudioRate, Enum(48000, 44100, 48000)));
        SpaObject theirsToo = Format((SpaFormat.AudioRate, Enum(48000, 48000, 192000)));

        var choice =
            ValueOf(SpaPodProjection.Project(mine, theirsToo), SpaFormat.AudioRate) as SpaChoice;
        Assert.IsNotNull(
            choice,
            "a survivor copied more than once stays an enumeration, as upstream leaves it"
        );
        CollectionAssert.AreEquivalent(
            new[] { 48000 },
            Members(choice!),
            "the only surviving value should be 48000"
        );
    }

    /// <summary>An enumeration against a range keeps only the members inside it.</summary>
    [TestMethod]
    public void AnEnumerationAgainstARange_KeepsWhatTheRangeAdmits()
    {
        SpaObject mine = Format((SpaFormat.AudioRate, Enum(8000, 44100, 48000, 192000)));
        SpaObject theirs = Format((SpaFormat.AudioRate, Range(48000, 44100, 48000)));

        SpaObject? got = SpaPodProjection.Project(mine, theirs);

        Assert.IsNotNull(got);

        var choice = ValueOf(got, SpaFormat.AudioRate) as SpaChoice;
        Assert.IsNotNull(choice);

        int[] kept = [.. choice!.Alternatives.Skip(1).OfType<SpaInt>().Select(i => i.Value)];
        CollectionAssert.AreEquivalent(new[] { 44100, 48000 }, kept);
    }

    /// <summary>Two ranges narrow to their overlap.</summary>
    [TestMethod]
    public void TwoRanges_NarrowToTheOverlap()
    {
        SpaObject mine = Format((SpaFormat.AudioRate, Range(44100, 8000, 48000)));
        SpaObject theirs = Format((SpaFormat.AudioRate, Range(48000, 44100, 192000)));

        SpaObject? got = SpaPodProjection.Project(mine, theirs);

        Assert.IsNotNull(got);

        var choice = ValueOf(got, SpaFormat.AudioRate) as SpaChoice;
        Assert.IsNotNull(choice, "two ranges should narrow to a range");

        Assert.AreEqual(
            new SpaInt(44100),
            choice!.Alternatives[1],
            "the low end should be the higher minimum"
        );
        Assert.AreEqual(
            new SpaInt(48000),
            choice.Alternatives[2],
            "the high end should be the lower maximum"
        );
    }

    /// <summary>Disjoint values produce nothing, which is a refusal rather than an empty answer.</summary>
    /// <remarks>
    /// The caller offers no parameter at all in this case. Returning an empty or unnarrowed pod
    /// instead is what makes a peer believe a format is available when it is not.
    /// </remarks>
    [TestMethod]
    public void DisjointValues_ProduceNothing()
    {
        SpaObject mine = Format((SpaFormat.AudioRate, Enum(44100, 88200)));
        SpaObject theirs = Format((SpaFormat.AudioRate, Enum(48000, 96000)));

        Assert.IsNull(
            SpaPodProjection.Project(mine, theirs),
            "disjoint enumerations must not produce a format"
        );
    }

    /// <summary>A property the filter never mentions survives untouched.</summary>
    [TestMethod]
    public void AnUnconstrainedProperty_SurvivesUnchanged()
    {
        SpaObject mine = Format(
            (SpaFormat.AudioRate, Enum(44100, 48000)),
            (SpaFormat.AudioChannels, new SpaInt(2))
        );

        SpaObject theirs = Format((SpaFormat.AudioRate, Enum(48000)));

        SpaObject? got = SpaPodProjection.Project(mine, theirs);

        Assert.IsNotNull(got);
        Assert.AreEqual(
            new SpaInt(2),
            ValueOf(got, SpaFormat.AudioChannels),
            "a property the filter does not mention should be carried through as it was"
        );
    }

    /// <summary>The projection never contradicts the matcher.</summary>
    /// <remarks>
    /// The two encode the same rules and are used together - the matcher to decide whether to
    /// answer, the projection to say what. If they ever disagreed, a node would either offer a
    /// parameter it then could not narrow, or narrow one it had already refused.
    /// </remarks>
    [TestMethod]
    public void TheProjectionAndTheMatcher_NeverDisagree()
    {
        SpaChoice[] shapes =
        [
            Enum(44100, 48000),
            Enum(48000, 96000),
            Enum(8000),
            Range(48000, 44100, 96000),
            Range(44100, 8000, 44100),
        ];

        foreach (SpaChoice a in shapes)
        {
            foreach (SpaChoice b in shapes)
            {
                SpaObject mine = Format((SpaFormat.AudioRate, a));
                SpaObject theirs = Format((SpaFormat.AudioRate, b));

                bool matches = SpaPodFilter.Matches(mine, theirs);
                bool projects = SpaPodProjection.Project(mine, theirs) is not null;

                Assert.AreEqual(
                    matches,
                    projects,
                    $"matcher said {matches} and projection said {projects} for {a.Kind} vs {b.Kind}"
                );
            }
        }
    }

    /// <summary>Range against range: the overlap, defaulting to the filter's value if it lands in it.</summary>
    /// <remarks>filter.h 185-223: the filter's default, else the candidate's, else the overlap's minimum.</remarks>
    [TestMethod]
    public void TwoRanges_DefaultToTheFiltersValue_ThenTheCandidates_ThenTheMinimum()
    {
        SpaChoice Overlap(SpaChoice mine, SpaChoice theirs) =>
            (SpaChoice)
                ValueOf(
                    SpaPodProjection.Project(
                        Format((SpaFormat.AudioRate, mine)),
                        Format((SpaFormat.AudioRate, theirs))
                    ),
                    SpaFormat.AudioRate
                )!;

        Assert.AreEqual(
            new SpaInt(48000),
            Overlap(Range(44100, 8000, 96000), Range(48000, 22050, 192000)).Alternatives[0],
            "the filter's default is in the overlap and should lead"
        );
        Assert.AreEqual(
            new SpaInt(44100),
            Overlap(Range(44100, 8000, 96000), Range(4000, 22050, 192000)).Alternatives[0],
            "the filter's default is outside the overlap, so the candidate's should lead"
        );
        Assert.AreEqual(
            new SpaInt(22050),
            Overlap(Range(8000, 8000, 96000), Range(4000, 22050, 192000)).Alternatives[0],
            "neither default is in the overlap, so its minimum should lead"
        );
    }

    /// <summary>Range against a stepped range yields a plain range, as upstream writes it.</summary>
    [TestMethod]
    public void ARangeAgainstAStep_YieldsAPlainRange()
    {
        var step = new SpaChoice(
            SpaChoiceType.Step,
            SpaType.Int,
            [new SpaInt(64), new SpaInt(16), new SpaInt(1024), new SpaInt(16)]
        );
        var got = (SpaChoice?)ValueOf(
            SpaPodProjection.Project(
                Format((SpaFormat.AudioRate, Range(256, 32, 512))),
                Format((SpaFormat.AudioRate, step))
            ),
            SpaFormat.AudioRate
        );

        Assert.IsNotNull(got);
        Assert.AreEqual(
            SpaChoiceType.Range,
            got!.Kind,
            "upstream marks the overlap of range and step as a plain range (filter.h 223)"
        );
        Assert.AreEqual(new SpaInt(32), got.Alternatives[1]);
        Assert.AreEqual(new SpaInt(512), got.Alternatives[2]);
    }

    /// <summary>Flags against an enumeration is refused rather than ANDed.</summary>
    /// <remarks>filter.h 233-244: flags pair only with None and Flags; anything else is -ENOTSUP.</remarks>
    [TestMethod]
    public void FlagsAgainstAnEnumeration_IsRefused()
    {
        var flags = new SpaChoice(SpaChoiceType.Flags, SpaType.Int, [new SpaInt(0b0110)]);
        Assert.IsNull(
            SpaPodProjection.Project(
                Format((SpaFormat.AudioRate, flags)),
                Format((SpaFormat.AudioRate, Enum(2, 4)))
            )
        );

        var theirs = new SpaChoice(SpaChoiceType.Flags, SpaType.Int, [new SpaInt(0b0011)]);
        var and = (SpaChoice?)ValueOf(
            SpaPodProjection.Project(
                Format((SpaFormat.AudioRate, flags)),
                Format((SpaFormat.AudioRate, theirs))
            ),
            SpaFormat.AudioRate
        );
        Assert.IsNotNull(and, "flags against flags is the supported pairing");
        Assert.AreEqual(new SpaInt(0b0010), and!.Alternatives[0], "flags intersect by AND");
    }

    /// <summary>A property only the filter has is carried into the answer, unless marked drop.</summary>
    /// <remarks>filter.h 301-313: the peer's constraint travels with the result.</remarks>
    [TestMethod]
    public void AFilterOnlyProperty_IsCarriedIntoTheAnswer_UnlessMarkedDrop()
    {
        SpaObject mine = Format((SpaFormat.AudioRate, new SpaInt(48000)));
        SpaObject theirs = new(
            SpaType.ObjectFormat,
            SpaParamType.EnumFormat,
            [
                new SpaPodProperty(SpaFormat.AudioRate, SpaPodPropFlags.None, new SpaInt(48000)),
                new SpaPodProperty(SpaFormat.AudioChannels, SpaPodPropFlags.None, new SpaInt(2)),
                new SpaPodProperty(SpaFormat.AudioFormat, SpaPodPropFlags.Drop, new SpaId(283)),
            ]
        );

        SpaObject? got = SpaPodProjection.Project(mine, theirs);

        Assert.IsNotNull(got);
        Assert.AreEqual(
            new SpaInt(2),
            ValueOf(got, SpaFormat.AudioChannels),
            "the filter-only property should be carried in"
        );
        Assert.IsNull(
            got!.Find(SpaFormat.AudioFormat),
            "a filter-only property marked drop should be left out"
        );
    }

    /// <summary>The answer's property flags are the AND of both sides'.</summary>
    [TestMethod]
    public void ThePropertyFlags_AreTheAndOfBothSides()
    {
        SpaObject mine = new(
            SpaType.ObjectFormat,
            SpaParamType.EnumFormat,
            [
                new SpaPodProperty(
                    SpaFormat.AudioRate,
                    SpaPodPropFlags.Mandatory | SpaPodPropFlags.DontFixate,
                    new SpaInt(48000)
                ),
            ]
        );
        SpaObject theirs = new(
            SpaType.ObjectFormat,
            SpaParamType.EnumFormat,
            [new SpaPodProperty(SpaFormat.AudioRate, SpaPodPropFlags.Mandatory, new SpaInt(48000))]
        );

        Assert.AreEqual(
            SpaPodPropFlags.Mandatory,
            SpaPodProjection.Project(mine, theirs)!.Find(SpaFormat.AudioRate)!.Flags,
            "filter.h 98: flags are p1->flags & p2->flags"
        );
    }

    /// <summary>When the filter's own default is not valid for its own choice, the candidate leads.</summary>
    /// <remarks>filter.h 110-116: the roles swap rather than letting an invalid default win.</remarks>
    [TestMethod]
    public void AFilterWhoseDefaultIsInvalid_LetsTheCandidateLead()
    {
        // An enumeration whose default is not among its own members is not a valid choice
        // (compare.h checks members from index 1). Without the swap the filter's order would lead
        // and 44100 would win; with it, the candidate's order leads and 96000 wins.
        SpaObject mine = Format(
            (
                SpaFormat.AudioRate,
                new SpaChoice(
                    SpaChoiceType.Enum,
                    SpaType.Int,
                    [new SpaInt(96000), new SpaInt(96000), new SpaInt(44100), new SpaInt(48000)]
                )
            )
        );
        SpaObject theirs = Format(
            (
                SpaFormat.AudioRate,
                new SpaChoice(
                    SpaChoiceType.Enum,
                    SpaType.Int,
                    [new SpaInt(44100), new SpaInt(48000), new SpaInt(96000)]
                )
            )
        );

        var got = ValueOf(SpaPodProjection.Project(mine, theirs), SpaFormat.AudioRate);
        Assert.IsNotNull(got, "the swap still intersects; it only changes which side leads");
        SpaValue lead = got is SpaChoice c ? c.Alternatives[0] : got!;
        Assert.AreEqual(
            new SpaInt(96000),
            lead,
            "with the filter's default invalid, the candidate's order should lead"
        );
    }
}
