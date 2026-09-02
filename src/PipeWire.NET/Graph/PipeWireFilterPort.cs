using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// One port of a <see cref="PipeWireFilter"/>: a mono channel of DSP audio in or out.
/// </summary>
/// <remarks>
/// <para>
/// A filter port carries one channel. Stereo is two ports, not one port of interleaved pairs, which
/// is what makes a filter graph routable per channel.
/// </para>
/// <para>
/// The buffer is only valid inside the process callback that handed it out, and only for the sample
/// count that callback was given. Keeping the span past that point reads memory the graph has moved
/// on from.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class PipeWireFilterPort
{
    private readonly void* _portData;
    private readonly PipeWireFilter _owner;

    internal PipeWireFilterPort(PipeWireFilter owner, void* portData, PipeWirePortDirection direction, string name)
    {
        _owner = owner;
        _portData = portData;
        Direction = direction;
        Name = name;
    }

    /// <summary>Which way audio moves through this port.</summary>
    public PipeWirePortDirection Direction { get; }

    /// <summary>The port name, as it appears in the graph.</summary>
    public string Name { get; }

    /// <summary>
    /// This port's samples for the current cycle, or an empty span when the graph gave it none.
    /// </summary>
    /// <param name="sampleCount">
    /// The cycle's sample count, as handed to the process callback. Asking for more than the cycle
    /// holds is what makes a filter read past its buffer.
    /// </param>
    /// <remarks>
    /// <para>
    /// Call only from inside the process callback. An input port's span holds what arrived; an
    /// output port's is where the result goes, and leaving it untouched emits whatever was there.
    /// </para>
    /// <para>
    /// An empty span is normal, not an error: a port with nothing connected to it, or one the graph
    /// skipped this cycle, has no buffer. Writing to it is simply not possible, so a filter has to
    /// check rather than assume.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The filter that owns this port has been disposed.</exception>
    public Span<float> GetSamples(uint sampleCount)
    {
        // The port data belongs to the filter and dies with it. Without this the pointer is simply
        // read after free, which is a corrupt buffer or a crash rather than an exception.
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);

        void* buffer = Interop.Native.pw_filter_get_dsp_buffer(_portData, sampleCount);
        return buffer is null ? default : new Span<float>(buffer, (int)sampleCount);
    }
}
