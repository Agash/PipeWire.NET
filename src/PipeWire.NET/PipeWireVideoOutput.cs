using System.Runtime.Versioning;
using PipeWire.NET.Generated;
using PipeWire.NET.Spa;

namespace PipeWire.NET;

/// <summary>
/// Publishes video frames TO PipeWire as a virtual camera. PipeWire pulls frames by
/// invoking <see cref="FillFrame"/>; write your pixels into the supplied span.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PipeWireVideoOutput : IAsyncDisposable
{
    /// <summary>Signature for <see cref="FillFrame"/>. Return <see langword="true"/> to publish the frame.</summary>
    public delegate bool FillFrameHandler(
        PipeWireVideoOutput sender, Span<byte> pixels, int stride, int width, int height, PixelFormat format);

    /// <summary>Invoked on the loop thread when a buffer is ready to fill.</summary>
    public event FillFrameHandler? FillFrame;

    /// <summary>Raised when the connection state changes.</summary>
    public event Action<PipeWireVideoOutput, PipeWireStreamState, PipeWireStreamState>? StateChanged;

    private readonly PipeWireContext _ctx;
    private readonly string _name;
    private readonly int _width, _height, _frameRate;
    private readonly PixelFormat _format;
    private PipeWireStreamCore? _core;

    /// <param name="context">A started <see cref="PipeWireContext"/>.</param>
    /// <param name="nodeName">Name visible to consumers.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="format">Pixel format to publish.</param>
    /// <param name="frameRate">Target frame rate (Hz).</param>
    public PipeWireVideoOutput(PipeWireContext context, string nodeName,
        int width, int height, PixelFormat format = PixelFormat.Bgra, int frameRate = 30)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(nodeName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        _ctx = context; _name = nodeName;
        _width = width; _height = height; _format = format; _frameRate = frameRate;
    }

    /// <summary>Starts publishing and registers the node in the graph.</summary>
    public unsafe void Connect()
    {
        if (_core is not null) throw new InvalidOperationException("Already connected.");

        var props = new StreamProperties(StreamMediaType.Video, StreamCategory.Playback)
            .WithRole("Camera")
            .WithNodeName(_name);

        _core = new PipeWireStreamCore(_ctx, props, _name, OnBuffer, OnState);

        Span<byte> pod = stackalloc byte[512];
        int len = SpaFormat.WriteVideoFormat(pod,
            stackalloc[] { _format }, (uint)_width, (uint)_height, (uint)_frameRate, fixedSize: true);
        _core.Connect(spa_direction.SPA_DIRECTION_OUTPUT, Native.PW_ID_ANY,
            pw_stream_flags.PW_STREAM_FLAG_AUTOCONNECT | pw_stream_flags.PW_STREAM_FLAG_MAP_BUFFERS,
            pod[..len]);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _core?.DisposeAsync() ?? ValueTask.CompletedTask;

    private unsafe void OnBuffer(spa_data* d, pw_buffer* buf, in PipeWireStreamCore.StreamClock clock)
    {
        if (d->data is null || d->chunk is null) return;

        int stride  = _width * SpaFormat.BytesPerPixel(_format);
        int byteLen = stride * _height;
        if ((uint)byteLen > d->maxsize) byteLen = (int)d->maxsize;

        var pixels = new Span<byte>(d->data, byteLen);
        bool publish = FillFrame?.Invoke(this, pixels, stride, _width, _height, _format) ?? false;

        d->chunk->offset = 0;
        d->chunk->stride = stride;
        d->chunk->size   = publish ? (uint)byteLen : 0;
    }

    private void OnState(PipeWireStreamState oldState, PipeWireStreamState newState) =>
        StateChanged?.Invoke(this, oldState, newState);
}
