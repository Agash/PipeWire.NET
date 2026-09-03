using System.Runtime.Versioning;

namespace PipeWire.NET.Graph;

/// <summary>
/// One port of a <see cref="PipeWireFilter"/>: a mono channel of DSP audio in or out,
/// or a MIDI/control sequence port (see <see cref="Format"/>).
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
    private readonly PipeWireDspFormat _format;

    internal PipeWireFilterPort(
        PipeWireFilter owner, void* portData, PipeWirePortDirection direction, string name,
        PipeWireDspFormat format)
    {
        _owner = owner;
        _portData = portData;
        Direction = direction;
        Name = name;
        _format = format;
    }

    /// <summary>Which way audio moves through this port.</summary>
    public PipeWirePortDirection Direction { get; }

    /// <summary>The port name, as it appears in the graph.</summary>
    public string Name { get; }

    /// <summary>What this port carries: audio samples, or MIDI/control sequences.</summary>
    public PipeWireDspFormat Format => _format;

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
    /// <exception cref="InvalidOperationException">The port carries MIDI/control sequences, not audio.</exception>
    /// <remarks>
    /// <para>
    /// Valid only inside the owning filter's process callback, and only while that filter is alive.
    /// Outside the callback there is no buffer for the cycle and this returns empty; after the
    /// filter is disposed it throws.
    /// </para>
    /// <para>
    /// The span belongs to the cycle, not to the caller. Storing it and reading after the callback
    /// returns reads a buffer the graph has taken back.
    /// </para>
    /// </remarks>
    public Span<float> GetSamples(uint sampleCount)
    {
        // The port data belongs to the filter and dies with it. Without this the pointer is simply
        // read after free, which is a corrupt buffer or a crash rather than an exception.
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);

        // A MIDI/control port's buffer holds a sequence pod, not floats. Reinterpreting it as
        // samples hands back garbage with a valid-looking type, which is worse than refusing:
        // sequences get a typed accessor of their own when the sequence transport lands.
        if (_format is not PipeWireDspFormat.MonoAudio)
            throw new InvalidOperationException(
                $"port '{Name}' carries {_format}, not audio; GetSamples is audio-only.");

        void* buffer = Interop.Native.pw_filter_get_dsp_buffer(_portData, sampleCount);
        return buffer is null ? default : new Span<float>(buffer, checked((int)sampleCount));
    }
}
