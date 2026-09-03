using System.Globalization;
using System.Runtime.Versioning;
using PipeWire.NET.Graph;
using PipeWire.NET;

namespace PipeWire.NET.SampleConsole;

// Read-only graph exploration plus the two safe control reads. Volume writes need --set/--mute
// spelled out; nothing here changes state by default.
[SupportedOSPlatform("linux")]
internal static class GraphCommands
{
    public static async Task<int> ListAsync(CancellationToken cancellationToken)
    {
        await using var session = await Session.ConnectAsync(
            "sample-list", cancellationToken).ConfigureAwait(false);
        PipeWireGraphSnapshot graph = session.Registry.Current;

        Console.WriteLine($"Graph version {graph.Version}: " +
            $"{graph.Nodes.Length} nodes, {graph.Ports.Length} ports, " +
            $"{graph.Links.Length} links, {graph.Devices.Length} devices, " +
            $"{graph.Clients.Length} clients.");

        Console.WriteLine("Nodes:");
        foreach (PipeWireNode node in graph.Nodes)
        {
            int ports = 0;
            foreach (PipeWirePort _ in graph.GetPortsForNode(node.NodeId))
                ports++;

            Console.WriteLine($"  [{node.NodeId,4}] {node.Description ?? node.NodeName ?? "<no name>"}");
            Console.WriteLine($"           class={node.MediaClass ?? "?"} " +
                $"media={node.Media} flow={node.Flow} ports={ports}");
        }

        if (graph.Devices.Length > 0)
        {
            Console.WriteLine("Devices:");
            foreach (PipeWireDevice device in graph.Devices)
                Console.WriteLine($"  [{device.Id,4}] {device.Description ?? device.DeviceName} " +
                    $"({device.Api ?? "?"})");
        }

        if (graph.Links.Length > 0)
        {
            Console.WriteLine("Links:");
            foreach (PipeWireLink link in graph.Links)
            {
                (PipeWirePort? output, PipeWirePort? input) = graph.GetEndpoints(link);
                Console.WriteLine($"  [{link.LinkId,4}] " +
                    $"{output?.NodeId}:{output?.PortName ?? "?"} -> " +
                    $"{input?.NodeId}:{input?.PortName ?? "?"}");
            }
        }

        foreach (PipeWireMetadataObject metadata in graph.MetadataStores)
            Console.WriteLine($"Metadata [{metadata.Id}] {metadata.MetadataName ?? "?"}");

        return 0;
    }

    public static async Task<int> MonitorAsync(CancellationToken cancellationToken)
    {
        await using var session = await Session.ConnectAsync(
            "sample-monitor", cancellationToken).ConfigureAwait(false);

        Console.WriteLine("Watching the graph; Ctrl+C stops.");
        try
        {
            await foreach (PipeWireGraphSnapshot graph in session.Registry
                .WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                Console.WriteLine($"[{graph.Version}] nodes={graph.Nodes.Length} " +
                    $"ports={graph.Ports.Length} links={graph.Links.Length} " +
                    $"devices={graph.Devices.Length} clients={graph.Clients.Length}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C is the normal exit, not an error.
        }

        return 0;
    }

    public static async Task<int> VolumeAsync(string[] args, CancellationToken cancellationToken)
    {
        string? target = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null;
        string? setRaw = Program.OptionValue(args, "--set");
        bool mute = Program.HasFlag(args, "--mute");
        bool unmute = Program.HasFlag(args, "--unmute");

        float setLevel = 0;
        if (setRaw is not null)
        {
            if (!float.TryParse(setRaw, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out setLevel)
                || float.IsNaN(setLevel) || setLevel < 0 || setLevel > 1)
            {
                Console.Error.WriteLine($"--set takes a volume from 0 to 1, not '{setRaw}'.");
                return 2;
            }
        }

        if (mute && unmute)
        {
            Console.Error.WriteLine("--mute and --unmute together make no sense; pick one.");
            return 2;
        }

        await using var session = await Session.ConnectAsync(
            "sample-volume", cancellationToken).ConfigureAwait(false);

        PipeWireNode? node = target is null
            ? await DefaultSinkNodeAsync(session, cancellationToken).ConfigureAwait(false)
            : FindNode(session, target);
        if (node is null)
        {
            Console.Error.WriteLine(target is null
                ? "No default sink in this session; name a node explicitly."
                : $"No node matches '{target}'.");
            return 1;
        }

        Console.WriteLine($"Node [{node.NodeId}] {node.Description ?? node.NodeName}:");

        await using PipeWireNodeControl control = session.Registry.BindNode(node.NodeId);
        await control.ReadyAsync(cancellationToken).ConfigureAwait(false);

        float? before = await control.GetVolumeAsync(cancellationToken).ConfigureAwait(false);
        bool? mutedBefore = await control.GetMutedAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"  volume {Render(before)}, muted {Render(mutedBefore)}");

        if (setRaw is not null)
        {
            await control.SetVolumeAsync(setLevel, cancellationToken).ConfigureAwait(false);
            float? after = await control.GetVolumeAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"  volume now {Render(after)}");
        }

        if (mute || unmute)
        {
            await control.SetMutedAsync(mute, cancellationToken).ConfigureAwait(false);
            bool? after = await control.GetMutedAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"  muted now {Render(after)}");
        }

