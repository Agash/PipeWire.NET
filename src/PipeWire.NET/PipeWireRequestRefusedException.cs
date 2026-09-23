namespace PipeWire.NET;

/// <summary>The daemon considered the request and refused it; the connection is unaffected.</summary>
public sealed class PipeWireRequestRefusedException : PipeWireException
{
    /// <inheritdoc cref="PipeWireException(string, int, uint?, string?)"/>
    public PipeWireRequestRefusedException(
        string operation,
        int result,
        uint? objectId = null,
        string? daemonMessage = null
    )
        : base(operation, result, objectId, daemonMessage) { }

    /// <inheritdoc/>
    public PipeWireRequestRefusedException()
        : base() { }

    /// <inheritdoc/>
    public PipeWireRequestRefusedException(string message)
        : base(message) { }

    /// <inheritdoc/>
    public PipeWireRequestRefusedException(string message, Exception innerException)
        : base(message, innerException) { }
}
