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
    public const uint AnyNode = Native.PW_ID_ANY;

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
    /// <param name="cancellationToken">
    /// Abandons the wait for the loop lock. The connect request itself is issued
    /// synchronously once that is held, so there is nothing to recall after it.
    /// </param>
    public unsafe void Connect(
        uint targetNodeId = AnyNode,
        string? targetObjectName = null,
        bool autoConnect = true,
        CancellationToken cancellationToken = default)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");

        var props = new StreamProperties(StreamMediaType.Audio, StreamCategory.Playback)
            .WithRole("Music")
            .WithNodeName(_name);
        if (targetObjectName is not null) props.WithTargetObject(targetObjectName);
        // Built locally and only published once the connect succeeded. Assigning the field first
        // leaves a failed connect behind a stream that reports itself already connected and can
        // never be retried.

        var core = new PipeWireStreamCore(_ctx, props, _name, OnBuffer, OnState, OnFormat);

        PipeWireStreamFlags flags = PipeWireStreamFlags.MapBuffers;
        if (autoConnect) flags |= PipeWireStreamFlags.Autoconnect;

        Span<byte> pod = stackalloc byte[256];
        int len = SpaFormatPod.WriteAudioFormat(pod, _format, _sampleRate, _channels);
        try
        {
            core.Connect(SpaDirection.Output, targetNodeId, flags, pod[..len],
                cancellationToken: cancellationToken);
            _core = core;
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
    /// <c>graph.GetPortsForNode(stream.NodeId!.Value)</c> finds its ports, which can then be linked.
    /// </remarks>
    public uint? NodeId
    {
        get
        {
            uint id = _core?.NodeId ?? Native.PW_ID_ANY;
            return id == Native.PW_ID_ANY ? null : id;
        }
    }

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
    }

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
}
