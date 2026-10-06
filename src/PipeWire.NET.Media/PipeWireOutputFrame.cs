using System.Runtime.Versioning;

namespace PipeWire.NET.Media;

/// <summary>
/// A shared buffer taken to render the next frame into (<see cref="PipeWireVideoOutput.TryBeginFrame"/>).
/// </summary>
[SupportedOSPlatform("linux")]
public readonly ref struct PipeWireOutputFrame : IDisposable
{
    private readonly PipeWireVideoOutput? _output;

    internal PipeWireOutputFrame(PipeWireVideoOutput output, int bufferIndex)
    {
        _output = output;
        BufferIndex = bufferIndex;
    }

    /// <summary>
    /// The pool buffer to render into: the index <see cref="PipeWireVideoOutput.AllocateDmaBuf"/> backed
    /// with the application's DMA-BUFs.
    /// </summary>
    public int BufferIndex { get; }

    /// <summary>
    /// Queues the buffer to the consumer as the next frame. Rendering has finished when this is called,
    /// or, under explicit sync with timelines the application supplied, signals their acquire point.
    /// </summary>
    public void Publish() =>
        (_output ?? throw new InvalidOperationException("The frame was not opened.")).EndFrame(
            publish: true
        );

    /// <summary>Ends the frame; unless it was published, the buffer goes back unused.</summary>
    public void Dispose() => _output?.EndFrame(publish: false);
}
