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
/// <remarks>
/// Derived from <see cref="Exception"/> rather than <see cref="InvalidOperationException"/>: a
/// daemon refusal is not a caller state error, and sharing a base with one means every
/// <c>catch (InvalidOperationException)</c> written to contain a local bug also swallows a
/// permission refusal or a dropped connection.
/// </remarks>
public sealed class PipeWireException : Exception
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
    /// <remarks>
    /// Zero means no daemon result was available, not success: the message-only constructors exist
    /// for the standard exception shape and carry no code.
    /// </remarks>
    public int Result { get; }

    /// <summary>The global the operation was against, where one applies.</summary>
    public uint? ObjectId { get; }

    /// <summary>What the daemon said, where it said anything.</summary>
    public string? DaemonMessage { get; }

    /// <summary>True when the daemon refused for want of permission (<c>-EACCES</c> or <c>-EPERM</c>).</summary>
    /// <remarks>
    /// Both codes are refusals a retry does not fix. The daemon uses EACCES for a permission bit the
    /// client does not hold and EPERM for an operation it may not perform at all, and a caller
    /// branching on "was I allowed" wants the same answer for each.
    /// </remarks>
    public bool IsPermissionDenied => Result is -13 or -1;

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

        string? name = ErrnoName(result);

        if (name is not null) text.Append(CultureInfo.InvariantCulture, $" ({name})");
        if (daemonMessage is not null) text.Append(CultureInfo.InvariantCulture, $": {daemonMessage}");

        return text.ToString();
    }

    /// <summary>The symbolic name for a negated errno, or null when it is not one this maps.</summary>
    /// <remarks>
    /// The codes PipeWire itself returns from its own paths, plus the ones the kernel hands back
    /// through them. Not the whole of errno.h: a name is only worth printing where it tells a reader
    /// something the number does not, and an unmapped code still prints as a number.
    /// </remarks>
    private static string? ErrnoName(int result) => result switch
    {
        -1 => "EPERM",
        -2 => "ENOENT",
        -4 => "EINTR",
        -5 => "EIO",
        -9 => "EBADF",
        -11 => "EAGAIN",
        -12 => "ENOMEM",
        -13 => "EACCES",
        -14 => "EFAULT",
        -16 => "EBUSY",
        -17 => "EEXIST",
        -19 => "ENODEV",
        -22 => "EINVAL",
        -24 => "EMFILE",
        -25 => "ENOTTY",
        -28 => "ENOSPC",
        -32 => "EPIPE",
        -34 => "ERANGE",
        -38 => "ENOSYS",
        -39 => "ENOTEMPTY",
        -71 => "EPROTO",
        -74 => "EBADMSG",
        -75 => "EOVERFLOW",
        -84 => "EILSEQ",
        -88 => "ENOTSOCK",
        -90 => "EMSGSIZE",
        -93 => "EPROTONOSUPPORT",
        -95 => "EOPNOTSUPP",
        -98 => "EADDRINUSE",
        -103 => "ECONNABORTED",
        -104 => "ECONNRESET",
        -105 => "ENOBUFS",
        -107 => "ENOTCONN",
        -108 => "ESHUTDOWN",
        -110 => "ETIMEDOUT",
        -111 => "ECONNREFUSED",
        -125 => "ECANCELED",
        _ => null,
    };
}
