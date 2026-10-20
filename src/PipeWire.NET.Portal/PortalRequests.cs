using System.Globalization;
using System.Security.Cryptography;
using Tmds.DBus.Protocol;

namespace PipeWire.NET.Portal;

// The request/response pattern every interactive portal method follows: the method returns the object
// path of a Request at once, and the answer arrives later as that Request's Response signal.
internal static class PortalRequests
{
    public const string Service = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";
    public const string RequestInterface = "org.freedesktop.portal.Request";
    public const string SessionInterface = "org.freedesktop.portal.Session";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    // A handle token: a valid object path element, unique enough that this connection's requests and
    // sessions never collide.
    public static string NewToken() =>
        "pipewire_net_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    // The path the portal gives a session made with a token:
    // /org/freedesktop/portal/desktop/session/SENDER/TOKEN, the sender being the caller's unique name
    // with the leading ':' removed and '.' replaced by '_'.
    public static string SessionPath(DBusConnection connection, string token) =>
        $"{ObjectPath}/session/{Sender(connection)}/{token}";

    // Sends the call and waits for its Response. Every Response under this connection's request
    // namespace is watched from before the call, and routed by path once the call returns it: a portal
    // before 0.9 chooses the path itself and may answer before the reply naming it arrives, so a
    // Response for a path not yet known is kept. Cancellation closes the request, which dismisses its
    // dialog.
    public static async Task<Dictionary<string, VariantValue>> RequestAsync(
        DBusConnection connection,
        MessageBuffer call,
        string what,
        CancellationToken cancellationToken
    )
    {
        ResponseWaiter waiter = new();
        using IDisposable watch = await connection
            .AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Interface = RequestInterface,
                    Member = "Response",
                    PathNamespace = $"{ObjectPath}/request/{Sender(connection)}",
                },
                static (message, _) =>
                {
                    Reader reader = message.GetBodyReader();
                    PortalResponse response = (PortalResponse)reader.ReadUInt32();
                    return (
                        message.PathAsString ?? string.Empty,
                        response,
                        reader.ReadDictionaryOfStringToVariantValue()
                    );
                },
                waiter.Receive,
                emitOnCapturedContext: false
            )
            .ConfigureAwait(false);
        string path = await connection
            .CallMethodAsync(
                call,
                static (message, _) => message.GetBodyReader().ReadObjectPathAsString()
            )
            .ConfigureAwait(false);
        waiter.Expect(path);

        await using CancellationTokenRegistration closing = cancellationToken.Register(() =>
        {
            waiter.Answered.TrySetCanceled(cancellationToken);
            _ = CloseAsync(connection, path, RequestInterface);
        });
        (PortalResponse response, Dictionary<string, VariantValue> results) =
            await waiter.Answered.Task.ConfigureAwait(false);
        return response == PortalResponse.Success
            ? results
            : throw new PortalException(
                response,
                response == PortalResponse.Cancelled
                    ? $"The user cancelled {what}."
                    : $"The portal refused {what}."
            );
    }

    // Calls Close on a Request or Session. Fire and forget from a cancellation callback: a request that
    // has already answered, or a peer that has gone, has nothing left to close.
    public static async Task CloseAsync(DBusConnection connection, string path, string @interface)
    {
        try
        {
            await connection
                .CallMethodAsync(CloseMessage(connection, path, @interface))
                .ConfigureAwait(false);
        }
        catch (DBusExceptionBase)
        {
            // Deliberately not logged: closing what the portal already ended is not a fault.
        }
    }

    // Reads one property of a portal interface (org.freedesktop.DBus.Properties.Get).
    public static Task<VariantValue> GetPropertyAsync(
        DBusConnection connection,
        string @interface,
        string property
    )
    {
        MessageWriter writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: Service,
                path: ObjectPath,
                @interface: PropertiesInterface,
                member: "Get",
                signature: "ss"
            );
            writer.WriteString(@interface);
            writer.WriteString(property);
            return connection.CallMethodAsync(
                writer.CreateMessage(),
                static (message, _) => message.GetBodyReader().ReadVariantValue()
            );
        }
        finally
        {
            writer.Dispose();
        }
    }

    // Writes an a{sv} of options.
    public static void WriteOptions(
        ref MessageWriter writer,
        scoped ReadOnlySpan<KeyValuePair<string, VariantValue>> options
    )
    {
        ArrayStart start = writer.WriteDictionaryStart();
        foreach (KeyValuePair<string, VariantValue> option in options)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(option.Key);
            writer.WriteVariant(option.Value);
        }

        writer.WriteDictionaryEnd(start);
    }

    private static MessageBuffer CloseMessage(
        DBusConnection connection,
        string path,
        string @interface
    )
    {
        MessageWriter writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: Service,
                path: path,
                @interface: @interface,
                member: "Close"
            );
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static string Sender(DBusConnection connection) =>
        (
            connection.UniqueName
            ?? throw new InvalidOperationException("The D-Bus connection is not connected.")
        )
            .TrimStart(':')
            .Replace('.', '_');

    // Reads a uint result that a portal may omit.
    public static uint? UInt32(Dictionary<string, VariantValue> results, string key) =>
        results.TryGetValue(key, out VariantValue value) && value.Type == VariantValueType.UInt32
            ? value.GetUInt32()
            : null;

    public static string? String(Dictionary<string, VariantValue> results, string key) =>
        results.TryGetValue(key, out VariantValue value) && value.Type == VariantValueType.String
            ? value.GetString()
            : null;

    public static string Invariant(uint value) => value.ToString(CultureInfo.InvariantCulture);

    // Holds the Response for a request whose path the call has not returned yet.
    private sealed class ResponseWaiter
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<
            string,
            (PortalResponse, Dictionary<string, VariantValue>)
        > _early = [];
        private string? _path;

        public TaskCompletionSource<(
            PortalResponse,
            Dictionary<string, VariantValue>
        )> Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Receive(
            Notification<(
                string Path,
                PortalResponse Response,
                Dictionary<string, VariantValue> Results
            )> notification
        )
        {
            if (!notification.HasValue)
            {
                if (notification.Type != NotificationType.ObserverDisposed)
                {
                    Answered.TrySetException(
                        new PortalException(
                            "The connection to the portal ended.",
                            notification.Exception
                        )
                    );
                }

                return;
            }

            (string path, PortalResponse response, Dictionary<string, VariantValue> results) =
                notification.Value;
            lock (_gate)
            {
                if (_path is null)
                {
                    _early[path] = (response, results);
                    return;
                }

                if (path != _path)
                {
                    return;
                }
            }

            Answered.TrySetResult((response, results));
        }

        public void Expect(string path)
        {
            (PortalResponse, Dictionary<string, VariantValue>)? early;
            lock (_gate)
            {
                _path = path;
                early = _early.Remove(path, out var answer) ? answer : null;
                _early.Clear();
            }

            if (early is { } value)
            {
                Answered.TrySetResult(value);
            }
        }
    }
}
