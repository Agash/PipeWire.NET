using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace PipeWire.NET.Portal;

/// <summary>
/// The xdg-desktop-portal Camera interface: asks the user for camera access and hands back a PipeWire
/// remote that shows only the cameras. How a sandboxed application reaches cameras.
/// </summary>
/// <example>
/// <code>
/// await using CameraPortal portal = await CameraPortal.ConnectAsync();
/// if (portal.IsCameraPresent)
/// {
///     using SafeHandle remote = await portal.OpenCameraRemoteAsync();
///     await context.StartOnFdAsync(remote);
/// }
/// </code>
/// </example>
public sealed partial class CameraPortal : IAsyncDisposable
{
    private const string Interface = "org.freedesktop.portal.Camera";

    private readonly DBusConnection _connection;
    private readonly bool _ownsConnection;
    private readonly ILogger<CameraPortal> _logger;

    private CameraPortal(
        DBusConnection connection,
        bool ownsConnection,
        ILogger<CameraPortal> logger,
        uint version,
        bool isCameraPresent
    )
    {
        _connection = connection;
        _ownsConnection = ownsConnection;
        _logger = logger;
        Version = version;
        IsCameraPresent = isCameraPresent;
    }

    /// <summary>The version of the portal's Camera interface.</summary>
    public uint Version { get; }

    /// <summary>Whether a camera was present when the portal was connected.</summary>
    public bool IsCameraPresent { get; }

    /// <summary>Connects to the portal on the session bus, on a connection of its own.</summary>
    /// <param name="loggerFactory">Where the portal logs.</param>
    /// <param name="cancellationToken">Cancels connecting.</param>
    /// <returns>The portal.</returns>
    /// <exception cref="PortalException">No session bus, or no Camera portal on it.</exception>
    public static async Task<CameraPortal> ConnectAsync(
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
    /// <param name="loggerFactory">Where the portal logs.</param>
    /// <param name="cancellationToken">Cancels reading the portal's properties.</param>
    /// <returns>The portal.</returns>
    /// <exception cref="PortalException">No Camera portal on the bus.</exception>
    public static Task<CameraPortal> CreateAsync(
        DBusConnection connection,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ReadAsync(connection, ownsConnection: false, loggerFactory, cancellationToken);
    }

    /// <summary>
    /// Asks the user for camera access (AccessCamera), then opens the PipeWire remote that shows the
    /// cameras (OpenPipeWireRemote). Pass it to <c>PipeWireContext.StartOnFdAsync</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request and dismisses the dialog.</param>
    /// <returns>The remote's descriptor, owned by the caller.</returns>
    /// <exception cref="PortalException">The user refused, or the portal did.</exception>
    public async Task<SafeHandle> OpenCameraRemoteAsync(
        CancellationToken cancellationToken = default
    )
    {
        string token = PortalRequests.NewToken();
        _ = await PortalRequests
            .RequestAsync(
                _connection,
                AccessCameraMessage(token),
                "camera access",
                cancellationToken
            )
            .ConfigureAwait(false);
        LogAccessGranted();

        MessageWriter writer = _connection.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(
                destination: PortalRequests.Service,
                path: PortalRequests.ObjectPath,
                @interface: Interface,
                member: "OpenPipeWireRemote",
                signature: "a{sv}"
            );
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

    /// <summary>Closes the connection when the portal made it.</summary>
    /// <returns>A task that completes when closed.</returns>
    public ValueTask DisposeAsync()
    {
        if (_ownsConnection)
        {
            _connection.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static async Task<CameraPortal> ReadAsync(
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
            bool present = (
                await PortalRequests
                    .GetPropertyAsync(connection, Interface, "IsCameraPresent")
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false)
            ).GetBool();
            return new CameraPortal(
                connection,
                ownsConnection,
                (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<CameraPortal>(),
                version,
                present
            );
        }
        catch (DBusExceptionBase exception)
        {
            throw new PortalException("No Camera portal answered on the session bus.", exception);
        }
    }

    private MessageBuffer AccessCameraMessage(string token)
    {
        MessageWriter writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: PortalRequests.Service,
                path: PortalRequests.ObjectPath,
                @interface: Interface,
                member: "AccessCamera",
                signature: "a{sv}"
            );
            PortalRequests.WriteOptions(ref writer, [new("handle_token", token)]);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    [LoggerMessage(
        EventId = 21,
        Level = LogLevel.Information,
        Message = "Camera access granted by the portal"
    )]
    private partial void LogAccessGranted();
}
