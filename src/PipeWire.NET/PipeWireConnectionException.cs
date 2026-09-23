namespace PipeWire.NET;

/// <summary>A failure of the connection rather than of any one request.</summary>
public class PipeWireConnectionException : PipeWireException
{
    /// <inheritdoc cref="PipeWireException(string, int, uint?, string?)"/>
    public PipeWireConnectionException(
        string operation,
        int result,
        uint? objectId = null,
        string? daemonMessage = null
    )
        : base(operation, result, objectId, daemonMessage) { }

    /// <inheritdoc/>
    public PipeWireConnectionException()
        : base() { }

    /// <inheritdoc/>
    public PipeWireConnectionException(string message)
        : base(message) { }

    /// <inheritdoc/>
    public PipeWireConnectionException(string message, Exception innerException)
        : base(message, innerException) { }
}
