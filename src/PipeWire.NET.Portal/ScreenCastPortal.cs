using System.Collections.Immutable;
using System.Drawing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tmds.DBus.Protocol;

namespace PipeWire.NET.Portal;

/// <summary>
/// The xdg-desktop-portal ScreenCast interface: asks the user which screens or windows to share, and
/// hands back the PipeWire nodes and the remote to stream them from. How screen capture works on
/// Wayland, and inside a Flatpak sandbox on any session.
/// </summary>
/// <example>
/// <code>
/// await using ScreenCastPortal portal = await ScreenCastPortal.ConnectAsync();
/// await using ScreenCastSession session = await portal.StartAsync(new() { Sources = ScreenCastSources.Monitor });
/// using SafeHandle remote = await session.OpenPipeWireRemoteAsync();
/// await context.StartOnFdAsync(remote);
/// // connect a PipeWireVideoCapture to session.Streams[0].NodeId
/// </code>
/// </example>
public sealed partial class ScreenCastPortal : IAsyncDisposable
{
    internal const string Interface = "org.freedesktop.portal.ScreenCast";

    private readonly DBusConnection _connection;
    private readonly bool _ownsConnection;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ScreenCastPortal> _logger;

    private ScreenCastPortal(
        DBusConnection connection,
        bool ownsConnection,
        ILoggerFactory loggerFactory,
        uint version,
        ScreenCastSources sources,
        ScreenCastCursorModes cursorModes
    )
    {
        _connection = connection;
        _ownsConnection = ownsConnection;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ScreenCastPortal>();
        Version = version;
        AvailableSources = sources;
        AvailableCursorModes = cursorModes;
    }

    /// <summary>The version of the portal's ScreenCast interface.</summary>
    public uint Version { get; }

    /// <summary>The kinds of source the portal can share.</summary>
    public ScreenCastSources AvailableSources { get; }

    /// <summary>The cursor modes the portal supports.</summary>
    public ScreenCastCursorModes AvailableCursorModes { get; }

