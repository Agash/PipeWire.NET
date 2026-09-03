using System.Collections.Immutable;

namespace PipeWire.NET.Spa;

/// <summary>
/// Decides whether a parameter value satisfies an enumeration filter, mirroring upstream
/// <c>spa_pod_filter</c> (<c>spa/include/spa/pod/filter.h</c>) as a boolean match.
/// </summary>
/// <remarks>
/// <para>
/// Upstream builds a projected copy; this answers only whether the candidate survives, which is
/// what a serving implementation needs to decide emission. The match rules are the same: value
/// sets intersect for None/Enum, range membership for Range/Step, bitwise overlap for Flags,
/// range overlap for Range against Range, recursion for nested objects and structs.
/// </para>
/// <para>
/// Deliberate divergences, all erring toward no false negatives except where upstream itself
/// rejects: an unsupported combination (anything against Flags except None/Flags, a step over a
/// non-Int/Long/Rectangle, a malformed range) does not match, exactly as upstream skips the
/// item; an unknown composite falls back to structural equality, which agrees with
/// <c>memcmp</c> on real pods.
/// </para>
/// </remarks>
internal static class SpaPodFilter
{
    /// <summary>Whether a served parameter object satisfies every constraint of a filter object.</summary>
    /// <remarks>
    /// Object type and id are not compared: upstream matches per property only. A mandatory
    /// property missing on either side fails the match, in both directions, as upstream does.
    /// </remarks>
    internal static bool Matches(SpaObject candidate, SpaObject filter)
    {
        foreach (SpaProperty have in candidate.Properties)
        {
            SpaProperty? wanted = filter.Find(have.Key);
            if (wanted is null)
            {
                if ((have.Flags & SpaPodPropFlag.Mandatory) != 0) return false;
                continue;
            }

            if (!ValuesMatch(have.Value, wanted.Value)) return false;
        }

        foreach (SpaProperty wanted in filter.Properties)
        {
            if (candidate.Find(wanted.Key) is not null) continue;
            if ((wanted.Flags & SpaPodPropFlag.Mandatory) != 0) return false;
        }

        return true;
    }

    /// <summary>Whether a candidate value satisfies a filter value of the same property.</summary>
    internal static bool ValuesMatch(SpaValue candidate, SpaValue filter)
    {
        // Nested composites recurse rather than compare as blobs: an object filter constrains
        // per property, a struct pairs its fields in order, as upstream does.
        if (candidate is SpaObject candidateObject && filter is SpaObject filterObject)
            return Matches(candidateObject, filterObject);

        if (candidate is SpaStruct candidateStruct && filter is SpaStruct filterStruct)
        {
            if (candidateStruct.Fields.Length != filterStruct.Fields.Length) return false;
            for (int i = 0; i < candidateStruct.Fields.Length; i++)
                if (!ValuesMatch(candidateStruct.Fields[i], filterStruct.Fields[i])) return false;

            return true;
        }

        (SpaChoiceType candidateKind, ImmutableArray<SpaValue> candidateAlts) = Alternatives(candidate);
        (SpaChoiceType filterKind, ImmutableArray<SpaValue> filterAlts) = Alternatives(filter);

        if (candidateAlts.IsDefaultOrEmpty || filterAlts.IsDefaultOrEmpty) return false;

        bool candidateRanged = candidateKind is SpaChoiceType.Range or SpaChoiceType.Step;
        bool filterRanged = filterKind is SpaChoiceType.Range or SpaChoiceType.Step;
        bool candidateFlagged = candidateKind is SpaChoiceType.Flags;
        bool filterFlagged = filterKind is SpaChoiceType.Flags;
        bool candidateList = candidateKind is SpaChoiceType.None or SpaChoiceType.Enum;
        bool filterList = filterKind is SpaChoiceType.None or SpaChoiceType.Enum;

        // Flags pair with None and Flags by ANDing the pair (upstream rejects anything else
        // against Flags). Only Int/Long can flag; anything else fails the match as upstream
        // fails the item.
        if (candidateFlagged || filterFlagged)
        {
            if ((candidateKind is SpaChoiceType.None or SpaChoiceType.Flags)
                && (filterKind is SpaChoiceType.None or SpaChoiceType.Flags))
                return FlagsOverlap(candidateAlts[0], filterAlts[0]);

            return false;
        }

        if (!candidateRanged && !filterRanged)
            return candidateList && filterList && Intersect(candidateAlts, filterAlts);

        if (candidateRanged && filterRanged)
            return RangesOverlap(candidateKind, candidateAlts, filterKind, filterAlts);

        if (filterRanged)
            return RangeOf(filterKind, filterAlts) is { } range
                && candidateAlts.Any(a => InRange(a, range));

        return RangeOf(candidateKind, candidateAlts) is { } candidateRange
            && filterAlts.Any(b => InRange(b, candidateRange));
    }

    private static (SpaChoiceType Kind, ImmutableArray<SpaValue> Alts) Alternatives(SpaValue value) =>
        value is SpaChoice choice
            ? (choice.Kind, choice.Alternatives)
            : (SpaChoiceType.None, [value]);

