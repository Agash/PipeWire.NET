namespace PipeWire.NET.Media;

/// <summary>
/// A chunk of audio samples delivered by <see cref="PipeWireAudioCapture.FrameReady"/>.
/// The sample span is only valid for the duration of the event handler.
/// </summary>
public readonly ref struct AudioFrame
{
    /// <param name="samples">Raw interleaved sample bytes for the current chunk.</param>
    /// <param name="sampleRate">Sample rate in Hz (e.g. 48000).</param>
    /// <param name="channels">Number of audio channels (e.g. 2 for stereo).</param>
    /// <param name="format">Sample format.</param>
    /// <param name="sequenceNumber">Monotonically increasing chunk index for this stream session.</param>
    /// <param name="presentationTimestampNs">The header <c>pts</c> in nanoseconds, or -1 when there is none.</param>
    /// <param name="graphTimeNs">Graph clock time (monotonic ns) of the capture cycle.</param>
    /// <param name="streamPositionNs">Media position (ns) at the cycle; null if unknown.</param>
    /// <param name="delayNs">Signal delay/latency (ns) from source to this stream.</param>
    /// <param name="queuedTimeNs">The cycle time the buffer was queued in (<c>pw_buffer.time</c>), or -1.</param>
    public AudioFrame(
        ReadOnlySpan<byte> samples,
        int sampleRate,
        int channels,
        AudioSampleFormat format,
        ulong sequenceNumber,
        long presentationTimestampNs = -1,
        long graphTimeNs = -1,
        long streamPositionNs = -1,
        long delayNs = 0,
        long queuedTimeNs = -1
    )
    {
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
        Format = format;
        SequenceNumber = sequenceNumber;
        PresentationTimestampNs = presentationTimestampNs < 0 ? null : presentationTimestampNs;
        QueuedTimeNs = queuedTimeNs < 0 ? null : queuedTimeNs;
        GraphTimeNs = graphTimeNs < 0 ? null : graphTimeNs;
        StreamPositionNs = streamPositionNs < 0 ? null : streamPositionNs;
        DelayNs = delayNs;
    }

    /// <summary>Raw interleaved sample bytes.</summary>
    public ReadOnlySpan<byte> Samples { get; }

    /// <summary>Copies the chunk so it can be kept past the handler that delivered it.</summary>
    /// <inheritdoc cref="OwnedAudioFrame" path="/remarks"/>
    public OwnedAudioFrame Clone() =>
        new(
            [.. Samples],
            SampleRate,
            Channels,
            Format,
            SequenceNumber,
            PresentationTimestampNs,
            QueuedTimeNs,
            GraphTimeNs,
            StreamPositionNs,
            DelayNs
        );

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Number of audio channels.</summary>
    public int Channels { get; }

    /// <summary>Sample format of <see cref="Samples"/>.</summary>
    public AudioSampleFormat Format { get; }

    /// <summary>Monotonically increasing chunk index for this stream session.</summary>
    public ulong SequenceNumber { get; }

    /// <summary>
    /// The producer's presentation timestamp for this chunk, in nanoseconds - the <c>pts</c> of the
    /// buffer's <c>SPA_META_Header</c> - or null when the buffer carries no header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the producer's own clock: this library's outputs and upstream's video-src stamp
    /// CLOCK_MONOTONIC, GStreamer's pipewiresink stamps its pipeline's running time. It survives only
    /// where nothing converts the media on the way. Video normally passes through unconverted, so
    /// this is what a video consumer aligns on, as upstream's video-play-sync does. Audio does not:
    /// no audio converter or mixer copies the header, so an audio consumer normally sees null here
    /// and uses <see cref="QueuedTimeNs"/> instead.
    /// </para>
    /// <para>
    /// Carried per buffer, so chunks that arrive in the same graph cycle still have their own.
    /// </para>
    /// </remarks>
    public long? PresentationTimestampNs { get; }

    /// <summary>
    /// The graph cycle time, in nanoseconds on CLOCK_MONOTONIC, at which this chunk's buffer was queued
    /// in the stream (<c>pw_buffer.time</c>), or null when the daemon did not say.
    /// </summary>
    /// <remarks>
    /// Upstream's own definition: "the cycle time in nanoseconds when this buffer was queued in the
    /// stream. It can be compared against the <c>pw_time</c> values or <c>pw_stream_get_nsec()</c>"
    /// (stream.h). The graph's time rather than the producer's, so it is what audio arrives with, and
    /// what upstream's pipewiresrc falls back to when there is no header (<c>b-&gt;time - delay</c>).
    /// </remarks>
    public long? QueuedTimeNs { get; }

    /// <summary>
    /// Graph clock time (CLOCK_MONOTONIC nanoseconds) of the processing cycle that delivered this
    /// chunk, from <c>pw_stream_get_time_n</c>; null if the graph offered none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per cycle, not per buffer: everything delivered in one cycle shares it, so a burst of
    /// buffers carries one value. For per-buffer time use <see cref="PresentationTimestampNs"/> or
    /// <see cref="QueuedTimeNs"/>.
    /// </para>
    /// <para>
    /// It only advances if the group's driver publishes a clock. Driver nodes always do (a sound
    /// card, a null sink, the dummy driver). A stream acting as driver has to write it itself:
    /// this library's outputs do, and GStreamer's pipewiresink does for audio but deliberately not
    /// for video, so a capture of a pipewiresink video source sees this frozen.
    /// </para>
    /// </remarks>
    public long? GraphTimeNs { get; }

    /// <summary>
    /// Media position (ns) of this stream at the capture cycle (<c>ticks*rate</c>) - a
    /// sample-accurate, monotonic media clock for this audio stream. null if unknown.
    /// </summary>
    public long? StreamPositionNs { get; }

    /// <summary>
    /// Signal delay (ns) from the source to this stream. The samples correspond to roughly
    /// <see cref="GraphTimeNs"/> - <see cref="DelayNs"/> on the shared clock - use for
    /// latency-compensated, sample-accurate timestamping.
    /// </summary>
    public long DelayNs { get; }

    /// <summary>Number of frames (samples per channel) in this chunk, or 0 if the format is unusable.</summary>
    /// <remarks>
    /// A frame arrives from a producer, so its channel count is an input rather than a fact. Zero
    /// channels is a divide by zero on a property, which throws from somewhere a caller has no
    /// reason to guard.
    /// </remarks>
    public int FrameCount
    {
        get
        {
            int stride = Channels * Format.BytesPerSample();
            return stride <= 0 ? 0 : Samples.Length / stride;
        }
    }
}
