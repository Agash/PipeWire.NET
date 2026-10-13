namespace PipeWire.NET.Spa;

/// <summary>
/// Whether a parameter survives an enumeration filter: upstream <c>spa_pod_filter</c> asked as a
/// yes-or-no question.
/// </summary>
/// <remarks>
/// <para>
/// A serving implementation that only needs to decide whether to offer a parameter asks this; one
/// that has to answer with the narrowed parameter uses <see cref="SpaPodProjection"/>. Both are the
/// same computation. A match is defined as "the projection produced something", so the two cannot
/// disagree: separate matching rules would let a node offer a parameter that it then cannot narrow.
/// </para>
/// </remarks>
internal static class SpaPodFilter
{
    /// <summary>Whether a served parameter object satisfies a filter object.</summary>
    internal static bool Matches(SpaObject candidate, SpaObject filter) =>
        SpaPodProjection.Project(candidate, filter) is not null;

    /// <summary>Whether a candidate value survives a filter value of the same property.</summary>
    internal static bool ValuesMatch(SpaValue candidate, SpaValue filter) =>
        SpaPodProjection.FilterValues(candidate, filter) is not null;
}
