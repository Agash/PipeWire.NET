using PipeWire.NET.Spa;

namespace PipeWire.NET.Graph;

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
            new SpaPodProperty((uint)SpaParamProcessLatency.Quantum, 0, new SpaFloat(Quantum)),
            new SpaPodProperty((uint)SpaParamProcessLatency.Rate, 0, new SpaInt(Rate)),
            new SpaPodProperty((uint)SpaParamProcessLatency.Ns, 0, new SpaLong(Ns)),
        ]);
}
