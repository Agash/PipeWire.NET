using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Media.Streams;

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

    /// <summary>Connects to a discovered source.</summary>
    /// <param name="source">The node to capture from.</param>
    /// <param name="sampleRate">Preferred sample rate (Hz).</param>
    /// <param name="channels">Preferred channel count.</param>
    /// <param name="format">Preferred sample format.</param>
    /// <param name="stayWithTheSource">
    /// <see langword="true"/> to end the stream when its source goes away, rather than letting the
    /// daemon attach it to another one.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void Connect(
        PipeWireNode source,
        int sampleRate = 48000,
        int channels = 2,
        AudioSampleFormat format = AudioSampleFormat.F32Le,
        bool stayWithTheSource = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        Connect(source.NodeId, sampleRate, channels, format, stayWithTheSource: stayWithTheSource);
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
    public unsafe void Connect(uint targetNodeId = AnyNode,
        int sampleRate = 48000, int channels = 2, AudioSampleFormat format = AudioSampleFormat.F32Le,
        string? targetObjectName = null,
        bool stayWithTheSource = false)
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");
        Volatile.Write(ref _fmtCell,
            new NegotiatedFormat(new SpaFormatPod.AudioFormatInfo(format, sampleRate, channels)));

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
            PipeWireStreamFlags.Autoconnect | PipeWireStreamFlags.MapBuffers
                | (stayWithTheSource ? PipeWireStreamFlags.DontReconnect : 0),
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
        // not facts. A span built from an out-of-range pair reads straight past the mapping, and a
        // size above int.MaxValue casts to a negative length.
        if ((ulong)offset + size > d->maxsize) return;
        if (size > int.MaxValue) return;

        SpaFormatPod.AudioFormatInfo fmt = Format;
        if (fmt.SampleRate <= 0 || fmt.Channels <= 0) return;

        var samples = new ReadOnlySpan<byte>((byte*)d->data + offset, (int)size);
        var frame = new AudioFrame(samples, fmt.SampleRate, fmt.Channels, fmt.Format, ++_sequence,
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
}
