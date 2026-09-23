using System.Collections.Immutable;

namespace PipeWire.NET.Spa;

/// <summary>
/// Value comparison over the managed pod model, transcribed from upstream <c>spa/pod/compare.h</c>.
/// </summary>
/// <remarks>
/// <para>
/// Format negotiation is decided by these few rules - which of two values is smaller, whether one is
/// inside a range, whether it is a whole number of steps - and getting one wrong does not throw: two
/// sides agree on a format one of them cannot carry, and the fault surfaces much later as a stream
/// that connects and then delivers nothing. So each function here keeps its upstream name in its
/// summary and its upstream behaviour in its body, including the corners that look arbitrary
/// (rectangles order by area first, a step tests <c>value % step</c> rather than distance from the
/// minimum, an enumeration's default does not count as one of its members).
/// </para>
/// <para>
/// The C functions compare raw bytes of one declared pod type. Here the two sides are typed values,
/// so "different types" is decided by <see cref="SameType"/> before any comparison, which is where
/// upstream's callers check <c>type</c> and <c>size</c> first.
/// </para>
/// </remarks>
internal static class SpaPodCompare
{
    /// <summary>Whether two values are of one pod type and so can be compared at all.</summary>
    internal static bool SameType(SpaValue a, SpaValue b) => a.GetType() == b.GetType();

    /// <summary><c>spa_pod_compare_value</c>: the ordering of two values of one type.</summary>
    /// <remarks>
    /// Rectangles order by area and then by width; fractions cross-multiply, so 1/2 equals 2/4;
    /// booleans compare as truth values. Anything without a scalar ordering - objects, structs,
    /// arrays - is compared the way upstream's default arm compares it, by its bytes, which can
    /// only say equal or not.
    /// </remarks>
    internal static int CompareValue(SpaValue a, SpaValue b) =>
        (a, b) switch
        {
            (SpaBool x, SpaBool y) => (x.Value ? 1 : 0).CompareTo(y.Value ? 1 : 0),
            (SpaId x, SpaId y) => x.Value.CompareTo(y.Value),
            (SpaInt x, SpaInt y) => x.Value.CompareTo(y.Value),
            (SpaLong x, SpaLong y) => x.Value.CompareTo(y.Value),
            (SpaFloat x, SpaFloat y) => x.Value.CompareTo(y.Value),
            (SpaDouble x, SpaDouble y) => x.Value.CompareTo(y.Value),
            (SpaString x, SpaString y) => Math.Sign(string.CompareOrdinal(x.Value, y.Value)),
            (SpaRectangle x, SpaRectangle y) => CompareRectangle(x, y),
            (SpaFraction x, SpaFraction y) => ((ulong)x.Numerator * y.Denominator).CompareTo(
                (ulong)y.Numerator * x.Denominator
            ),
            _ => BytesEqual(a, b) ? 0 : 1,
        };

    private static int CompareRectangle(SpaRectangle x, SpaRectangle y)
    {
        ulong a1 = (ulong)x.Width * x.Height,
            a2 = (ulong)y.Width * y.Height;
        if (a1 != a2)
            return a1 < a2 ? -1 : 1;
        return x.Width.CompareTo(y.Width);
    }

    /// <summary>Upstream's <c>memcmp</c> arm: equal exactly when the encoded pods are equal.</summary>
    private static bool BytesEqual(SpaValue a, SpaValue b)
    {
        Span<byte> left = stackalloc byte[4096];
        Span<byte> right = stackalloc byte[4096];
        return SpaPod.TryWrite(a, left, out int la)
            && SpaPod.TryWrite(b, right, out int lb)
            && left[..la].SequenceEqual(right[..lb]);
    }

    /// <summary><c>spa_pod_compare_is_step_of</c>: 1 when a whole number of steps, 0 when not,
    /// -1 when the step itself is invalid (below 1) or the type cannot step.</summary>
    internal static int IsStepOf(SpaValue value, SpaValue step) =>
        (value, step) switch
        {
            (SpaInt v, SpaInt s) => s.Value < 1 ? -1 : (v.Value % s.Value == 0 ? 1 : 0),
            (SpaLong v, SpaLong s) => s.Value < 1 ? -1 : (v.Value % s.Value == 0 ? 1 : 0),
            (SpaRectangle v, SpaRectangle s) => s.Width < 1 || s.Height < 1
                ? -1
                : (v.Width % s.Width == 0 && v.Height % s.Height == 0 ? 1 : 0),
            _ => -1,
        };

    /// <summary><c>spa_pod_compare_is_in_range</c>: 1 inside, 0 outside, -1 on an invalid step.</summary>
    internal static int IsInRange(SpaValue value, SpaValue min, SpaValue max, SpaValue? step)
    {
        if (CompareValue(value, min) < 0 || CompareValue(value, max) > 0)
            return 0;
        return step is null ? 1 : IsStepOf(value, step);
    }

    /// <summary><c>spa_pod_compare_is_valid_choice</c>: whether <paramref name="value"/> is one the
    /// choice allows.</summary>
    /// <remarks>
    /// An enumeration is tested against its members only, from index 1: index 0 is the default, and
    /// upstream does not count a default that is not also listed. Flags accept anything.
    /// </remarks>
    internal static bool IsValidChoice(
        SpaValue value,
        ImmutableArray<SpaValue> alts,
        SpaChoiceType kind
    )
    {
        switch (kind)
        {
            case SpaChoiceType.None:
                return alts.Length >= 1 && CompareValue(value, alts[0]) == 0;

            case SpaChoiceType.Enum:
                for (var i = 1; i < alts.Length; i++)
                    if (CompareValue(value, alts[i]) == 0)
                        return true;
                return false;

            case SpaChoiceType.Range:
                return alts.Length >= 3 && IsInRange(value, alts[1], alts[2], null) == 1;

            case SpaChoiceType.Step:
                return alts.Length >= 4 && IsInRange(value, alts[1], alts[2], alts[3]) == 1;

            case SpaChoiceType.Flags:
                return true;

            default:
                return false;
        }
    }

    /// <summary><c>spa_pod_filter_flags_value</c>: the bitwise AND of two flag values, or null when
    /// it is zero or the type cannot hold flags.</summary>
    internal static SpaValue? AndFlags(SpaValue a, SpaValue b) =>
        (a, b) switch
        {
            (SpaInt x, SpaInt y) when (x.Value & y.Value) != 0 => new SpaInt(x.Value & y.Value),
            (SpaLong x, SpaLong y) when (x.Value & y.Value) != 0 => new SpaLong(x.Value & y.Value),
            _ => null,
        };
}
