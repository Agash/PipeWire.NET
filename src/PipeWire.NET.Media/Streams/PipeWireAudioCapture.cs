using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media.Streams;

/// <summary>
/// Receives audio samples from a PipeWire source (microphone, virtual source, or the
/// monitor of an output sink).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class PipeWireAudioCapture : IAsyncDisposable
{
    /// <summary>Wildcard node id - let PipeWire auto-select a source.</summary>
    public const uint AnyNode = Native.PW_ID_ANY;

    /// <summary>Signature for <see cref="FrameReady"/>.</summary>
    public delegate void FrameReadyHandler(PipeWireAudioCapture sender, AudioFrame frame);

    /// <summary>Raised on the loop thread when an audio chunk is available. Do not cache the frame.</summary>
    public event FrameReadyHandler? FrameReady;

    /// <summary>Raised when the connection state changes.</summary>
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

    /// <summary>Connects to an audio source.</summary>
    /// <param name="targetNodeId">Source node id, or <see cref="AnyNode"/>.</param>
    /// <param name="sampleRate">Preferred sample rate (Hz).</param>
    /// <param name="channels">Preferred channel count.</param>
    /// <param name="format">Preferred sample format.</param>
    /// <param name="targetObjectName">
    /// Optional <c>target.object</c> - bind to a specific node by name/serial regardless of
    /// the session manager's default-device routing.
    /// </param>
    public unsafe void Connect(uint targetNodeId = AnyNode,
        int sampleRate = 48000, int channels = 2, AudioSampleFormat format = AudioSampleFormat.F32Le,
        string? targetObjectName = null)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");
        _fmt = new SpaFormatPod.AudioFormatInfo(format, sampleRate, channels);

        var props = new StreamProperties(StreamMediaType.Audio, StreamCategory.Capture)
            .WithRole("Music")
            .WithNodeName(_name);
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
            PipeWireStreamFlags.Autoconnect | PipeWireStreamFlags.MapBuffers,
            pod[..len]);
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
        uint offset = d->chunk->offset;
        uint size   = d->chunk->size;
        if (size == 0) return;

        // The chunk header lives in memory the producer owns, so its offset and size are inputs,
        // not facts. A span built from an out-of-range pair reads straight past the mapping.
        if ((ulong)offset + size > d->maxsize) return;

        var samples = new ReadOnlySpan<byte>((byte*)d->data + offset, (int)size);
        var frame = new AudioFrame(samples, _fmt.SampleRate, _fmt.Channels, _fmt.Format, ++_sequence,
            presentationTimeNs: SpaFormatPod.FindPresentationTimeNs(buf->buffer),
            captureClockNs: clock.CaptureClockNs,
            mediaClockNs: clock.MediaClockNs,
            delayNs: clock.DelayNs);
        FrameReady?.Invoke(this, frame);
    }

    private void OnState(PipeWireStreamState oldState, PipeWireStreamState newState) =>
        StateChanged?.Invoke(this, oldState, newState);

    private unsafe void OnFormat(spa_pod* param)
    {
        _fmt = SpaFormatPod.ParseAudioFormat(param, _fmt);
        LogNegotiatedFormat(_fmt.Format, _fmt.SampleRate, _fmt.Channels);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "negotiated audio format {Format} {SampleRate}Hz {Channels}ch")]
    private partial void LogNegotiatedFormat(AudioSampleFormat format, int sampleRate, int channels);
}
