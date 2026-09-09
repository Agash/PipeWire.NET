namespace PipeWire.NET;

/// <summary>A failure of the connection rather than of any one request.</summary>
public class PipeWireConnectionException : PipeWireException
{
    /// <inheritdoc cref="PipeWireException(string, int, uint?, string?)"/>
    public PipeWireConnectionException(string operation, int result, uint? objectId = null, string? daemonMessage = null)
        : base(operation, result, objectId, daemonMessage)
    {
    }

    /// <inheritdoc/>
    public PipeWireConnectionException() : base()
    {
    }

    /// <inheritdoc/>
    public PipeWireConnectionException(string message) : base(message)
    {
    }

    /// <inheritdoc/>
    public PipeWireConnectionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

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

/// <summary>A connection that was working has gone; objects bound through it are dead.</summary>
public sealed class PipeWireConnectionClosedException : PipeWireConnectionException
{
    /// <inheritdoc cref="PipeWireException(string, int, uint?, string?)"/>
    public PipeWireConnectionClosedException(string operation, int result, uint? objectId = null, string? daemonMessage = null)
        : base(operation, result, objectId, daemonMessage)
    {
    }

    /// <inheritdoc/>
    public PipeWireConnectionClosedException() : base()
    {
    }

    /// <inheritdoc/>
    public PipeWireConnectionClosedException(string message) : base(message)
    {
    }

    /// <inheritdoc/>
    public PipeWireConnectionClosedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The daemon considered the request and refused it; the connection is unaffected.</summary>
public sealed class PipeWireRequestRefusedException : PipeWireException
{
    /// <inheritdoc cref="PipeWireException(string, int, uint?, string?)"/>
    public PipeWireRequestRefusedException(string operation, int result, uint? objectId = null, string? daemonMessage = null)
        : base(operation, result, objectId, daemonMessage)
    {
    }

    /// <inheritdoc/>
    public PipeWireRequestRefusedException() : base()
    {
    }

    /// <inheritdoc/>
    public PipeWireRequestRefusedException(string message) : base(message)
    {
    }

    /// <inheritdoc/>
    public PipeWireRequestRefusedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

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
