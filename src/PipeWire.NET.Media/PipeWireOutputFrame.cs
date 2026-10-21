using System.Runtime.Versioning;

namespace PipeWire.NET.Media;

/// <summary>
/// A buffer taken to write the next frame into (<see cref="PipeWireVideoOutput.TryBeginFrame"/>): one of
/// the application's shared DMA-BUFs, or the daemon's memory.
/// </summary>
[SupportedOSPlatform("linux")]
public readonly ref struct PipeWireOutputFrame : IDisposable
{
    private readonly PipeWireVideoOutput? _output;

    internal PipeWireOutputFrame(
        PipeWireVideoOutput output,
        int bufferIndex,
        Span<byte> pixels,
        int stride,
        PixelFormat format,
        int width,
        int height,
        PipeWireSyncPoint? acquire = null
    )
    {
        _output = output;
        BufferIndex = bufferIndex;
        Pixels = pixels;
        Stride = stride;
        Format = format;
        Width = width;
        Height = height;
        Acquire = acquire;
    }

    /// <summary>
    /// The pixel format the buffer holds, the one negotiated when it was taken. A consumer may settle on
    /// another of the offered formats at any time; a frame in a different one is not written here.
    /// </summary>
    public PixelFormat Format { get; }

    /// <summary>The width the buffer holds, in pixels.</summary>
    public int Width { get; }

    /// <summary>The height the buffer holds, in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Under explicit sync, the point on the buffer's acquire timeline that marks this frame ready for the
    /// consumer; null otherwise. GPU work rendering the frame can signal it itself, and the frame is then
    /// published with <c>Publish(signalAcquire: false)</c>, so no thread waits for the GPU.
    /// </summary>
    public PipeWireSyncPoint? Acquire { get; }

    /// <summary>
    /// For a shared buffer, the pool buffer to render into: the index
    /// <see cref="PipeWireVideoOutput.AllocateDmaBuf"/> backed with the application's DMA-BUFs; -1 for a
    /// memory buffer.
    /// </summary>
    public int BufferIndex { get; }

    /// <summary>
    /// For a memory buffer, the picture to write, its planes one after another at <see cref="Stride"/>
    /// (the chroma planes of a planar format at the stride their width scales it to); empty for a shared
    /// buffer.
    /// </summary>
    public Span<byte> Pixels { get; }

    /// <summary>For a memory buffer, the bytes per row of the first plane; 0 for a shared buffer.</summary>
    public int Stride { get; }

    /// <summary>
    /// Queues the buffer to the consumer as the next frame. Rendering has finished when this is called,
    /// or, under explicit sync with timelines the application supplied, signals their acquire point.
    /// </summary>
    public void Publish() => Publish(signalAcquire: true);

    /// <summary>Queues the buffer to the consumer as the next frame.</summary>
    /// <param name="signalAcquire">
    /// Whether the library signals <see cref="Acquire"/> now, rendering having finished; false when the
    /// application's GPU work signals it once the frame is rendered, and the consumer waits for that.
    /// </param>
    public void Publish(bool signalAcquire) =>
        (_output ?? throw new InvalidOperationException("The frame was not opened.")).EndFrame(
            publish: true,
            signalAcquire
        );

    /// <summary>Ends the frame; unless it was published, the buffer goes back unused.</summary>
    public void Dispose() => _output?.EndFrame(publish: false);
}
