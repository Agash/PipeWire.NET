using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Tmds.DBus.Protocol;

namespace PipeWire.NET.Tests;

// A private message bus (dbus-daemon) with a stand-in for xdg-desktop-portal on it: the ScreenCast and
// Camera interfaces answering as the real portal does, with each step's answer set by the test.
internal sealed class FakePortal : IPathMethodHandler, IAsyncDisposable
{
    private const string Desktop = "/org/freedesktop/portal/desktop";

    private readonly Process _daemon;
    private readonly DBusConnection _service;
    private readonly ConcurrentQueue<string> _calls = new();
    private readonly ConcurrentQueue<string> _closed = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, VariantValue>> _options =
        new();
    private readonly AnonymousPipeServerStream _remote = new(PipeDirection.Out);

    private string? _config;

    private FakePortal(Process daemon, string address, DBusConnection service)
    {
        _daemon = daemon;
        Address = address;
        _service = service;
    }

    public string Address { get; }

    // How each interactive step answers; 0 succeeds, 1 is the user cancelling, null never answers.
    public uint? CreateSessionResponse { get; set; } = 0;

    public uint? SelectSourcesResponse { get; set; } = 0;

    public uint? StartResponse { get; set; } = 0;

    public uint? AccessCameraResponse { get; set; } = 0;

    // Answer with a request path of the portal's own choosing, as portals before 0.9 did.
    public bool MovesRequests { get; set; }

    public bool CameraPresent { get; set; } = true;

    public string? RestoreToken { get; set; } = "restore-1";

    public string? SessionHandle { get; private set; }

    // Every call, as "Interface.Member".
    public IReadOnlyCollection<string> Calls => _calls;

    // Every object Close was called on.
    public IReadOnlyCollection<string> Closed => _closed;

    // The bytes written here come out of the descriptor OpenPipeWireRemote handed over.
    public Stream RemoteWriter => _remote;

    public string Path => Desktop;

    public bool HandlesChildPaths => true;

