namespace PipeWire.NET;

/// <summary>A libpipewire call in this process failed before the daemon was asked.</summary>
public sealed class PipeWireInteropException : PipeWireException
{
    /// <inheritdoc cref="PipeWireException(string, int, uint?, string?)"/>
    public PipeWireInteropException(string operation, int result, uint? objectId = null, string? daemonMessage = null)
        : base(operation, result, objectId, daemonMessage)
    {
    }

    /// <inheritdoc/>
    public PipeWireInteropException() : base()
    {
    }

    /// <inheritdoc/>
    public PipeWireInteropException(string message) : base(message)
    {
    }

    /// <inheritdoc/>
    public PipeWireInteropException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
