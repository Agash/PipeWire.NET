namespace PipeWire.NET.Media;

/// <summary>
/// A capture frame's buffer kept from the producer past the handler it was delivered to. Disposing it
/// hands the buffer back, once, from any thread.
/// </summary>
/// <seealso cref="PipeWireVideoCapture.HoldCurrentFrame"/>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class PipeWireFrameHold : IDisposable
{
    private readonly PipeWireVideoCapture _capture;
    private readonly PipeWireStreamCore.BufferHold _buffer;
    private readonly SyncRelease _release;
    private int _disposed;

    internal PipeWireFrameHold(
        PipeWireVideoCapture capture,
        PipeWireStreamCore.BufferHold buffer,
        SyncRelease release
    )
    {
        _capture = capture;
        _buffer = buffer;
        _release = release;
    }

    /// <summary>Hands the buffer back to the producer, signalling its release point first.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _capture.Released(_release);
            _buffer.Release();
        }
    }
}
