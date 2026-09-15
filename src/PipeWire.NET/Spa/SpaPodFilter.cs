namespace PipeWire.NET.Spa;

/// <summary>
/// Whether a parameter survives an enumeration filter: upstream <c>spa_pod_filter</c> asked as a
/// yes-or-no question.
/// </summary>
/// <remarks>
/// <para>
/// A serving implementation that only needs to decide whether to offer a parameter asks this; one
/// that has to answer with the narrowed parameter uses <see cref="SpaPodProjection"/>. Both are the
/// same computation. This class used to carry its own copy of the matching rules, which drifted from
/// the projection's - the projection had come to prefer the candidate's values where upstream prefers
/// the filter's, and to accept flags against an enumeration where upstream refuses - and two notions
/// of one rule is how a node ends up offering a parameter it then cannot narrow. So a match is now
/// defined as "the projection produced something", and there is nothing left here to drift.
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
