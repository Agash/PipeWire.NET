using System.Collections.Immutable;

namespace PipeWire.NET.Spa;

/// <summary>
/// Structural comparison for the collections a <see cref="SpaValue"/> carries.
/// </summary>
/// <remarks>
/// A record compares its members with <see cref="EqualityComparer{T}.Default"/>, and for
/// <see cref="ImmutableArray{T}"/> that compares the wrapped array by reference. Two pods with
/// identical contents would therefore be unequal, which is wrong for a value model and wrong in the
/// one place it matters most - comparing a parameter that was read back against the one that was
/// written.
/// </remarks>
internal static class SpaValueEquality
{
    /// <summary>Compares two arrays element by element, treating default and empty as equal.</summary>
    internal static bool SequenceEqual<T>(ImmutableArray<T> left, ImmutableArray<T>? right)
    {
        if (right is not { } other) return false;
        if (left.IsDefaultOrEmpty || other.IsDefaultOrEmpty)
            return left.IsDefaultOrEmpty && other.IsDefaultOrEmpty;
        if (left.Length != other.Length) return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], other[i]))
                return false;
        }

        return true;
    }

    /// <summary>A hash over an array's contents, matching <see cref="SequenceEqual"/>.</summary>
    /// <remarks>
    /// Only the first eight elements are mixed in. A pod can carry hundreds of values - a video
    /// format enumeration, a channel map - and hashing all of them would make every dictionary
    /// insert walk the whole array for no benefit; equality still compares in full.
    /// </remarks>
    internal static int Combine<T>(ImmutableArray<T> values)
    {
        if (values.IsDefaultOrEmpty) return 0;

        var hash = new HashCode();
        hash.Add(values.Length);
        for (int i = 0; i < values.Length && i < 8; i++)
            hash.Add(values[i]);

        return hash.ToHashCode();
    }
}
