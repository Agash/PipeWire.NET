using System.Runtime.Versioning;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media.Streams;

/// <summary>
/// Publishes audio samples TO PipeWire (virtual source / playback). PipeWire pulls
/// samples by invoking <see cref="FillSamples"/>; write PCM into the supplied span.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PipeWireAudioOutput : IAsyncDisposable
{
    /// <summary>Signature for <see cref="FillSamples"/>. Return the number of bytes written (0 = silence).</summary>
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
    /// means the current default sink - i.e. the speakers. Pass <see langword="false"/> to publish
    /// the node and leave it unrouted, so a caller can link it deliberately. A test or a transport
    /// agent usually wants that; a media player does not.
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

        int max = (int)d->maxsize;
        var samples = new Span<byte>(d->data, max);
        int written = FillSamples?.Invoke(this, samples, _sampleRate, _channels, _format) ?? 0;
        written = Math.Clamp(written, 0, max);

        d->chunk->offset = 0;
        d->chunk->stride = _format.BytesPerSample() * _channels;
        d->chunk->size   = (uint)written;
    }

    private void OnState(PipeWireStreamState oldState, PipeWireStreamState newState) =>
        StateChanged?.Invoke(this, oldState, newState);
}
