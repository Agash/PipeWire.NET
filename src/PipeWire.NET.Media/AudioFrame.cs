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
    /// <param name="presentationTimeNs">Presentation timestamp in nanoseconds, or -1 if unavailable.</param>
    /// <param name="captureClockNs">Graph clock time (monotonic ns) of the capture cycle.</param>
    /// <param name="mediaClockNs">Media position (ns) at the cycle; -1 if unknown.</param>
    /// <param name="delayNs">Signal delay/latency (ns) from source to this stream.</param>
    public AudioFrame(
        ReadOnlySpan<byte> samples,
        int sampleRate,
        int channels,
        AudioSampleFormat format,
        ulong sequenceNumber,
        long presentationTimeNs = -1,
        long captureClockNs = -1,
        long mediaClockNs = -1,
        long delayNs = 0)
    {
        Samples            = samples;
        SampleRate         = sampleRate;
        Channels           = channels;
        Format             = format;
        SequenceNumber     = sequenceNumber;
        PresentationTimeNs = presentationTimeNs;
        CaptureClockNs     = captureClockNs;
        MediaClockNs       = mediaClockNs;
        DelayNs            = delayNs;
    }

    /// <summary>Raw interleaved sample bytes.</summary>
    public ReadOnlySpan<byte> Samples { get; }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Number of audio channels.</summary>
    public int Channels { get; }

    /// <summary>Sample format of <see cref="Samples"/>.</summary>
    public AudioSampleFormat Format { get; }

    /// <summary>Monotonically increasing chunk index for this stream session.</summary>
    public ulong SequenceNumber { get; }

    /// <summary>
    /// Content presentation timestamp in nanoseconds (from SPA_META_Header), or -1 if unavailable.
    /// </summary>
    /// <remarks>
    /// PipeWire audio does NOT carry a per-buffer header timestamp, so this is normally -1 for
    /// audio. For A/V synchronization use <see cref="CaptureClockNs"/> instead - it is the shared
    /// graph-clock time and is populated for audio and video alike.
    /// </remarks>
    public long PresentationTimeNs { get; }

    /// <summary>
    /// Graph clock time (monotonic ns) of the processing cycle that delivered this chunk, from
    /// <c>pw_stream_get_time</c>. Shared across all streams in the graph - this is the timestamp
    /// to align audio against video for A/V sync (audio has no per-buffer header PTS). -1 if unavailable.
    /// </summary>
    public long CaptureClockNs { get; }

    /// <summary>
    /// Media position (ns) of this stream at the capture cycle (<c>ticks*rate</c>) - a
    /// sample-accurate, monotonic media clock for this audio stream. -1 if unknown.
    /// </summary>
    public long MediaClockNs { get; }

    /// <summary>
    /// Signal delay (ns) from the source to this stream. The samples correspond to roughly
    /// <see cref="CaptureClockNs"/> - <see cref="DelayNs"/> on the shared clock - use for
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
