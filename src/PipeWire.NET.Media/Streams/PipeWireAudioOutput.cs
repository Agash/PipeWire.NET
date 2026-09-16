using System.Collections.Immutable;
using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media.Streams;

/// <summary>
/// Publishes audio samples TO PipeWire (virtual source / playback). PipeWire pulls
/// samples by invoking <see cref="FillSamples"/>; write PCM into the supplied span.
/// </summary>
/// <remarks>
/// <b>Dispose it; do not let it fall out of scope.</b> The callbacks the daemon holds refer back
/// here through a weak handle, so an instance the application drops is collected and simply stops
/// delivering, with no error and no final state change. That is the deliberate half of the trade:
/// a strong handle would keep every one ever made alive for the life of the process. What it costs
/// is that the garbage collector cannot be the thing that closes one, because by the time it runs
/// there is nothing left to close it from.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class PipeWireAudioOutput : IAsyncDisposable
{
    /// <summary>Signature for <see cref="FillSamples"/>. Return the number of bytes written.</summary>
    /// <remarks>
    /// Zero is an <em>empty</em> buffer, not a silent one - the consumer sees an underrun, which is
    /// not the same thing as hearing nothing. To publish silence, clear the span and return its
    /// full length. A handler that throws also publishes an empty buffer rather than whatever the
    /// previous cycle left in it.
    /// </remarks>
    public delegate int FillSamplesHandler(
        PipeWireAudioOutput sender, Span<byte> samples, int sampleRate, int channels, AudioSampleFormat format);

    /// <summary>Invoked on the loop thread when a buffer is ready to fill.</summary>
    public event FillSamplesHandler? FillSamples;

    /// <summary>Raised on the loop thread when the connection state changes.</summary>
    public event Action<PipeWireAudioOutput, PipeWireStreamState, PipeWireStreamState>? StateChanged;

    private readonly PipeWireContext _ctx;
    private readonly string _name;
    private readonly int _sampleRate, _channels;
    private readonly AudioSampleFormat _format;
    private PipeWireStreamCore? _core;

    // The negotiated format, not the offered one. The daemon can renegotiate (rate or channel
    // changes on the route), and filling at the offered values after that writes the wrong frame
    // shape. Swapped whole: a multi-field struct written on the loop thread and read on the data
    // thread has no atomic assignment.
    private sealed class NegotiatedFormat(SpaFormatPod.AudioFormatInfo info)
    {
        public SpaFormatPod.AudioFormatInfo Info { get; } = info;
    }

    private NegotiatedFormat _fmtCell = new(new SpaFormatPod.AudioFormatInfo(AudioSampleFormat.F32Le, 48000, 2));

    private SpaFormatPod.AudioFormatInfo Negotiated => Volatile.Read(ref _fmtCell).Info;

    /// <param name="context">A started <see cref="PipeWireContext"/>.</param>
    /// <param name="nodeName">Name visible to consumers.</param>
    /// <param name="sampleRate">Sample rate to publish (Hz).</param>
    /// <param name="channels">Channel count.</param>
    /// <param name="format">Sample format.</param>
    public PipeWireAudioOutput(PipeWireContext context, string nodeName,
        int sampleRate = 48000, int channels = 2, AudioSampleFormat format = AudioSampleFormat.F32Le)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(nodeName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        _ctx = context; _name = nodeName;
        _sampleRate = sampleRate; _channels = channels; _format = format;
        _fmtCell = new NegotiatedFormat(new SpaFormatPod.AudioFormatInfo(format, sampleRate, channels));
    }

    /// <summary>Any node - let the session manager choose where this stream is routed.</summary>
    public const uint AnyNode = NativeConstants.PW_ID_ANY;

    /// <summary>Starts publishing.</summary>
    /// <param name="targetNodeId">
    /// The node to route into, or <see cref="AnyNode"/> to let the session manager decide.
    /// </param>
    /// <param name="targetObjectName">
    /// Optional <c>target.object</c> - bind to a specific node by name or serial regardless of
    /// the session manager's default-device routing.
    /// </param>
    /// <param name="autoConnect">
    /// When true the session manager routes this stream automatically, which for a playback stream
    /// means the current default sink, that is the speakers. Pass <see langword="false"/> to publish
    /// the node and leave it unrouted, so a caller can link it deliberately. A test or a transport
    /// usually wants that; a media player does not.
    /// </param>
    /// <param name="driver">
    /// Ask to be the graph's driver (<c>PW_STREAM_FLAG_DRIVER</c>), so cycles happen when this stream
    /// triggers them rather than on another node's clock - upstream's video-src and audio-src connect
    /// this way to pace their own output. Pair with <see cref="DriveAt"/>. The daemon still decides:
    /// <see cref="IsDriving"/> says whether it did. Without it the stream is only ever a follower.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the wait for the loop lock. The connect request itself is issued
    /// synchronously once that is held, so there is nothing to recall after it.
    /// </param>
    public unsafe void Connect(
        uint targetNodeId = AnyNode,
        string? targetObjectName = null,
        bool autoConnect = true,
        bool driver = false,
        CancellationToken cancellationToken = default)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");

        var props = new StreamProperties(StreamMediaType.Audio, StreamCategory.Playback)
            .WithRole("Music")
            .WithNodeName(_name);

        // After the stream's own defaults, so an explicit media.role or node.description from the
        // caller wins rather than being silently ignored. An explicit method argument still beats
        // both, which is why target.object is applied below this.
        if (ExtraProperties is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> kv in ExtraProperties)
                props.With(kv.Key, kv.Value);
        }

        if (targetObjectName is not null) props.WithTargetObject(targetObjectName);
        // Built locally and only published once the connect succeeded. Assigning the field first
        // leaves a failed connect behind a stream that reports itself already connected and can
        // never be retried.

        var core = new PipeWireStreamCore(_ctx, props, _name, OnBuffer, OnState, OnFormat);

        PipeWireStreamFlags flags = PipeWireStreamFlags.MapBuffers;
        if (autoConnect) flags |= PipeWireStreamFlags.Autoconnect;
        if (driver) flags |= PipeWireStreamFlags.Driver;

        Span<byte> pod = stackalloc byte[256];
        int len = SpaFormatPod.WriteAudioFormat(pod, _format, _sampleRate, _channels);
        try
        {
            core.Connect(SpaDirection.Output, targetNodeId, flags, pod[..len],
                cancellationToken: cancellationToken);
            _core = core;

            // Set here, not when a handler subscribes: a caller that subscribed before connecting
            // would otherwise never be hooked up, and the events it was waiting for would pass silently.
            core.OnCommand = RaiseCommand;
        }
        catch
        {
            core.Dispose();
            throw;
        }

    }

    /// <summary>
    /// This stream's own node in the graph, or <see langword="null"/> until it is connected.
    /// </summary>
    /// <remarks>
    /// A stream is a node like any other, so this is the handle for routing it:
    /// <c>graph.GetPortsForNode(await stream.WaitForNodeIdAsync())</c> finds its ports, which can then
    /// be linked. Awaited rather than read, because the id only exists once the daemon has bound the
    /// stream, which is after <c>Connect</c> returns.
    /// </remarks>
    public uint? NodeId
    {
        get
        {
            uint id = _core?.NodeId ?? NativeConstants.PW_ID_ANY;
            return id == NativeConstants.PW_ID_ANY ? null : id;
        }
    }


    /// <summary>Whether the daemon has made this stream the graph's driver.</summary>
    public bool IsDriving => _core?.IsDriving ?? false;

    /// <summary>
    /// Paces the graph from the stream's own data loop, driving one cycle per interval.
    /// </summary>
    /// <param name="interval">The period. <see cref="TimeSpan.Zero"/> stops the pacing.</param>
    /// <returns>True when the timer was armed or disarmed as asked.</returns>
    /// <remarks>
    /// Only meaningful for a stream the daemon has made the driver - see <see cref="IsDriving"/>.
    /// The timer fires on the data loop, so the cycle it triggers costs no thread hop; driving from
    /// a caller's own thread works too, but paces against that thread's scheduling instead.
    /// </remarks>
    public bool DriveAt(TimeSpan interval) => _core?.DriveAt(interval) ?? false;

    /// <summary>
    /// Extra node properties to set when connecting, on top of the ones this stream sets itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Session managers, routing rules and anything showing the user a list of streams read these,
    /// so the tags are how a stream explains itself to the rest of the system. <c>pw-cat</c> sets
    /// <c>media.title</c>, <c>media.artist</c> and the rest for exactly that reason.
    /// </para>
    /// <para>
    /// Applied at connect, and a key set here wins over this stream's own default for that key -
    /// which is the point, since overriding <c>media.role</c> or <c>node.description</c> is the
    /// usual reason to reach for this. To change a tag on a stream that is already running, use
    /// <c>UpdateProperties</c> instead; some keys, <c>target.object</c> among them, are only read
    /// when the connection is made.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string>? ExtraProperties { get; set; }

    /// <summary>How much this stream currently holds, or null when it cannot be read.</summary>
    /// <remarks>
    /// The error term for a rate controller. Pair it with <see cref="PipeWireRateController"/> to
    /// close the loop the way upstream's tunnels do.
    /// </remarks>
    public PipeWireStreamQueue? Queue => _core?.Queue;

    /// <summary>Whether the daemon has put this stream in lazy scheduling.</summary>
    public bool IsLazy => _core?.IsLazy ?? false;

    /// <summary>Adds or replaces properties on the live stream.</summary>
    /// <param name="properties">The keys to set. An empty value removes a key.</param>
    /// <returns>The number of properties actually changed.</returns>
    public int UpdateProperties(IReadOnlyDictionary<string, string> properties) =>
        _core?.UpdateProperties(properties) ?? 0;

    /// <summary>Raised on the loop thread when the daemon sends this stream's node a command.</summary>
    /// <remarks>
    /// Suspend, Flush, Drain and RequestProcess all arrive here. A state change tells you the
    /// stream stopped streaming; this tells you why.
    /// </remarks>
    public event Action<SpaNodeCommand>? CommandReceived
    {
        add => _commandReceived += value;
        remove => _commandReceived -= value;
    }

    private Action<SpaNodeCommand>? _commandReceived;

    private void RaiseCommand(SpaNodeCommand command) => _commandReceived?.Invoke(command);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _core?.DisposeAsync() ?? ValueTask.CompletedTask;

    private unsafe void OnBuffer(spa_data* d, pw_buffer* buf, in PipeWireStreamCore.StreamClock clock)
    {
        if (d->data is null || d->chunk is null) return;

        // maxsize is the producer's word, and it is unsigned. Casting first turns a value above
        // int.MaxValue into a negative length, which the span constructor is the wrong place to
        // find out about.
        if (d->maxsize > int.MaxValue) return;

        // The negotiated snapshot, not the offered values: a renegotiation the handler has not
        // seen yet fills the wrong shape, and a withdrawn format (nothing negotiated) fills
        // nothing rather than dividing by a zero frame size below.
        SpaFormatPod.AudioFormatInfo fmt = Negotiated;
        if (fmt.SampleRate <= 0 || fmt.Channels <= 0 || fmt.Format == AudioSampleFormat.Unknown)
            return;

        int max = (int)d->maxsize;
        int frameBytes = fmt.Format.BytesPerSample() * fmt.Channels;

        // Honour what the graph asked for. pw_buffer.requested is the number of frames the
        // resampler wants this cycle, and filling the whole buffer regardless hands downstream
        // more than it has room to take - which it then has to queue, adding latency the caller
        // never asked for. Zero means the producer offered no suggestion, in which case maxsize
        // stands. Upstream's own audio-src example does exactly this clamp.
        if (buf->requested != 0 && frameBytes > 0)
        {
            long wanted = (long)buf->requested * frameBytes;
            if (wanted < max) max = (int)wanted;
        }

        // Written before the handler runs, not after. The core queues the buffer in a finally even
        // when the handler throws, and a chunk left holding the previous cycle's size publishes
        // that many bytes of whatever is in the buffer now - stale audio, presented as current.
        d->chunk->offset = 0;
        d->chunk->stride = frameBytes;
        d->chunk->size   = 0;

        var samples = new Span<byte>(d->data, max);
        int written = FillSamples?.Invoke(this, samples, fmt.SampleRate, fmt.Channels, fmt.Format) ?? 0;
        written = Math.Clamp(written, 0, max);

        // Down to a whole number of frames. A producer that returns a byte count mid-frame would
        // otherwise publish a partial one, and since chunk.size is read in units of chunk.stride
        // the consumer takes the remainder as the start of the next frame: every channel after it
        // is offset by the shortfall for the rest of the buffer.
        if (frameBytes > 0) written -= written % frameBytes;

        d->chunk->size = (uint)written;

        // In frames, not bytes, and on the pw_buffer rather than the chunk. PipeWire sums this
        // across queued buffers and reports it as pw_time.queued, which is what a rate controller
        // measures its error against; leaving it zero makes the stream look permanently empty.
        // module-rtp, module-avb and module-roc all set it the same way.
        buf->size = frameBytes > 0 ? (ulong)(written / frameBytes) : 0;

        // After the handler, so a time it set for this buffer is the one stamped. Taken, not read:
        // a supplied time belongs to one buffer.
        long? supplied = NextPresentationTimestampNs;
        if (supplied is not null) NextPresentationTimestampNs = null;

        if (written > 0)
            PipeWireStreamCore.StampPresentationTime(buf, supplied ?? _core?.NowNs() ?? -1);
    }

    /// <summary>
    /// The presentation time to stamp on the next buffer, in nanoseconds on CLOCK_MONOTONIC, or null
    /// to stamp the current stream time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written into the buffer's <c>SPA_META_Header</c>, as GStreamer's pipewiresink writes it for
    /// audio and video alike. The clock is CLOCK_MONOTONIC, what <c>pw_stream_get_nsec</c> reads and
    /// what driver nodes publish the graph clock in; on Linux
    /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> reads the same clock in nanoseconds.
    /// </para>
    /// <para>
    /// It reaches a consumer only when nothing converts the audio on the way: no audio converter or
    /// mixer in the graph copies <c>spa_meta_header.pts</c> (upstream's only writers are producers -
    /// alsa-pcm, bluez5, v4l2, pipewiresink), and between two streams the audio always passes through
    /// converters. So an ordinary audio consumer gets a null <see cref="AudioFrame.PresentationTimestampNs"/>
    /// and aligns on the graph's cycle time in <see cref="AudioFrame.QueuedTimeNs"/> instead. To put audio and video on one timeline, stamp the video in the graph's clock - the
    /// default, <c>pw_stream_get_nsec</c> - which is the clock the audio arrives in.
    /// </para>
    /// <para>
    /// Applies to one buffer and is then cleared, so a value set once does not stamp every buffer
    /// after it. Leave it unset to stamp the current stream time, which is right for a live source.
    /// </para>
    /// </remarks>
    public long? NextPresentationTimestampNs { get; set; }

    private unsafe void OnFormat(spa_pod* param)
    {
        if (param is null)
        {
            Volatile.Write(ref _fmtCell,
                new NegotiatedFormat(new SpaFormatPod.AudioFormatInfo(AudioSampleFormat.Unknown, 0, 0)));
            return;
        }

        SpaFormatPod.AudioFormatInfo parsed = SpaFormatPod.ParseAudioFormat(param, Negotiated);
        Volatile.Write(ref _fmtCell, new NegotiatedFormat(parsed));
    }

    private void OnState(PipeWireStreamState oldState, PipeWireStreamState newState) =>
        StateChanged?.Invoke(this, oldState, newState);

    /// <summary>Waits until the stream is negotiated and running.</summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <remarks>
    /// <c>Connect</c> issues a request; the daemon then negotiates a format over several round
    /// trips, and only then does the stream start. Without this a caller has to subscribe to
    /// <c>StateChanged</c> and drive its own completion, which is the same code every time.
    /// <para>
    /// Cancelling abandons the wait, not the stream: the connection stays up and keeps negotiating,
    /// because there is nothing to recall. Dispose it to stop it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not connected yet.</exception>
    /// <exception cref="PipeWireException">The stream reached its error state instead.</exception>
    public Task WaitForStreamingAsync(CancellationToken cancellationToken = default)
    {
        PipeWireStreamCore core = _core
            ?? throw new InvalidOperationException("Connect before waiting for the stream to start.");

        return core.WaitForStreamingAsync(cancellationToken);
    }

    /// <summary>
    /// Waits until the daemon has given this stream a node id, and returns it.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The node id, the same value <see cref="NodeId"/> reports from then on.</returns>
    /// <remarks>
    /// <see cref="NodeId"/> is null straight after <c>Connect</c>: the stream is a proxy until the
    /// daemon binds it, and the id is only assigned then. Linking or targeting this stream needs the
    /// id, so await this rather than reading the property and hoping. It completes when the stream
    /// first reaches Paused, which is where upstream's own examples read the id.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not connected yet.</exception>
    /// <exception cref="PipeWireException">The stream reached its error state instead.</exception>
    public Task<uint> WaitForNodeIdAsync(CancellationToken cancellationToken = default)
    {
        PipeWireStreamCore core = _core
            ?? throw new InvalidOperationException("Connect before waiting for the node id.");

        return core.WaitForNodeIdAsync(cancellationToken);
    }

    /// <summary>Every control this stream exposes, as the daemon last reported them.</summary>
    /// <remarks>
    /// Empty until the stream is connected and the daemon has reported them, which happens during
    /// negotiation. A snapshot: the daemon re-reports a control whenever one of its values changes.
    /// </remarks>
    public ImmutableArray<PipeWireStreamControl> Controls =>
        _core?.Controls ?? [];

    /// <summary>One control by SPA property id, or null when the stream has not reported it.</summary>
    public PipeWireStreamControl? GetControl(uint id) => _core?.GetControl(id);

    /// <summary>Sets a control's values.</summary>
    /// <param name="id">The SPA property id, as carried by <see cref="PipeWireStreamControl.Id"/>.</param>
    /// <param name="values">
    /// One value for a scalar control, or one per channel. More than
    /// <see cref="PipeWireStreamControl.MaximumValues"/> is the daemon's to refuse, not this
    /// library's to guess at.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait for the loop lock.</param>
    /// <remarks>
    /// Sent as a <c>Props</c> object. The daemon applies it when it next runs the node, so this
    /// returning does not mean the value is in effect; read it back from <see cref="Controls"/> if
    /// that matters.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not connected yet.</exception>
    /// <exception cref="ArgumentException"><paramref name="values"/> is empty.</exception>
    public void SetControl(uint id, ReadOnlySpan<float> values, CancellationToken cancellationToken = default)
    {
        if (values.IsEmpty)
            throw new ArgumentException("a control needs at least one value.", nameof(values));

        PipeWireStreamCore core = _core
            ?? throw new InvalidOperationException("Connect before setting a control.");

        core.SetControl(id, values, cancellationToken);
    }

    /// <summary>The graph clock as of the last cycle, or null before the graph offers it.</summary>
    /// <remarks>
    /// Two streams on one context are driven by the same clock, so their clocks are directly
    /// comparable - which is what lets audio and video be lined up against each other.
    /// </remarks>
    public PipeWireGraphClock? GraphClock => _core?.GraphClock;

    /// <summary>What the graph's resampler is doing for this stream, or null if none is.</summary>
    public PipeWireRateMatch? RateMatch => _core?.RateMatch;

    /// <summary>
    /// Applies a rate correction to this stream, 1.0 being none.
    /// </summary>
    /// <remarks>
    /// For bridging the graph clock to one this library does not own - a network transport, another
    /// device. The upstream pattern is to derive the correction from how far the queue is from its
    /// target, smooth it, and apply it here; see PipeWire's own rtp and tunnel modules. Applying an
    /// unsmoothed correction makes the drift worse rather than better.
    /// </remarks>
    public void SetRate(double rate) => _core?.SetRate(rate);

    /// <summary>
    /// Announces the latency this stream adds, so the rest of the graph can compensate.
    /// </summary>
    /// <remarks>
    /// Anything holding a queue - a network transport, an encoder - adds delay that nothing else
    /// can see. Left unannounced it becomes drift between this stream and everything it is meant
    /// to stay in sync with. Pass the process latency too when the delay is per-cycle rather than
    /// fixed; PipeWire's own transport modules announce both.
    /// </remarks>
    public void AnnounceLatency(PipeWireLatency latency, PipeWireProcessLatency? processLatency = null)
    {
        ArgumentNullException.ThrowIfNull(latency);
        _core?.AnnounceLatency(latency, processLatency);
    }

    /// <summary>
    /// Plays out what is queued and waits until the daemon says it has finished.
    /// </summary>
    /// <remarks>
    /// The difference between stopping and ending: disposing a stream drops whatever is still
    /// queued, which truncates the tail of the audio. This waits for it.
    /// </remarks>
    public Task DrainAsync(CancellationToken cancellationToken = default) =>
        _core?.DrainAsync(cancellationToken) ?? Task.CompletedTask;
}
