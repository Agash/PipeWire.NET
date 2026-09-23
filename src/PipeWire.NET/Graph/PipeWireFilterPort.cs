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

    /// <summary>The native port this wraps, as the filter's events identify it.</summary>
    internal void* PortData => _portData;
    private readonly PipeWireFilter _owner;
    private readonly PipeWireDspFormat _format;

    internal PipeWireFilterPort(
        PipeWireFilter owner,
        void* portData,
        PipeWirePortDirection direction,
        string name,
        PipeWireDspFormat format
    )
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
    /// Outside the callback there is no buffer for the cycle and this returns empty; after the
    /// filter is disposed it throws.
    /// </para>
    /// <para>
    /// An empty span is normal, not an error: a port with nothing connected to it, or one the graph
    /// skipped this cycle, has no buffer, and a buffer too small for the count asked for is not
    /// handed out either. Writing to it is simply not possible, so a filter has to
    /// check rather than assume.
    /// </para>
    /// <para>
    /// The span belongs to the cycle, not to the caller. Storing it and reading after the callback
    /// returns reads a buffer the graph has taken back.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The filter that owns this port has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The port carries MIDI/control sequences, not audio.</exception>
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
                $"port '{Name}' carries {_format}, not audio; GetSamples is audio-only."
            );

        return DspBuffer(sampleCount);
    }

    /// <summary>
    /// The cycle's pixels for a video port, as 32 bit float RGBA.
    /// </summary>
    /// <param name="width">The frame width, from the graph's position area.</param>
    /// <param name="height">The frame height, from the graph's position area.</param>
    /// <returns>
    /// Four floats per pixel, row-major and tightly packed, or an empty span when the graph gave
    /// this port no buffer for the cycle, or one too small for a frame of this size.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The video counterpart of <see cref="GetSamples"/>, and subject to the same rules: call it
    /// only from inside the process callback, and do not keep the span past it.
    /// </para>
    /// <para>
    /// The geometry is a parameter rather than a property because a filter port does not negotiate
    /// one. Upstream's <c>video-dsp-play</c> reads the size from the graph's position area each
    /// cycle for exactly this reason - the frame size belongs to the graph, not to the port.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The filter that owns this port has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The port does not carry video.</exception>
    public Span<float> GetPixels(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);

        if (_format is not PipeWireDspFormat.Rgba32FloatVideo)
            throw new InvalidOperationException(
                $"port '{Name}' carries {_format}, not video; GetPixels is video-only."
            );

        return DspBuffer(checked(width * height * 4));
    }

    /// <summary>
    /// <c>pw_filter_get_dsp_buffer</c>, bounded by the buffer it hands out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same steps as upstream's (filter.c): dequeue, take the first data, on an output port
    /// claim the chunk, queue it back, return the data. Done here rather than called, because
    /// upstream's returns a bare pointer and never compares the count with the buffer's
    /// <c>maxsize</c> - its callers are C, and size from the position area and live with it. A span
    /// built over that pointer for the asked-for count is a write past the buffer whenever the two
    /// disagree, and they do: the graph can publish a video size before the buffers it allocated for
    /// the old one are replaced. That wrote past a DSP video buffer and took the test host down with
    /// an access violation (lab box, 2026-09-15).
    /// </para>
    /// <para>
    /// A cycle whose buffer cannot hold what was asked for returns empty, which a caller already
    /// handles as "no buffer this cycle"; a partial frame or quantum is not one. An output port's
    /// buffer is still queued back, with an empty chunk, so it is not lost to the graph.
    /// </para>
    /// </remarks>
    private Span<float> DspBuffer(uint floats)
    {
        pw_buffer* buf = Interop.Native.pw_filter_dequeue_buffer(_portData);
        if (buf is null)
            return default;

        spa_buffer* sb = buf->buffer;
        if (sb is null || sb->n_datas == 0 || sb->datas is null || sb->datas[0].data is null)
        {
            _ = Interop.Native.pw_filter_queue_buffer(_portData, buf);
            return default;
        }

        spa_data* d = &sb->datas[0];
        ulong bytes = (ulong)floats * sizeof(float);
        bool fits = bytes <= d->maxsize;

        if (Direction == PipeWirePortDirection.Out && d->chunk is not null)
        {
            d->chunk->offset = 0;
            d->chunk->size = fits ? (uint)bytes : 0;
            d->chunk->stride = sizeof(float);
            d->chunk->flags = 0;
        }

        _ = Interop.Native.pw_filter_queue_buffer(_portData, buf);
        return fits ? new Span<float>(d->data, checked((int)floats)) : default;
    }

    /// <summary>
    /// Reads the timed events that arrived on a MIDI or control port this cycle.
    /// </summary>
    /// <returns>The sequence, or <see langword="null"/> when the port had no buffer this cycle.</returns>
    /// <remarks>
    /// <para>
    /// A sequence port's buffer holds a <c>spa_pod_sequence</c> of timed controls, not samples,
    /// which is why <see cref="GetSamples"/> refuses it. Each control carries an offset in frames
    /// from the start of the cycle, so a consumer knows not just what happened but when within the
    /// quantum - the difference between MIDI that is merely delivered and MIDI that is in time.
    /// </para>
    /// <para>
    /// Call only from inside the process callback.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The filter that owns this port has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The port does not carry a sequence.</exception>
    public Spa.SpaSequence? ReadEvents()
    {
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);
        RequireSequencePort(nameof(ReadEvents));

        pw_buffer* buf = Interop.Native.pw_filter_dequeue_buffer(_portData);
        if (buf is null)
            return null;

        try
        {
            spa_buffer* sb = buf->buffer;
            if (sb is null || sb->n_datas == 0 || sb->datas is null)
                return null;

            spa_data* d = &sb->datas[0];
            if (d->data is null || d->chunk is null)
                return null;

            uint size = d->chunk->size;
            if (size == 0 || size > d->maxsize)
                return null;

            var pod = new ReadOnlySpan<byte>((byte*)d->data + d->chunk->offset, checked((int)size));

            return
                Spa.SpaPod.TryParse(pod, out Spa.SpaValue? parsed) && parsed is Spa.SpaSequence seq
                ? seq
                : null;
        }
        finally
        {
            // Returned either way: a buffer dequeued and not queued back is one the pool never
            // sees again, and the port stalls a few cycles later with no error anywhere.
            _ = Interop.Native.pw_filter_queue_buffer(_portData, buf);
        }
    }

    /// <summary>
    /// Publishes timed events on a MIDI or control port for this cycle.
    /// </summary>
    /// <param name="events">The controls to emit, in ascending offset order.</param>
    /// <returns><see langword="false"/> when the port had no buffer to write into this cycle.</returns>
    /// <remarks>
    /// <para>
    /// The chunk size is set to the pod's own length rather than to a sample count, which is why
    /// this cannot go through the DSP-buffer helper: that helper assumes floats and would declare a
    /// size in samples, leaving the consumer to parse a sequence that claims the wrong length.
    /// </para>
    /// <para>
    /// Call only from inside the process callback.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The filter that owns this port has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The port does not carry a sequence.</exception>
    /// <exception cref="ArgumentException">The events do not fit the port's buffer.</exception>
    public bool WriteEvents(scoped ReadOnlySpan<Spa.SpaControl> events)
    {
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);
        RequireSequencePort(nameof(WriteEvents));

        pw_buffer* buf = Interop.Native.pw_filter_dequeue_buffer(_portData);
        if (buf is null)
            return false;

        try
        {
            spa_buffer* sb = buf->buffer;
            if (sb is null || sb->n_datas == 0 || sb->datas is null)
                return false;

            spa_data* d = &sb->datas[0];
            if (d->data is null || d->chunk is null || d->maxsize == 0)
                return false;

            var target = new Span<byte>(d->data, checked((int)d->maxsize));
            var sequence = new Spa.SpaSequence(0, [.. events]);

            if (!Spa.SpaPod.TryWrite(sequence, target, out int written))
            {
                throw new ArgumentException(
                    $"the events need {Spa.SpaPod.GetByteCount(sequence)} bytes, and the port's "
                        + $"buffer holds {d->maxsize}.",
                    nameof(events)
                );
            }

            d->chunk->offset = 0;
            d->chunk->size = (uint)written;
            d->chunk->stride = 1;
            d->chunk->flags = 0;
            return true;
        }
        finally
        {
            _ = Interop.Native.pw_filter_queue_buffer(_portData, buf);
        }
    }

    private void RequireSequencePort(string member)
    {
        if (_format is PipeWireDspFormat.Midi or PipeWireDspFormat.Control or PipeWireDspFormat.Ump)
            return;

        throw new InvalidOperationException(
            $"port '{Name}' carries {_format}, not a sequence; {member} is for MIDI and control ports."
        );
    }
}
