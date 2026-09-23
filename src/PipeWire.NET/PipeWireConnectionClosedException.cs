namespace PipeWire.NET;

/// <summary>A connection that was working has gone; objects bound through it are dead.</summary>
public sealed class PipeWireConnectionClosedException : PipeWireConnectionException
{
    /// <inheritdoc cref="PipeWireException(string, int, uint?, string?)"/>
    public PipeWireConnectionClosedException(
        string operation,
        int result,
        uint? objectId = null,
        string? daemonMessage = null
    )
        : base(operation, result, objectId, daemonMessage) { }

    /// <inheritdoc/>
    public PipeWireConnectionClosedException()
        : base() { }

    /// <inheritdoc/>
    public PipeWireConnectionClosedException(string message)
        : base(message) { }

    /// <inheritdoc/>
    public PipeWireConnectionClosedException(string message, Exception innerException)
        : base(message, innerException) { }
}
