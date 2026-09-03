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

    /// <summary>Raised when the connection state changes.</summary>
    public event Action<PipeWireAudioOutput, PipeWireStreamState, PipeWireStreamState>? StateChanged;

    private readonly PipeWireContext _ctx;
    private readonly string _name;
    private readonly int _sampleRate, _channels;
    private readonly AudioSampleFormat _format;
    private PipeWireStreamCore? _core;

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
    public unsafe void Connect(
        uint targetNodeId = AnyNode, string? targetObjectName = null, bool autoConnect = true)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");

        var props = new StreamProperties(StreamMediaType.Audio, StreamCategory.Playback)
            .WithRole("Music")
            .WithNodeName(_name);
        if (targetObjectName is not null) props.WithTargetObject(targetObjectName);
        // Built locally and only published once the connect succeeded. Assigning the field first
        // leaves a failed connect behind a stream that reports itself already connected and can
        // never be retried.

        var core = new PipeWireStreamCore(_ctx, props, _name, OnBuffer, OnState);

        PipeWireStreamFlags flags = PipeWireStreamFlags.MapBuffers;
        if (autoConnect) flags |= PipeWireStreamFlags.Autoconnect;

        Span<byte> pod = stackalloc byte[256];
        int len = SpaFormatPod.WriteAudioFormat(pod, _format, _sampleRate, _channels);
        try
        {
            core.Connect(SpaDirection.Output, targetNodeId, flags, pod[..len]);
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

        int max = (int)d->maxsize;
        int frameBytes = _format.BytesPerSample() * _channels;

        // Written before the handler runs, not after. The core queues the buffer in a finally even
        // when the handler throws, and a chunk left holding the previous cycle's size publishes
        // that many bytes of whatever is in the buffer now - stale audio, presented as current.
        d->chunk->offset = 0;
        d->chunk->stride = frameBytes;
        d->chunk->size   = 0;

        var samples = new Span<byte>(d->data, max);
        int written = FillSamples?.Invoke(this, samples, _sampleRate, _channels, _format) ?? 0;
        written = Math.Clamp(written, 0, max);

        // Down to a whole number of frames. A producer that returns a byte count mid-frame would
        // otherwise publish a partial one, and since chunk.size is read in units of chunk.stride
        // the consumer takes the remainder as the start of the next frame: every channel after it
        // is offset by the shortfall for the rest of the buffer.
        if (frameBytes > 0) written -= written % frameBytes;

        d->chunk->size = (uint)written;
    }

    private void OnState(PipeWireStreamState oldState, PipeWireStreamState newState) =>
        StateChanged?.Invoke(this, oldState, newState);
}