    /// <summary>Connects to the portal on the session bus, on a connection of its own.</summary>
    /// <param name="loggerFactory">Where the portal and its sessions log.</param>
    /// <param name="cancellationToken">Cancels connecting.</param>
    /// <returns>The portal.</returns>
    /// <exception cref="PortalException">No session bus, or no ScreenCast portal on it.</exception>
    public static async Task<ScreenCastPortal> ConnectAsync(
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        string address =
            DBusAddress.Session
            ?? throw new PortalException(
                "No D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set)."
            );
        DBusConnection connection = new(address);
        try
        {
            await connection
                .ConnectAsync()
                .AsTask()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return await ReadAsync(
                    connection,
                    ownsConnection: true,
                    loggerFactory,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Uses the portal over a connection of the application's, which it leaves open.</summary>
    /// <param name="connection">A connected session bus connection.</param>
    /// <param name="loggerFactory">Where the portal and its sessions log.</param>
    /// <param name="cancellationToken">Cancels reading the portal's capabilities.</param>
    /// <returns>The portal.</returns>
    /// <exception cref="PortalException">No ScreenCast portal on the bus.</exception>
    public static Task<ScreenCastPortal> CreateAsync(
        DBusConnection connection,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ReadAsync(connection, ownsConnection: false, loggerFactory, cancellationToken);
    }

    /// <summary>
    /// Asks the user what to share, through the portal's dialog, and starts the session: CreateSession,
    /// SelectSources and Start.
    /// </summary>
    /// <param name="request">What to offer the user.</param>
    /// <param name="cancellationToken">Cancels the request and dismisses the dialog.</param>
    /// <returns>The started session, with its streams.</returns>
    /// <exception cref="PortalException">The user cancelled, or the portal refused.</exception>
    public async Task<ScreenCastSession> StartAsync(
        ScreenCastRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        string sessionToken = PortalRequests.NewToken();
        string token = PortalRequests.NewToken();
        Dictionary<string, VariantValue> created = await PortalRequests
            .RequestAsync(
                _connection,
                CreateSessionMessage(token, sessionToken),
                "creating a screen cast session",
                cancellationToken
            )
            .ConfigureAwait(false);
        string session =
            PortalRequests.String(created, "session_handle")
            ?? PortalRequests.SessionPath(_connection, sessionToken);

        ScreenCastSession started = new(
            _connection,
            session,
            _loggerFactory.CreateLogger<ScreenCastSession>()
        );
        try
        {
            await started.WatchClosedAsync().ConfigureAwait(false);
            token = PortalRequests.NewToken();
            _ = await PortalRequests
                .RequestAsync(
                    _connection,
                    SelectSourcesMessage(token, session, request),
                    "selecting screen cast sources",
                    cancellationToken
                )
                .ConfigureAwait(false);

            token = PortalRequests.NewToken();
            Dictionary<string, VariantValue> results = await PortalRequests
                .RequestAsync(
                    _connection,
                    StartMessage(token, session, request.ParentWindow),
                    "sharing the screen",
                    cancellationToken
                )
                .ConfigureAwait(false);
            ScreenCastStart start = ReadStart(results);
            started.Started(start);
            LogStarted(session, start.Streams.Length, start.RestoreToken is not null);
            return started;
        }
        catch (Exception exception)
        {
            LogStartFailed(exception, session);
            await started.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Closes the connection when the portal made it; sessions on it end with it.</summary>
    /// <returns>A task that completes when closed.</returns>
    public ValueTask DisposeAsync()
    {
        if (_ownsConnection)
        {
            _connection.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    internal static ScreenCastStart ReadStart(Dictionary<string, VariantValue> results)
    {
        ImmutableArray<ScreenCastStream>.Builder streams =
            ImmutableArray.CreateBuilder<ScreenCastStream>();
        if (
            results.TryGetValue("streams", out VariantValue list)
            && list.Type == VariantValueType.Array
        )
        {
            for (int i = 0; i < list.Count; i++)
            {
                VariantValue entry = list.GetItem(i);
                Dictionary<string, VariantValue> properties = entry
                    .GetItem(1)
                    .GetDictionary<string, VariantValue>();
                streams.Add(
                    new ScreenCastStream(entry.GetItem(0).GetUInt32())
                    {
                        Id = PortalRequests.String(properties, "id"),
                        Position = Pair(properties, "position") is (int x, int y)
                            ? new Point(x, y)
                            : null,
                        Size = Pair(properties, "size") is (int w, int h) ? new Size(w, h) : null,
                        Source = (ScreenCastSources)(
                            PortalRequests.UInt32(properties, "source_type") ?? 0
                        ),
                        MappingId = PortalRequests.String(properties, "mapping_id"),
                        Serial =
                            properties.TryGetValue("pipewire-serial", out VariantValue serial)
                            && serial.Type == VariantValueType.UInt64
                                ? serial.GetUInt64()
                                : null,
                    }
                );
            }
        }

        return new ScreenCastStart(
            streams.DrainToImmutable(),
            PortalRequests.String(results, "restore_token")
        );
    }

    private static (int, int)? Pair(Dictionary<string, VariantValue> properties, string key) =>
        properties.TryGetValue(key, out VariantValue value)
        && value.Type == VariantValueType.Struct
        && value.Count == 2
            ? (value.GetItem(0).GetInt32(), value.GetItem(1).GetInt32())
            : null;

    private static async Task<ScreenCastPortal> ReadAsync(
        DBusConnection connection,
        bool ownsConnection,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken
    )
    {
        try
        {
            uint version = (
                await PortalRequests
                    .GetPropertyAsync(connection, Interface, "version")
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false)
            ).GetUInt32();
            uint sources = (
                await PortalRequests
                    .GetPropertyAsync(connection, Interface, "AvailableSourceTypes")
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false)
            ).GetUInt32();
            uint cursors = (
                await PortalRequests
                    .GetPropertyAsync(connection, Interface, "AvailableCursorModes")
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false)
            ).GetUInt32();
            return new ScreenCastPortal(
                connection,
                ownsConnection,
                loggerFactory ?? NullLoggerFactory.Instance,
                version,
                (ScreenCastSources)sources,
                (ScreenCastCursorModes)cursors
            );
        }
        catch (DBusExceptionBase exception)
        {
            throw new PortalException(
                "No ScreenCast portal answered on the session bus.",
                exception
            );
        }
    }

    private MessageBuffer CreateSessionMessage(string token, string sessionToken)
    {
        MessageWriter writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: PortalRequests.Service,
                path: PortalRequests.ObjectPath,
                @interface: Interface,
                member: "CreateSession",
                signature: "a{sv}"
            );
            PortalRequests.WriteOptions(
                ref writer,
                [new("handle_token", token), new("session_handle_token", sessionToken)]
            );
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private MessageBuffer SelectSourcesMessage(
        string token,
        string session,
        ScreenCastRequest request
    )
    {
        List<KeyValuePair<string, VariantValue>> options =
        [
            new("handle_token", token),
            new("types", (uint)request.Sources),
            new("multiple", request.Multiple),
        ];
        if (request.CursorMode is { } cursor)
        {
            options.Add(new("cursor_mode", (uint)cursor));
        }

        // Version 4 added restoring; earlier portals reject unknown options' types, so they are only sent
        // when asked for.
        if (request.Persistence != ScreenCastPersistence.None)
        {
            options.Add(new("persist_mode", (uint)request.Persistence));
        }

        if (request.RestoreToken is { } restore)
        {
            options.Add(new("restore_token", restore));
        }

        MessageWriter writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: PortalRequests.Service,
                path: PortalRequests.ObjectPath,
                @interface: Interface,
                member: "SelectSources",
                signature: "oa{sv}"
            );
            writer.WriteObjectPath(session);
            PortalRequests.WriteOptions(
                ref writer,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(options)
            );
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private MessageBuffer StartMessage(string token, string session, string parentWindow)
    {
        MessageWriter writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: PortalRequests.Service,
                path: PortalRequests.ObjectPath,
                @interface: Interface,
                member: "Start",
                signature: "osa{sv}"
            );
            writer.WriteObjectPath(session);
            writer.WriteString(parentWindow);
            PortalRequests.WriteOptions(ref writer, [new("handle_token", token)]);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Screen cast session {Session} started with {Streams} stream(s), restorable: {Restorable}"
    )]
    private partial void LogStarted(string session, int streams, bool restorable);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Screen cast session {Session} did not start"
    )]
    private partial void LogStartFailed(Exception exception, string session);
}
