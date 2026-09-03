using System.Collections.Immutable;
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
    long MaxNs)
{
    /// <summary>Reads one out of a <c>SPA_PARAM_Latency</c> object, or null if it is not one.</summary>
    /// <remarks>
    /// Absent members read as zero rather than refusing the object. A producer sends the units it
    /// knows and leaves the rest out, so requiring all seven would reject most real pods; zero is
    /// also what the C helpers leave in an uninitialised <c>spa_latency_info</c>.
    /// </remarks>
    public static PipeWireLatency? From(SpaObject? param)
    {
        if (param is null || param.ObjectType != SpaType.ObjectParamLatency) return null;

        return new PipeWireLatency(
            (SpaDirection)Id(param, SpaParamLatency.Direction),
            Float(param, SpaParamLatency.MinQuantum),
            Float(param, SpaParamLatency.MaxQuantum),
            Int(param, SpaParamLatency.MinRate),
            Int(param, SpaParamLatency.MaxRate),
            Long(param, SpaParamLatency.MinNs),
            Long(param, SpaParamLatency.MaxNs));
    }

    /// <summary>This latency as the parameter object the daemon expects.</summary>
    public SpaObject ToParameter() =>
        new(SpaType.ObjectParamLatency, SpaParamType.Latency,
        [
            new SpaProperty((uint)SpaParamLatency.Direction, 0, new SpaId((uint)Direction)),
            new SpaProperty((uint)SpaParamLatency.MinQuantum, 0, new SpaFloat(MinQuantum)),
            new SpaProperty((uint)SpaParamLatency.MaxQuantum, 0, new SpaFloat(MaxQuantum)),
            new SpaProperty((uint)SpaParamLatency.MinRate, 0, new SpaInt(MinRate)),
            new SpaProperty((uint)SpaParamLatency.MaxRate, 0, new SpaInt(MaxRate)),
            new SpaProperty((uint)SpaParamLatency.MinNs, 0, new SpaLong(MinNs)),
            new SpaProperty((uint)SpaParamLatency.MaxNs, 0, new SpaLong(MaxNs)),
        ]);

    private static uint Id(SpaObject o, SpaParamLatency key) =>
        o[(uint)key] is SpaId v ? v.Value : 0;

    private static float Float(SpaObject o, SpaParamLatency key) =>
        o[(uint)key] is SpaFloat v ? v.Value : 0f;

    private static int Int(SpaObject o, SpaParamLatency key) =>
        o[(uint)key] is SpaInt v ? v.Value : 0;

    private static long Long(SpaObject o, SpaParamLatency key) =>
        o[(uint)key] is SpaLong v ? v.Value : 0L;
}

/// <summary>The latency a node adds by processing, which it declares rather than discovers.</summary>
/// <remarks>
/// This is the one an application is expected to set. Everything downstream computes its own figures
/// from what each node claims, so a node that filters, buffers or looks ahead and does not say so
/// makes every latency figure past it wrong by exactly that amount, silently and with nothing in the
/// graph to indicate it.
/// <para>
/// The three units are alternatives, not components: set the one the processing is naturally
/// expressed in and leave the others zero. A fixed algorithmic delay is <see cref="Ns"/>; a
/// look-ahead of a whole buffer is <see cref="Quantum"/> 1; a filter with a known tap count is
/// <see cref="Rate"/>.
/// </para>
/// </remarks>
/// <param name="Quantum">Latency as a fraction of the graph quantum.</param>
/// <param name="Rate">Latency in samples at the graph rate.</param>
/// <param name="Ns">Latency in nanoseconds.</param>
public sealed record PipeWireProcessLatency(float Quantum = 0f, int Rate = 0, long Ns = 0L)
{
    /// <summary>Reads one out of a <c>SPA_PARAM_ProcessLatency</c> object, or null if it is not one.</summary>
    public static PipeWireProcessLatency? From(SpaObject? param)
    {
        if (param is null || param.ObjectType != SpaType.ObjectParamProcessLatency) return null;

        return new PipeWireProcessLatency(
            param[(uint)SpaParamProcessLatency.Quantum] is SpaFloat q ? q.Value : 0f,
            param[(uint)SpaParamProcessLatency.Rate] is SpaInt r ? r.Value : 0,
            param[(uint)SpaParamProcessLatency.Ns] is SpaLong n ? n.Value : 0L);
    }

    /// <summary>This latency as the parameter object the daemon expects.</summary>
    public SpaObject ToParameter() =>
        new(SpaType.ObjectParamProcessLatency, SpaParamType.ProcessLatency,
        [
            new SpaProperty((uint)SpaParamProcessLatency.Quantum, 0, new SpaFloat(Quantum)),
            new SpaProperty((uint)SpaParamProcessLatency.Rate, 0, new SpaInt(Rate)),
            new SpaProperty((uint)SpaParamProcessLatency.Ns, 0, new SpaLong(Ns)),
        ]);
}

/// <summary>Metadata travelling with a stream, per direction.</summary>
/// <remarks>
/// A tag is how a producer says what the audio is - track title, station name, the media role - to
/// whatever is downstream, without any of it being part of the format. It rides the graph alongside
/// the data rather than through a side channel, so it survives being routed.
/// </remarks>
/// <param name="Direction">Which side of the object this describes.</param>
/// <param name="Info">The key and value pairs, in the order the producer wrote them.</param>
public sealed record PipeWireTag(
    SpaDirection Direction,
    ImmutableArray<KeyValuePair<string, string>> Info)
{
    /// <summary>Reads one out of a <c>SPA_PARAM_Tag</c> object, or null if it is not one.</summary>
    /// <remarks>
    /// The info is a Struct of a count followed by that many key and value strings
    /// (<c>spa/param/tag.h:23-27</c>). The count is the producer's word, so the pairs actually
    /// present are what is read; a count disagreeing with them is ignored rather than trusted.
    /// </remarks>
    public static PipeWireTag? From(SpaObject? param)
    {
        if (param is null || param.ObjectType != SpaType.ObjectParamTag) return null;

        var pairs = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();

        if (param[(uint)SpaParamTag.Info] is SpaStruct info)
        {
            // Skips the leading count and reads pairs until they run out.
            int start = !info.Fields.IsDefaultOrEmpty && info.Fields[0] is SpaInt ? 1 : 0;
            for (int i = start; i + 1 < info.Fields.Length; i += 2)
            {
                if (info.Fields[i] is SpaString key && info.Fields[i + 1] is SpaString value)
                    pairs.Add(new KeyValuePair<string, string>(key.Value, value.Value));
            }
        }

        return new PipeWireTag(
            (SpaDirection)(param[(uint)SpaParamTag.Direction] is SpaId d ? d.Value : 0),
            pairs.ToImmutable());
    }

    /// <summary>This tag as the parameter object the daemon expects.</summary>
    public SpaObject ToParameter()
    {
        var fields = ImmutableArray.CreateBuilder<SpaValue>((Info.Length * 2) + 1);
        fields.Add(new SpaInt(Info.Length));
        foreach (KeyValuePair<string, string> pair in Info)
        {
            fields.Add(new SpaString(pair.Key));
            fields.Add(new SpaString(pair.Value));
        }

        return new SpaObject(SpaType.ObjectParamTag, SpaParamType.Tag,
        [
            new SpaProperty((uint)SpaParamTag.Direction, 0, new SpaId((uint)Direction)),
            new SpaProperty((uint)SpaParamTag.Info, 0, new SpaStruct(fields.MoveToImmutable())),
        ]);
    }
}
