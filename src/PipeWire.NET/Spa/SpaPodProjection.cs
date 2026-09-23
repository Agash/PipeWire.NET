using System.Collections.Immutable;

namespace PipeWire.NET.Spa;

/// <summary>
/// A served parameter narrowed by a peer's filter, transcribed from upstream <c>spa/pod/filter.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the intersection that decides a negotiation. A port that can do 44100 or 48000, filtered
/// by a peer that can do 48000 or 96000, must answer 48000 - and when several values survive, which
/// one leads matters just as much, because fixation takes the first. Upstream's
/// <c>spa_pod_filter</c> settles both, and this follows it case for case: the same table of
/// None/Enum/Range/Step/Flags combinations, the same preference for the <em>filter's</em> values
/// (the peer's), the same property-flag rules for <c>MANDATORY</c> and <c>DROP</c>.
/// </para>
/// <para>
/// It is hand-transcribed rather than generated because the C is <c>static inline</c> over the pod
/// builder, and translating it through ClangSharp drags in the builder's closure (see
/// <c>generate/podfilter.rsp</c>). Being hand-written is exactly why each branch below names the
/// lines of <c>filter.h</c> it follows: a transcription that drifts produces a negotiation that
/// silently agrees on the wrong format.
/// </para>
/// <para>
/// One representational difference, deliberately: upstream wraps a single surviving value in a
/// choice of kind None, which <c>spa_pod_get_values</c> then unwraps; here it is the bare value.
/// The two are read identically, and the bare value is what the rest of the managed model expects.
/// </para>
/// </remarks>
internal static class SpaPodProjection
{
    /// <summary>
    /// <c>spa_pod_filter</c> over two objects: the candidate narrowed by the filter, or null when the
    /// filter rules it out.
    /// </summary>
    /// <remarks>
    /// <c>spa_pod_filter_part</c>, object case (filter.h 285-317). Each candidate property is
    /// intersected with the filter's property of the same key. A candidate property the filter does
    /// not mention fails if it is mandatory, is dropped if it is marked drop, and is otherwise kept.
    /// A filter property the candidate does not have fails if mandatory, is dropped if marked drop,
    /// and is otherwise <em>added</em> - the peer's constraint travels into the answer.
    /// </remarks>
    internal static SpaObject? Project(SpaObject candidate, SpaObject filter)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(filter);

        var result = ImmutableArray.CreateBuilder<SpaPodProperty>(candidate.Properties.Length);

        foreach (SpaPodProperty p1 in candidate.Properties)
        {
            SpaPodProperty? p2 = filter.Find(p1.Key);
            if (p2 is not null)
            {
                if (FilterProp(p1, p2) is not { } narrowed)
                    return null;
                result.Add(narrowed);
            }
            else if ((p1.Flags & SpaPodPropFlags.Mandatory) != 0)
            {
                return null;
            }
            else if ((p1.Flags & SpaPodPropFlags.Drop) == 0)
            {
                result.Add(p1);
            }
        }

        foreach (SpaPodProperty p2 in filter.Properties)
        {
            if (candidate.Find(p2.Key) is not null)
                continue;
            if ((p2.Flags & SpaPodPropFlags.Mandatory) != 0)
                return null;
            if ((p2.Flags & SpaPodPropFlags.Drop) == 0)
                result.Add(p2);
        }

