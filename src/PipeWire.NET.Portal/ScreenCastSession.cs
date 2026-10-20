using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace PipeWire.NET.Portal;

/// <summary>
/// A started screen cast: the streams the user agreed to share, and the PipeWire remote they are on.
/// Disposing it ends the session, which stops the streams.
/// </summary>
public sealed partial class ScreenCastSession : IAsyncDisposable
{
    private readonly DBusConnection _connection;
    private readonly ILogger<ScreenCastSession> _logger;
    private readonly TaskCompletionSource _closed = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private IDisposable? _closedWatch;
    private int _disposed;

    internal ScreenCastSession(
        DBusConnection connection,
        string handle,
        ILogger<ScreenCastSession> logger
    )
    {
        _connection = connection;
        Handle = handle;
        _logger = logger;
    }

    /// <summary>The session's object path on the portal.</summary>
    public string Handle { get; }

    /// <summary>The streams, one per source the user picked.</summary>
    public ImmutableArray<ScreenCastStream> Streams { get; private set; } = [];

    /// <summary>
    /// The token that shares the same sources again without asking
    /// (<see cref="ScreenCastRequest.RestoreToken"/>); null when the choice is not remembered. Each token
    /// is good for one restore.
    /// </summary>
    public string? RestoreToken { get; private set; }

    /// <summary>
    /// Completes when the portal ends the session: the user stopped sharing, or the compositor did.
    /// Disposing the session does not complete it.
    /// </summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Opens the PipeWire remote the streams are on, restricted to them. Pass it to
    /// <c>PipeWireContext.StartOnFdAsync</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The remote's descriptor, owned by the caller.</returns>
    public async Task<SafeHandle> OpenPipeWireRemoteAsync(
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        MessageWriter writer = _connection.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(
                destination: PortalRequests.Service,
                path: PortalRequests.ObjectPath,
                @interface: ScreenCastPortal.Interface,
                member: "OpenPipeWireRemote",
                signature: "oa{sv}"
            );
            writer.WriteObjectPath(Handle);
            PortalRequests.WriteOptions(ref writer, []);
            call = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }

        return await _connection
            .CallMethodAsync(
                call,
                static (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>()
            )
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Ends the session on the portal.</summary>
    /// <returns>A task that completes when the portal has been told.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _closedWatch?.Dispose();
        if (!_closed.Task.IsCompleted)
        {
            await PortalRequests
                .CloseAsync(_connection, Handle, PortalRequests.SessionInterface)
                .ConfigureAwait(false);
            LogEnded(Handle);
        }
    }

    internal async Task WatchClosedAsync() =>
        _closedWatch = await _connection
            .WatchSignalAsync(
                sender: null,
                Handle,
                PortalRequests.SessionInterface,
                "Closed",
                (Notification notification) =>
                {
                    if (notification.Type == NotificationType.Value && _closed.TrySetResult())
                    {
                        LogClosedByPortal(Handle);
                    }
                },
                ObserverFlags.None,
                emitOnCapturedContext: false
            )
            .ConfigureAwait(false);

    internal void Started(ScreenCastStart start)
    {
        Streams = start.Streams;
        RestoreToken = start.RestoreToken;
    }

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Information,
        Message = "Screen cast session {Session} ended"
    )]
    private partial void LogEnded(string session);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Information,
        Message = "Screen cast session {Session} was closed by the portal"
    )]
    private partial void LogClosedByPortal(string session);
}