    public static async Task<FakePortal?> StartAsync(bool claimName)
    {
        string? daemonPath = new[] { "/usr/bin/dbus-daemon", "/bin/dbus-daemon" }.FirstOrDefault(
            File.Exists
        );
        if (!OperatingSystem.IsLinux() || daemonPath is null)
        {
            return null;
        }

        // A bus of its own with no service directories, so nothing on it is activated: the session
        // configuration would start the desktop's real portal for a name the stand-in has not claimed.
        string config = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"pipewire-net-portal-{Environment.ProcessId}-{Guid.NewGuid():N}.conf"
        );
        await File.WriteAllTextAsync(
                config,
                """
                <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
                 "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
                <busconfig>
                  <type>session</type>
                  <listen>unix:tmpdir=/tmp</listen>
                  <auth>EXTERNAL</auth>
                  <policy context="default">
                    <allow send_destination="*" eavesdrop="true"/>
                    <allow eavesdrop="true"/>
                    <allow own="*"/>
                  </policy>
                </busconfig>
                """
            )
            .ConfigureAwait(false);
        Process daemon = Process.Start(
            new ProcessStartInfo(
                daemonPath,
                $"--config-file={config} --nofork --nosyslog --print-address=1"
            )
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }
        )!;
        string address = (
            await daemon.StandardOutput.ReadLineAsync().ConfigureAwait(false)
        )!.Trim();
        DBusConnection service = new(address);
        await service.ConnectAsync().ConfigureAwait(false);
        FakePortal portal = new(daemon, address, service) { _config = config };
        service.AddMethodHandler(portal);
        if (claimName)
        {
            await service.RequestNameAsync("org.freedesktop.portal.Desktop").ConfigureAwait(false);
        }

        return portal;
    }

    public Dictionary<string, VariantValue> OptionsOf(string member) =>
        _options.TryGetValue(member, out Dictionary<string, VariantValue>? options) ? options : [];

    // The portal ends the session, as when the user stops sharing.
    public void CloseSession()
    {
        MessageWriter writer = _service.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(
                path: SessionHandle,
                @interface: "org.freedesktop.portal.Session",
                member: "Closed",
                signature: "a{sv}"
            );
            ArrayStart details = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(details);
            _ = _service.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        Message request = context.Request;
        string member = request.MemberAsString ?? string.Empty;
        string @interface = request.InterfaceAsString ?? string.Empty;
        _calls.Enqueue($"{@interface}.{member}");
        Reader reader = request.GetBodyReader();
        switch (@interface, member)
        {
            case ("org.freedesktop.DBus.Properties", "Get"):
                _ = reader.ReadString();
                ReplyProperty(context, reader.ReadString());
                break;
            case ("org.freedesktop.portal.Request" or "org.freedesktop.portal.Session", "Close"):
                _closed.Enqueue(request.PathAsString ?? string.Empty);
                Reply(context, null);
                break;
            case ("org.freedesktop.portal.ScreenCast", "CreateSession"):
            {
                Dictionary<string, VariantValue> options = Record(
                    member,
                    reader.ReadDictionaryOfStringToVariantValue()
                );
                SessionHandle =
                    $"{Desktop}/session/{SenderOf(request)}/{options["session_handle_token"].GetString()}";
                Respond(
                    context,
                    request,
                    options,
                    CreateSessionResponse,
                    (ref MessageWriter w) => WriteString(ref w, "session_handle", SessionHandle)
                );
                break;
            }
            case ("org.freedesktop.portal.ScreenCast", "SelectSources"):
            {
                _ = reader.ReadObjectPathAsString();
                Respond(
                    context,
                    request,
                    Record(member, reader.ReadDictionaryOfStringToVariantValue()),
                    SelectSourcesResponse,
                    null
                );
                break;
            }
            case ("org.freedesktop.portal.ScreenCast", "Start"):
            {
                _ = reader.ReadObjectPathAsString();
                _ = reader.ReadString();
                Respond(
                    context,
                    request,
                    Record(member, reader.ReadDictionaryOfStringToVariantValue()),
                    StartResponse,
                    WriteStreams
                );
                break;
            }
            case ("org.freedesktop.portal.Camera", "AccessCamera"):
                Respond(
                    context,
                    request,
                    Record(member, reader.ReadDictionaryOfStringToVariantValue()),
                    AccessCameraResponse,
                    null
                );
                break;
            case (_, "OpenPipeWireRemote"):
            {
                using MessageWriter writer = context.CreateReplyWriter("h");
                writer.WriteHandle(_remote.ClientSafePipeHandle);
                context.Reply(writer.CreateMessage());
                break;
            }
            default:
                context.ReplyUnknownMethodError();
                break;
        }

        return default;
    }

    public async ValueTask DisposeAsync()
    {
        _service.Dispose();
        await _remote.DisposeAsync().ConfigureAwait(false);
        _daemon.Kill();
        await _daemon.WaitForExitAsync().ConfigureAwait(false);
        _daemon.Dispose();
        if (_config is not null)
        {
            File.Delete(_config);
        }
    }

    private delegate void WriteResults(ref MessageWriter writer);

    private Dictionary<string, VariantValue> Record(
        string member,
        Dictionary<string, VariantValue> options
    )
    {
        _options[member] = options;
        return options;
    }

    // Replies with the request path, then answers on it, as the portal does once the dialog is done.
    private void Respond(
        MethodContext context,
        Message request,
        Dictionary<string, VariantValue> options,
        uint? response,
        WriteResults? results
    )
    {
        string token = MovesRequests
            ? "moved" + options["handle_token"].GetString()
            : options["handle_token"].GetString();
        string path = $"{Desktop}/request/{SenderOf(request)}/{token}";
        Reply(context, path);
        if (response is not { } code)
        {
            return;
        }

        MessageWriter writer = _service.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(
                destination: request.SenderAsString,
                path: path,
                @interface: "org.freedesktop.portal.Request",
                member: "Response",
                signature: "ua{sv}"
            );
            writer.WriteUInt32(code);
            ArrayStart dictionary = writer.WriteDictionaryStart();
            if (code == 0)
            {
                results?.Invoke(ref writer);
            }

            writer.WriteDictionaryEnd(dictionary);
            _ = _service.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void WriteStreams(ref MessageWriter writer)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString("streams");
        writer.WriteSignature("a(ua{sv})");
        ArrayStart streams = writer.WriteArrayStart(DBusType.Struct);
        WriteStream(ref writer, 42, "0", (0, 0), (1920, 1080), 1, 1042);
        WriteStream(ref writer, 43, "1", (1920, 0), (1280, 1024), 2, null);
        writer.WriteArrayEnd(streams);
        if (RestoreToken is { } restore)
        {
            WriteString(ref writer, "restore_token", restore);
        }
    }

    private static void WriteStream(
        ref MessageWriter writer,
        uint node,
        string id,
        (int X, int Y) position,
        (int W, int H) size,
        uint source,
        ulong? serial
    )
    {
        writer.WriteStructureStart();
        writer.WriteUInt32(node);
        ArrayStart properties = writer.WriteDictionaryStart();
        WriteString(ref writer, "id", id);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("position");
        writer.WriteVariant(VariantValue.Struct(position.X, position.Y));
        writer.WriteDictionaryEntryStart();
        writer.WriteString("size");
        writer.WriteVariant(VariantValue.Struct(size.W, size.H));
        writer.WriteDictionaryEntryStart();
        writer.WriteString("source_type");
        writer.WriteVariantUInt32(source);
        if (serial is { } value)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("pipewire-serial");
            writer.WriteVariantUInt64(value);
        }

        writer.WriteDictionaryEnd(properties);
    }

    private static void WriteString(ref MessageWriter writer, string key, string value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantString(value);
    }

    private void ReplyProperty(MethodContext context, string property)
    {
        using MessageWriter writer = context.CreateReplyWriter("v");
        switch (property)
        {
            case "version":
                writer.WriteVariantUInt32(5);
                break;
            case "AvailableSourceTypes":
                writer.WriteVariantUInt32(1 | 2);
                break;
            case "AvailableCursorModes":
                writer.WriteVariantUInt32(1 | 2 | 4);
                break;
            case "IsCameraPresent":
                writer.WriteVariantBool(CameraPresent);
                break;
            default:
                writer.WriteVariantUInt32(0);
                break;
        }

        context.Reply(writer.CreateMessage());
    }

    private static void Reply(MethodContext context, string? path)
    {
        using MessageWriter writer = context.CreateReplyWriter(path is null ? null : "o");
        if (path is not null)
        {
            writer.WriteObjectPath(path);
        }

        context.Reply(writer.CreateMessage());
    }

    private static string SenderOf(Message request) =>
        (request.SenderAsString ?? string.Empty).TrimStart(':').Replace('.', '_');
}