        return candidate with
        {
            Properties = result.ToImmutable(),
        };
    }

    /// <summary>
    /// <c>spa_pod_filter_prop</c>: one property narrowed by another of the same key, or null when the
    /// two share nothing.
    /// </summary>
    /// <remarks>
    /// The result's flags are the AND of both sides' (filter.h 98), so a property is mandatory in the
    /// answer only if both said so.
    /// </remarks>
    internal static SpaPodProperty? FilterProp(SpaPodProperty p1, SpaPodProperty p2)
    {
        ArgumentNullException.ThrowIfNull(p1);
        ArgumentNullException.ThrowIfNull(p2);

        return FilterValues(p1.Value, p2.Value) is { } value
            ? new SpaPodProperty(p1.Key, p1.Flags & p2.Flags, value)
            : null;
    }

    /// <summary>
    /// The value half of <c>spa_pod_filter_prop</c> (filter.h 82-260): the candidate's choice narrowed
    /// by the filter's, or null when no value survives or the combination is not supported.
    /// </summary>
    internal static SpaValue? FilterValues(SpaValue candidate, SpaValue filter)
    {
        (SpaChoiceType p1c, ImmutableArray<SpaValue> alt1, SpaType childType) = Values(candidate);
        (SpaChoiceType p2c, ImmutableArray<SpaValue> alt2, _) = Values(filter);

        // filter.h 85-86: an empty choice is invalid.
        if (alt1.IsDefaultOrEmpty || alt2.IsDefaultOrEmpty)
            return null;

        // filter.h 94-96: incompatible property types.
        if (!SpaPodCompare.SameType(alt1[0], alt2[0]))
            return null;

        // filter.h 110-116: prefer the filter's values, but only if the filter's own default is
        // valid for its own choice; otherwise the roles swap and the candidate's lead.
        if (!SpaPodCompare.IsValidChoice(alt2[0], alt2, p2c))
        {
            (alt1, alt2) = (alt2, alt1);
            (p1c, p2c) = (p2c, p1c);
        }

        var copied = ImmutableArray.CreateBuilder<SpaValue>();
        var nCopied = 0;

        bool list1 = p1c is SpaChoiceType.None or SpaChoiceType.Enum;
        bool list2 = p2c is SpaChoiceType.None or SpaChoiceType.Enum;
        bool range1 = p1c is SpaChoiceType.Range or SpaChoiceType.Step;
        bool range2 = p2c is SpaChoiceType.Range or SpaChoiceType.Step;

        if (list1 && list2)
        {
            // filter.h 118-131: every equal value, walking the filter's side first so its order -
            // and so its default - leads. The first value copied is written twice: once as the
            // default, once as a member. Both loops start at index 0, defaults included, exactly as
            // upstream does; a value that is both a default and a member is copied more than once.
            foreach (SpaValue a2 in alt2)
            {
                foreach (SpaValue a1 in alt1)
                {
                    if (SpaPodCompare.CompareValue(a1, a2) != 0)
                        continue;
                    if (nCopied++ == 0)
                        copied.Add(a1);
                    copied.Add(a1);
                }
            }
        }
        else if (list1 && range2)
        {
            // filter.h 133-163: the candidate's values that fall inside the filter's range. The
            // filter's range default leads if it is itself in range and one of the candidate's.
            if (alt2.Length < (p2c == SpaChoiceType.Step ? 4 : 3))
                return null;
            SpaValue min = alt2[1],
                max = alt2[2];
            SpaValue? step = p2c == SpaChoiceType.Step ? alt2[3] : null;
            var foundDefault = false;

            if (
                SpaPodCompare.CompareValue(alt2[0], min) >= 0
                && SpaPodCompare.CompareValue(alt2[0], max) <= 0
            )
            {
                foreach (SpaValue a1 in alt1)
                {
                    if (SpaPodCompare.CompareValue(a1, alt2[0]) != 0)
                        continue;
                    copied.Add(a1);
                    foundDefault = true;
                    break;
                }
            }

            foreach (SpaValue a1 in alt1)
            {
                int inRange = SpaPodCompare.IsInRange(a1, min, max, step);
                if (inRange < 0)
                    return null;
                if (inRange == 0)
                    continue;
                if (nCopied++ == 0 && !foundDefault)
                    copied.Add(a1);
                copied.Add(a1);
            }
        }
        else if (range1 && list2)
        {
            // filter.h 165-183: the filter's values that fall inside the candidate's range, in the
            // filter's order, which is how its preference wins.
            if (alt1.Length < (p1c == SpaChoiceType.Step ? 4 : 3))
                return null;
            SpaValue min = alt1[1],
                max = alt1[2];
            SpaValue? step = p1c == SpaChoiceType.Step ? alt1[3] : null;

            foreach (SpaValue a2 in alt2)
            {
                int inRange = SpaPodCompare.IsInRange(a2, min, max, step);
                if (inRange < 0)
                    return null;
                if (inRange == 0)
                    continue;
                if (nCopied++ == 0)
                    copied.Add(a2);
                copied.Add(a2);
            }
        }
        else if (range1 && range2)
        {
            // filter.h 185-223: the overlap of the two ranges. The default is the filter's if it
            // lands in the overlap, else the candidate's, else the overlap's minimum. The answer is
            // always a plain range: upstream writes default/min/max and marks it Range even when
            // either side was a Step.
            if (alt1.Length < 3 || alt2.Length < 3)
                return null;

            SpaValue min = SpaPodCompare.CompareValue(alt1[1], alt2[1]) < 0 ? alt2[1] : alt1[1];
            SpaValue max = SpaPodCompare.CompareValue(alt2[2], alt1[2]) < 0 ? alt2[2] : alt1[2];
            if (SpaPodCompare.CompareValue(max, min) < 0)
                return null;

            SpaValue def = alt2[0];
            int inRange = SpaPodCompare.IsInRange(def, min, max, null);
            if (inRange < 0)
                return null;
            if (inRange == 0)
            {
                def = alt1[0];
                inRange = SpaPodCompare.IsInRange(def, min, max, null);
                if (inRange < 0)
                    return null;
                if (inRange == 0)
                    def = min;
            }

            return new SpaChoice(SpaChoiceType.Range, childType, [def, min, max]);
        }
        else if (
            (p1c, p2c)
            is
                (SpaChoiceType.None, SpaChoiceType.Flags)
                or
                (SpaChoiceType.Flags, SpaChoiceType.None)
                or
                (SpaChoiceType.Flags, SpaChoiceType.Flags)
        )
        {
            // filter.h 225-232: the AND of the two flag sets; nothing in common is a refusal.
            return SpaPodCompare.AndFlags(alt1[0], alt2[0]) is { } flags
                ? new SpaChoice(SpaChoiceType.Flags, childType, [flags])
                : null;
        }
        else
        {
            // filter.h 233-244: flags against a range, a step or an enumeration is -ENOTSUP.
            return null;
        }

        // filter.h 246-257: how many values survived decides the shape of the answer.
        return nCopied switch
        {
            0 => null,
            1 => copied[0],
            _ => new SpaChoice(SpaChoiceType.Enum, childType, copied.ToImmutable()),
        };
    }

    /// <summary><c>spa_pod_get_values</c>: a value's alternatives, choice kind and child type.</summary>
    /// <remarks>A bare value is a choice of kind None with itself as the one alternative.</remarks>
    private static (SpaChoiceType Kind, ImmutableArray<SpaValue> Alts, SpaType ChildType) Values(
        SpaValue value
    ) =>
        value is SpaChoice choice
            ? (choice.Kind, choice.Alternatives, choice.ChildType)
            : (SpaChoiceType.None, [value], value.Type);
}
