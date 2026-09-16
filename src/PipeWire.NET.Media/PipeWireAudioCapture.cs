using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media;

/// <summary>
/// Receives audio samples from a PipeWire source (microphone, virtual source, or the
/// monitor of an output sink).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> One connection per instance: <c>Connect</c> refuses a second call, and disposal
/// is final. To point at a different source, make a new instance. There is deliberately no
/// reconnect, because a reconnect that reuses the negotiated format and buffers of a stream that
/// already ended is a different object wearing the old one's state.
/// </para>
/// <para>
/// <b>What the daemon does when a source disappears</b> is a separate question, and by default it
/// attaches the stream to another one. That is convenient for a media player and wrong for anything
/// that cares which device it is reading: frames keep arriving, from somewhere else, with nothing
/// in the API to say so. Pass <c>stayWithTheSource</c> to end the stream instead.
/// </para>
/// <para>
/// <b>Dispose it; do not let it fall out of scope.</b> The callbacks the daemon holds refer back
/// here through a weak handle, so an instance the application drops is collected and simply stops
/// delivering, with no error and no final state change. That is the deliberate half of the trade:
/// a strong handle would keep every one ever made alive for the life of the process. What it costs
/// is that the garbage collector cannot be the thing that closes one, because by the time it runs
/// there is nothing left to close it from.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireAudioCapture : IAsyncDisposable
{
    /// <summary>Wildcard node id - let PipeWire auto-select a source.</summary>
    public const uint AnyNode = NativeConstants.PW_ID_ANY;

    /// <summary>Handles an audio frame on the loop thread. Do not cache the frame.</summary>
    public delegate void FrameReadyHandler(PipeWireAudioCapture sender, AudioFrame frame);

    /// <summary>Raised on the loop thread when an audio chunk is available. Do not cache the frame.</summary>
    public event FrameReadyHandler? FrameReady;

    /// <summary>Raised on the loop thread when the connection state changes.</summary>
    public event Action<PipeWireAudioCapture, PipeWireStreamState, PipeWireStreamState>? StateChanged;

    private readonly PipeWireContext _ctx;
    private readonly string _name;
    private readonly ILogger _logger;
    private PipeWireStreamCore? _core;
    private ulong _sequence;
    private SpaFormatPod.AudioFormatInfo _fmt = new(AudioSampleFormat.F32Le, 48000, 2);

    /// <param name="context">A started <see cref="PipeWireContext"/>.</param>
    /// <param name="name">node.name advertised in the graph.</param>
    public PipeWireAudioCapture(PipeWireContext context, string name = "PipeWire.NET.AudioCapture")
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(name);
        _ctx = context; _name = name;
        _logger = context.LoggerFactory.CreateLogger($"PipeWire.NET.{name}");
    }

    /// <summary>Connects to a discovered source.</summary>
    /// <param name="source">The node to capture from.</param>
    /// <param name="sampleRate">Preferred sample rate (Hz).</param>
    /// <param name="channels">Preferred channel count.</param>
    /// <param name="format">Preferred sample format.</param>
    /// <param name="stayWithTheSource">
    /// <see langword="true"/> to end the stream when its source goes away, rather than letting the
    /// daemon attach it to another one.
    /// </param>
    /// <param name="pullMode">
    /// Take the driver role, so cycles happen when this stream asks for them rather than when the
    /// graph schedules them. Pair with <see cref="TriggerProcess"/>, and with
    /// <see cref="CommandReceived"/> to answer <see cref="SpaNodeCommand.RequestProcess"/>. This is
    /// how upstream's pull example consumes: a driver on an input stream pulls.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <param name="cancellationToken">
    /// Abandons the wait for the loop lock. The connect request itself is issued
    /// synchronously once that is held, so there is nothing to recall after it.
    /// </param>
    public void Connect(
        PipeWireNode source,
        int sampleRate = 48000,
        int channels = 2,
        AudioSampleFormat format = AudioSampleFormat.F32Le,
        bool stayWithTheSource = false,
        bool pullMode = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Connect(source.NodeId, sampleRate, channels, format,
            stayWithTheSource: stayWithTheSource, cancellationToken: cancellationToken);
    }

    /// <summary>Connects to an audio source.</summary>
    /// <param name="targetNodeId">Source node id, or <see cref="AnyNode"/>.</param>
    /// <param name="sampleRate">Preferred sample rate (Hz).</param>
    /// <param name="channels">Preferred channel count.</param>
    /// <param name="format">Preferred sample format.</param>
    /// <param name="targetObjectName">
    /// Optional <c>target.object</c> - bind to a specific node by name/serial regardless of
    /// the session manager's default-device routing.
    /// </param>
    /// <param name="stayWithTheSource">
    /// <see langword="true"/> to end the stream when its source goes away, rather than letting the
    /// daemon attach it to another one.
    /// </param>
    /// <param name="pullMode">
    /// Take the driver role, so cycles happen when this stream asks for them rather than when the
    /// graph schedules them. Pair with <see cref="TriggerProcess"/>, and with
    /// <see cref="CommandReceived"/> to answer <see cref="SpaNodeCommand.RequestProcess"/>.
    /// </param>
    /// <param name="autoConnect">
    /// When true the session manager routes this stream. Pass <see langword="false"/> to publish
    /// the node unlinked and link it deliberately (<see cref="Graph.PipeWireRegistry.CreateLinkAsync(Graph.PipeWirePort, Graph.PipeWirePort, CancellationToken)"/>),
    /// out of the session manager's policy entirely: nothing moves it to another source when a
    /// linked one goes, and nothing links it to a default. What a patchbay or a router wants.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the wait for the loop lock. The connect request itself is issued
    /// synchronously once that is held, so there is nothing to recall after it.
    /// </param>
    public unsafe void Connect(uint targetNodeId = AnyNode,
        int sampleRate = 48000, int channels = 2, AudioSampleFormat format = AudioSampleFormat.F32Le,
        string? targetObjectName = null,
        bool stayWithTheSource = false,
        bool pullMode = false,
        bool autoConnect = true,
        CancellationToken cancellationToken = default)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");
        Volatile.Write(ref _fmtCell,
            new NegotiatedFormat(new SpaFormatPod.AudioFormatInfo(format, sampleRate, channels)));

        var props = new StreamProperties(StreamMediaType.Audio, StreamCategory.Capture)
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        // Built locally and only published once the connect succeeded. Assigning the field first
        // leaves a failed connect behind a stream that reports itself already connected and can
        // never be retried.

        var core = new PipeWireStreamCore(_ctx, props, _name, OnBuffer, OnState, OnFormat);

        Span<byte> pod = stackalloc byte[256];
        int len = SpaFormatPod.WriteAudioFormat(pod, format, sampleRate, channels);
        try
        {
            core.Connect(SpaDirection.Input, targetNodeId,
            PipeWireStreamFlags.MapBuffers
                | (autoConnect ? PipeWireStreamFlags.Autoconnect : 0)
                | (stayWithTheSource ? PipeWireStreamFlags.DontReconnect : 0)
                | (pullMode ? PipeWireStreamFlags.Driver : 0),
            pod[..len],
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


    /// <summary>
    /// From inside a <c>FrameReady</c> handler: drop this frame instead of consuming it.
    /// </summary>
    /// <remarks>
    /// The buffer goes straight back to the pool without being counted as consumed. A consumer
    /// that is behind and would rather skip than fall further behind should say so here, so the
    /// queue depth other code reads stays honest about what was actually taken.
    /// </remarks>
    public void SkipCurrentFrame() => _core?.SkipCurrentBuffer();

    /// <summary>Whether the daemon has made this stream the graph's driver.</summary>
    public bool IsDriving => _core?.IsDriving ?? false;

    /// <summary>
    /// Asks for one cycle now. Only meaningful on a stream connected with <c>pullMode</c>.
    /// </summary>
    /// <remarks>
    /// The pull primitive: a consumer that drives calls this on its own schedule, or in answer to
    /// <see cref="SpaNodeCommand.RequestProcess"/> from <see cref="CommandReceived"/>. Safe to call
    /// from any thread - the trigger does its own hop onto the data loop.
    /// </remarks>
    public void TriggerProcess() => _core?.TriggerProcess();

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
        uint offset = d->chunk->offset;
        uint size   = d->chunk->size;
        if (size == 0) return;

        // The chunk header lives in memory the producer owns, so its offset and size are inputs,
        // not facts. A span built from an out-of-range pair reads straight past the mapping, and a
        // size above int.MaxValue casts to a negative length.
        if ((ulong)offset + size > d->maxsize) return;
        if (size > int.MaxValue) return;

        SpaFormatPod.AudioFormatInfo fmt = Format;
        if (fmt.SampleRate <= 0 || fmt.Channels <= 0) return;

        var samples = new ReadOnlySpan<byte>((byte*)d->data + offset, (int)size);
        var frame = new AudioFrame(samples, fmt.SampleRate, fmt.Channels, fmt.Format, ++_sequence,
            presentationTimestampNs: SpaFormatPod.FindPresentationTimestampNs(buf),
            queuedTimeNs: SpaFormatPod.QueuedTimeNs(buf),
            graphTimeNs: clock.GraphTimeNs,
            streamPositionNs: clock.StreamPositionNs,
            delayNs: clock.DelayNs);
        FrameReady?.Invoke(this, frame);
    }

    private void OnState(PipeWireStreamState oldState, PipeWireStreamState newState) =>
        StateChanged?.Invoke(this, oldState, newState);

    private unsafe void OnFormat(spa_pod* param)
    {
        if (param is null)
        {
            Volatile.Write(ref _fmtCell,
                new NegotiatedFormat(new SpaFormatPod.AudioFormatInfo(AudioSampleFormat.Unknown, 0, 0)));
            return;
        }

        SpaFormatPod.AudioFormatInfo parsed = SpaFormatPod.ParseAudioFormat(param, Format);
        Volatile.Write(ref _fmtCell, new NegotiatedFormat(parsed));
        LogNegotiatedFormat(parsed.Format, parsed.SampleRate, parsed.Channels);
    }

    // Swapped whole rather than mutated, for the same reason as the video wrappers: a multi-field
    // struct written on the loop thread and read on the data thread has no atomic assignment, so a
    // reader can pair one negotiation's rate with the next one's channel count.
    private sealed class NegotiatedFormat(SpaFormatPod.AudioFormatInfo info)
    {
        public SpaFormatPod.AudioFormatInfo Info { get; } = info;
    }

    private NegotiatedFormat _fmtCell =
        new(new SpaFormatPod.AudioFormatInfo(AudioSampleFormat.F32Le, 48000, 2));

    private SpaFormatPod.AudioFormatInfo Format => Volatile.Read(ref _fmtCell).Info;

    [LoggerMessage(Level = LogLevel.Debug, Message = "negotiated audio format {Format} {SampleRate}Hz {Channels}ch")]
    private partial void LogNegotiatedFormat(AudioSampleFormat format, int sampleRate, int channels);

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
}
