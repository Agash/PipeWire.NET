using System.Globalization;

namespace PipeWire.NET;

/// <summary>
/// A PipeWire operation that failed, with what the daemon said about it.
/// </summary>
/// <remarks>
/// A caller acts on the operation, the object and the errno, not on the message. <c>-13</c> is a
/// permission refusal that no retry fixes; <c>-32</c> is a dead connection that reconnecting might.
/// Formatting those into a sentence loses the distinction.
/// </remarks>
public sealed class PipeWireException : InvalidOperationException
{
    /// <param name="operation">The native call, such as <c>pw_core_sync</c>.</param>
    /// <param name="result">The result code, negative for a failure and normally a negative errno.</param>
    /// <param name="objectId">The global the operation was against, if it had one.</param>
    /// <param name="daemonMessage">What the daemon reported, if it reported anything.</param>
    public PipeWireException(string operation, int result, uint? objectId = null, string? daemonMessage = null)
        : base(Describe(operation, result, objectId, daemonMessage))
    {
        Operation = operation;
        Result = result;
        ObjectId = objectId;
        DaemonMessage = daemonMessage;
    }

    /// <inheritdoc/>
    public PipeWireException()
        : this("unknown", 0)
    {
    }

    /// <inheritdoc/>
    public PipeWireException(string message)
        : base(message)
    {
        Operation = "unknown";
    }

    /// <inheritdoc/>
    public PipeWireException(string message, Exception innerException)
        : base(message, innerException)
    {
        Operation = "unknown";
    }

    /// <summary>The native call that failed, such as <c>pw_node_set_param</c>.</summary>
    public string Operation { get; }

    /// <summary>The result code. Negative values are normally negated errno.</summary>
    public int Result { get; }

    /// <summary>The global the operation was against, where one applies.</summary>
    public uint? ObjectId { get; }

    /// <summary>What the daemon said, where it said anything.</summary>
    public string? DaemonMessage { get; }

    /// <summary>True when the daemon refused for want of permission (<c>-EACCES</c>).</summary>
    public bool IsPermissionDenied => Result == -13;

    /// <summary>True when the connection is gone (<c>-EPIPE</c>).</summary>
    public bool IsDisconnected => Result == -32;

    /// <summary>Throws if <paramref name="result"/> reports a failure.</summary>
    internal static void ThrowIfFailed(int result, string operation, uint? objectId = null)
    {
        if (result < 0) throw new PipeWireException(operation, result, objectId);
    }

    private static string Describe(string operation, int result, uint? objectId, string? daemonMessage)
    {
        var text = new System.Text.StringBuilder(operation);

        if (objectId is { } id)
            text.Append(CultureInfo.InvariantCulture, $" on object {id}");

        text.Append(CultureInfo.InvariantCulture, $" failed with {result}");

        string? name = result switch
        {
            -1 => "EPERM",
            -2 => "ENOENT",
            -13 => "EACCES",
            -22 => "EINVAL",
            -32 => "EPIPE",
            -38 => "ENOSYS",
            _ => null,
        };

        if (name is not null) text.Append(CultureInfo.InvariantCulture, $" ({name})");
        if (daemonMessage is not null) text.Append(CultureInfo.InvariantCulture, $": {daemonMessage}");

        return text.ToString();
    }
}