    private static bool Intersect(ImmutableArray<SpaValue> left, ImmutableArray<SpaValue> right)
    {
        foreach (SpaValue a in left)
            foreach (SpaValue b in right)
                if (ScalarEqual(a, b)) return true;

        return false;
    }

    /// <summary>Bitwise overlap of two flag defaults: upstream ANDs the pair and rejects zero.</summary>
    private static bool FlagsOverlap(SpaValue candidate, SpaValue filter)
    {
        if (candidate is SpaInt ci && filter is SpaInt fi) return (ci.Value & fi.Value) != 0;
        if (candidate is SpaLong cl && filter is SpaLong fl) return (cl.Value & fl.Value) != 0;
        return false;
    }

    private sealed record FilterRange(
        SpaValue Min, SpaValue Max, SpaValue? Step, Func<SpaValue, SpaValue, bool> SameType);

    private static FilterRange? RangeOf(SpaChoiceType kind, ImmutableArray<SpaValue> alts)
    {
        // Range is default/min/max, Step adds the step. Fewer alternatives is malformed, and a
        // range over mixed types is meaningless: both fail the match as upstream fails the item.
        int need = kind == SpaChoiceType.Step ? 4 : 3;
        if (alts.Length < need) return null;

        SpaValue min = alts[1], max = alts[2];
        SpaValue? step = kind == SpaChoiceType.Step ? alts[3] : null;
        if (!SameScalarType(min, max) || (step is not null && !SameScalarType(min, step)))
            return null;

        return new FilterRange(min, max, step, SameScalarType);
    }

    private static bool InRange(SpaValue value, FilterRange range)
    {
        if (!range.SameType(value, range.Min)) return false;
        if (CompareScalars(value, range.Min) < 0 || CompareScalars(value, range.Max) > 0)
            return false;

        // Upstream tests value % step == 0, not (value - min): only Int/Long/Rectangle can step.
        if (range.Step is null) return true;
        return (value, range.Step) switch
        {
            (SpaInt v, SpaInt s) => s.Value >= 1 && v.Value % s.Value == 0,
            (SpaLong v, SpaLong s) => s.Value >= 1 && v.Value % s.Value == 0,
            (SpaRectangle v, SpaRectangle s) => s.Width >= 1 && s.Height >= 1
                && v.Width % s.Width == 0 && v.Height % s.Height == 0,
            _ => false,
        };
    }

    private static bool RangesOverlap(
        SpaChoiceType candidateKind, ImmutableArray<SpaValue> candidateAlts,
        SpaChoiceType filterKind, ImmutableArray<SpaValue> filterAlts)
    {
        // Overlap is max-of-mins against min-of-maxes; an impossible range matches nothing.
        if (RangeOf(candidateKind, candidateAlts) is not { } c) return false;
        if (RangeOf(filterKind, filterAlts) is not { } f) return false;
        if (!c.SameType(f.Min, c.Min)) return false;

        SpaValue lo = CompareScalars(c.Min, f.Min) < 0 ? f.Min : c.Min;
        SpaValue hi = CompareScalars(c.Max, f.Max) < 0 ? c.Max : f.Max;
        return CompareScalars(hi, lo) >= 0;
    }

    private static bool SameScalarType(SpaValue a, SpaValue b) =>
        (a, b) is (SpaInt, SpaInt) or (SpaLong, SpaLong)
            or (SpaFloat, SpaFloat) or (SpaDouble, SpaDouble)
            or (SpaBool, SpaBool) or (SpaId, SpaId)
            or (SpaString, SpaString) or (SpaRectangle, SpaRectangle)
            or (SpaFraction, SpaFraction);

    /// <summary>Upstream <c>spa_pod_compare_value</c> ordering for the scalar types.</summary>
    private static int CompareScalars(SpaValue a, SpaValue b)
    {
        return (a, b) switch
        {
            (SpaInt x, SpaInt y) => x.Value.CompareTo(y.Value),
            (SpaLong x, SpaLong y) => x.Value.CompareTo(y.Value),
            (SpaFloat x, SpaFloat y) => x.Value.CompareTo(y.Value),
            (SpaDouble x, SpaDouble y) => x.Value.CompareTo(y.Value),
            (SpaBool x, SpaBool y) => (x.Value ? 1 : 0).CompareTo(y.Value ? 1 : 0),
            (SpaId x, SpaId y) => x.Value.CompareTo(y.Value),
            (SpaString x, SpaString y) => string.CompareOrdinal(x.Value, y.Value),
            (SpaRectangle x, SpaRectangle y) =>
                CompareArea(x, y) is int area && area != 0 ? area : x.Width.CompareTo(y.Width),
            // Cross-multiplied, as upstream does: 1/2 equals 2/4.
            (SpaFraction x, SpaFraction y) =>
                ((ulong)x.Numerator * y.Denominator).CompareTo((ulong)y.Numerator * x.Denominator),
            _ => -2,
        };
    }

    private static int CompareArea(SpaRectangle x, SpaRectangle y) =>
        ((ulong)x.Width * x.Height).CompareTo((ulong)y.Width * y.Height);

    private static bool ScalarEqual(SpaValue a, SpaValue b) =>
        SameScalarType(a, b) && CompareScalars(a, b) == 0;
}