        return 0;
    }

    public static async Task<int> DefaultsAsync(CancellationToken cancellationToken)
    {
        await using var session = await Session.ConnectAsync(
            "sample-defaults", cancellationToken).ConfigureAwait(false);

        PipeWireMetadataStore? store = session.Registry.BindMetadataStore("default");
        if (store is null)
        {
            Console.Error.WriteLine("No 'default' metadata store; this session has no session manager.");
            return 1;
        }

        await using (store)
        {
            await store.ReadyAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Default sink:   {store.DefaultAudioSink?.NameValue ?? "<none>"}");
            Console.WriteLine($"Default source: {store.DefaultAudioSource?.NameValue ?? "<none>"}");
            Console.WriteLine($"Clock rate:     {Render(store.ClockRate)} Hz");
            Console.WriteLine($"Clock quantum:  {Render(store.ClockQuantum)}");
            Console.WriteLine($"Quantum range:  {Render(store.ClockMinQuantum)}..{Render(store.ClockMaxQuantum)}");
            Console.WriteLine($"Forced rate:    {Render(store.ClockForcedRate)}");
            Console.WriteLine($"Forced quantum: {Render(store.ClockForcedQuantum)}");
        }

        return 0;
    }

    // The default sink node: metadata names the node, the registry resolves it.
    internal static async Task<PipeWireNode?> DefaultSinkNodeAsync(
        Session session, CancellationToken cancellationToken)
    {
        PipeWireMetadataStore? store = session.Registry.BindMetadataStore("default");
        if (store is null)
            return null;

        await using (store)
        {
            await store.ReadyAsync(cancellationToken).ConfigureAwait(false);
            string? name = store.DefaultAudioSink?.NameValue;
            if (name is null)
                return null;

            return FindNode(session, name);
        }
    }

    // Numeric id first, then a case-insensitive fragment of the node or description.
    internal static PipeWireNode? FindNode(Session session, string target)
    {
        if (uint.TryParse(target, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out uint id))
        {
            foreach (PipeWireNode node in session.Registry.Nodes)
            {
                if (node.NodeId == id)
                    return node;
            }

            return null;
        }

        PipeWireNode? first = null;
        int matches = 0;
        foreach (PipeWireNode node in session.Registry.Nodes)
        {
            string haystack = $"{node.NodeName} {node.Description}";
            if (haystack.Contains(target, StringComparison.OrdinalIgnoreCase))
            {
                first ??= node;
                matches++;
            }
        }

        if (matches > 1)
            Console.WriteLine($"  '{target}' matches {matches} nodes; using [{first!.NodeId}].");

        return first;
    }

    private static string Render(float? value) =>
        value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) : "<unknown>";

    private static string Render(bool? value) =>
        value.HasValue ? (value.Value ? "yes" : "no") : "<unknown>";

    private static string Render(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "<unset>";
}
