using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET;
using PipeWire.NET.Graph;
using PipeWire.NET.Portal;

namespace PipeWire.NET.SampleConsole;

// The xdg-desktop-portal flows: what the desktop's portal offers, and a screen cast from the user's
// choice in its dialog to the PipeWire nodes it shares.
[SupportedOSPlatform("linux")]
internal static class PortalCommands
{
    // Reads the ScreenCast and Camera portals' versions and capabilities; asks the user nothing.
    public static async Task<int> PortalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using ScreenCastPortal screenCast = await ScreenCastPortal
                .ConnectAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine(
                $"ScreenCast v{screenCast.Version}: sources {screenCast.AvailableSources}, "
                    + $"cursor modes {screenCast.AvailableCursorModes}"
            );
        }
        catch (PortalException e)
        {
            Console.WriteLine($"ScreenCast: {e.Message}");
            return Program.NothingToDo;
        }

        try
        {
            await using CameraPortal camera = await CameraPortal
                .ConnectAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"Camera v{camera.Version}: camera present {camera.IsCameraPresent}");
        }
        catch (PortalException e)
        {
            Console.WriteLine($"Camera: {e.Message}");
        }

        return Program.Ok;
    }

    // Asks the user for a monitor (or, with --window, a window), then connects to the remote the portal
    // opened and prints the shared nodes as the daemon sees them.
    public static async Task<int> ScreenCastAsync(
        string[] args,
        CancellationToken cancellationToken
    )
    {
        ScreenCastSources sources = args.Contains("--window")
            ? ScreenCastSources.Window
            : ScreenCastSources.Monitor;
        await using ScreenCastPortal portal = await ScreenCastPortal
            .ConnectAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        ScreenCastSession session;
        try
        {
            session = await portal
                .StartAsync(
                    new()
                    {
                        Sources = sources,
                        CursorMode = portal.AvailableCursorModes.HasFlag(
                            ScreenCastCursorModes.Metadata
                        )
                            ? ScreenCastCursorModes.Metadata
                            : null,
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (PortalException e)
        {
            Console.WriteLine(e.Message);
            return Program.NothingToDo;
        }

        await using (session.ConfigureAwait(false))
        {
            foreach (ScreenCastStream stream in session.Streams)
            {
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"stream node {stream.NodeId} {stream.Source} size {stream.Size} at {stream.Position}"
                    )
                );
            }

            using SafeHandle remote = await session
                .OpenPipeWireRemoteAsync(cancellationToken)
                .ConfigureAwait(false);
            await using PipeWireContext context = new("sample-screencast");
            await context.StartOnFdAsync(remote, cancellationToken).ConfigureAwait(false);
            Console.WriteLine("connected to daemon through the portal's remote");
            await using PipeWireRegistry registry = new(context);
            await registry.WaitForInitialEnumerationAsync(cancellationToken).ConfigureAwait(false);
            foreach (PipeWireNode node in registry.Current.Nodes)
            {
                Console.WriteLine(
                    $"  visible node [{node.NodeId}] {node.Description ?? node.NodeName ?? "<no name>"}"
                );
            }
        }

        return Program.Ok;
    }
}
