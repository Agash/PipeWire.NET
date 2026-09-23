namespace PipeWire.NET;

/// <summary>No connection was established.</summary>
public sealed class PipeWireConnectFailedException : PipeWireConnectionException
{
    /// <inheritdoc cref="PipeWireException(string, int, uint?, string?)"/>
    public PipeWireConnectFailedException(string operation, int result, uint? objectId = null, string? daemonMessage = null)
        : base(operation, result, objectId, daemonMessage)
    {
    }

    /// <inheritdoc/>
    public PipeWireConnectFailedException() : base()
    {
    }

    /// <inheritdoc/>
    public PipeWireConnectFailedException(string message) : base(message)
    {
    }

    /// <inheritdoc/>
    public PipeWireConnectFailedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
