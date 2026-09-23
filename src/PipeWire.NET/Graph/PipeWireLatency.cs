using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

/// <summary>The latency a port reports for one direction, as the graph has settled it.</summary>
/// <remarks>
/// Three units for the same quantity, and they are not interchangeable. The nanosecond figures are
/// the only ones meaningful on their own; the quantum and rate figures are relative to the graph's
/// current quantum and sample rate, so they change meaning when the graph reconfigures. A caller
/// showing a number to a person wants <see cref="MinNs"/> and <see cref="MaxNs"/>.
/// <para>
/// Reported per direction, so the latency of a path is the sum along it rather than any single
/// port's figure.
/// </para>
/// </remarks>
/// <param name="Direction">Which side of the port this describes.</param>
/// <param name="MinQuantum">Minimum latency as a fraction of the graph quantum.</param>
/// <param name="MaxQuantum">Maximum latency as a fraction of the graph quantum.</param>
/// <param name="MinRate">Minimum latency in samples at the graph rate.</param>
/// <param name="MaxRate">Maximum latency in samples at the graph rate.</param>
/// <param name="MinNs">Minimum latency in nanoseconds.</param>
/// <param name="MaxNs">Maximum latency in nanoseconds.</param>
public sealed record PipeWireLatency(
    SpaDirection Direction,
    float MinQuantum,
    float MaxQuantum,
    int MinRate,
    int MaxRate,
    long MinNs,
    long MaxNs
)
{
    /// <summary>Reads one out of a <c>SPA_PARAM_Latency</c> object, or null if it is not one.</summary>
    /// <remarks>
    /// Absent members read as zero rather than refusing the object. A producer sends the units it
    /// knows and leaves the rest out, so requiring all seven would reject most real pods; zero is
    /// also what the C helpers leave in an uninitialised <c>spa_latency_info</c>.
    /// </remarks>
    public static PipeWireLatency? From(SpaObject? param)
    {
        if (param is null || param.ObjectType != SpaType.ObjectParamLatency)
            return null;

        return new PipeWireLatency(
            (SpaDirection)Id(param, SpaParamLatency.Direction),
            Float(param, SpaParamLatency.MinQuantum),
            Float(param, SpaParamLatency.MaxQuantum),
            Int(param, SpaParamLatency.MinRate),
            Int(param, SpaParamLatency.MaxRate),
            Long(param, SpaParamLatency.MinNs),
            Long(param, SpaParamLatency.MaxNs)
        );
    }

    /// <summary>This latency as the parameter object the daemon expects.</summary>
    public SpaObject ToParameter() =>
        new(
            SpaType.ObjectParamLatency,
            SpaParamType.Latency,
            [
                new SpaPodProperty((uint)SpaParamLatency.Direction, 0, new SpaId((uint)Direction)),
                new SpaPodProperty((uint)SpaParamLatency.MinQuantum, 0, new SpaFloat(MinQuantum)),
                new SpaPodProperty((uint)SpaParamLatency.MaxQuantum, 0, new SpaFloat(MaxQuantum)),
                new SpaPodProperty((uint)SpaParamLatency.MinRate, 0, new SpaInt(MinRate)),
                new SpaPodProperty((uint)SpaParamLatency.MaxRate, 0, new SpaInt(MaxRate)),
                new SpaPodProperty((uint)SpaParamLatency.MinNs, 0, new SpaLong(MinNs)),
                new SpaPodProperty((uint)SpaParamLatency.MaxNs, 0, new SpaLong(MaxNs)),
            ]
        );

    private static uint Id(SpaObject o, SpaParamLatency key) =>
        o[(uint)key] is SpaId v ? v.Value : 0;

    private static float Float(SpaObject o, SpaParamLatency key) =>
        o[(uint)key] is SpaFloat v ? v.Value : 0f;

    private static int Int(SpaObject o, SpaParamLatency key) =>
        o[(uint)key] is SpaInt v ? v.Value : 0;

    private static long Long(SpaObject o, SpaParamLatency key) =>
        o[(uint)key] is SpaLong v ? v.Value : 0L;
}
