using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;
using PipeWire.NET;

namespace PipeWire.NET.SampleConsole;

// Serving, not consuming: a virtual source that stays published until Ctrl+C, and a DSP node
// wired tone -> gain -> default sink. Links are plain (no linger), so they die with the nodes;
// nothing here outlives the process.
[SupportedOSPlatform("linux")]
internal static class ServeCommands
{
    public static async Task<int> ServeAsync(CancellationToken cancellationToken)
    {
        await using var session = await Session.ConnectAsync(
            "sample-serve", cancellationToken).ConfigureAwait(false);

        PipeWireNode node = await session.Registry.CreateVirtualNode("Sample virtual source")
            .WithMediaClass("Audio/Source")
            .WithName("sample_source")
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"Serving virtual source [{node.NodeId}] 'sample_source'. Ctrl+C stops.");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is the normal exit, not an error.
        }

        await session.Registry.DestroyGlobalAsync(node.NodeId).ConfigureAwait(false);
        Console.WriteLine("Withdrawn.");
        return 0;
    }

    public static async Task<int> FilterAsync(string[] args, CancellationToken cancellationToken)
    {
        int seconds = Program.Seconds(args, 8);

        await using var session = await Session.ConnectAsync(
            "sample-filter", cancellationToken).ConfigureAwait(false);

        // A generated tone so the chain works on a machine with no microphone: tone feeds the
        // filter, the filter feeds the sink. Unrouted on purpose; the links below are explicit.
        await using var tone = new PipeWireAudioOutput(
            session.Context, "sample_tone", sampleRate: 48000, channels: 1);
        double phase = 0;
        tone.FillSamples += (_, samples, sampleRate, channels, format) =>
        {
            if (format != AudioSampleFormat.F32Le || channels <= 0)
                return 0;

            Span<float> mono = MemoryMarshal.Cast<byte, float>(samples);
            int framesInBuffer = mono.Length / channels;
            for (int i = 0; i < framesInBuffer; i++)
            {
                phase += 2.0 * Math.PI * 440.0 / sampleRate;
                float sample = (float)(Math.Sin(phase) * 0.15);
                for (int c = 0; c < channels; c++)
                    mono[(i * channels) + c] = sample;
            }

            return framesInBuffer * channels * sizeof(float);
        };
        tone.Connect(PipeWireAudioOutput.AnyNode, autoConnect: false);

        uint? toneId = await WaitForNodeIdAsync(tone, cancellationToken).ConfigureAwait(false);
        if (toneId is null)
        {
            Console.Error.WriteLine("The tone never appeared; aborting before linking.");
            return 1;
        }

        await using PipeWireFilter filter = PipeWireFilter.Create(session.Context, "sample_gain");
        PipeWireFilterPort input = filter.AddAudioPort(PipeWirePortDirection.In, "in");
        PipeWireFilterPort output = filter.AddAudioPort(PipeWirePortDirection.Out, "out");

        long cycles = 0;
        long processed = 0;
        filter.ProcessCallback = (_, sampleCount) =>
        {
            // The realtime thread: no allocation, no blocking, counters only.
            Interlocked.Increment(ref cycles);

            Span<float> dry = input.GetSamples(sampleCount);
            Span<float> wet = output.GetSamples(sampleCount);
            if (dry.IsEmpty || wet.IsEmpty)
                return;

            int framesToShape = Math.Min(dry.Length, wet.Length);
            for (int i = 0; i < framesToShape; i++)
                wet[i] = dry[i] * 0.5f;

            Interlocked.Increment(ref processed);
        };

        await filter.ConnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        uint filterId = await filter.WaitForNodeIdAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Filter is node [{filterId}] with {filter.Ports.Count} ports.");

        ImmutableArray<PipeWirePort> tonePorts = await WaitForPortsAsync(
            session.Registry, toneId.Value, cancellationToken).ConfigureAwait(false);
        ImmutableArray<PipeWirePort> filterPorts = await WaitForPortsAsync(
            session.Registry, filterId, cancellationToken).ConfigureAwait(false);

        uint toneOut = PortId(tonePorts, PipeWirePortDirection.Out);
        uint filterIn = PortId(filterPorts, PipeWirePortDirection.In);
        uint filterOut = PortId(filterPorts, PipeWirePortDirection.Out);

        if (toneOut == 0 || filterIn == 0 || filterOut == 0)
        {
            Console.Error.WriteLine("Ports never appeared; aborting before linking.");
            return 1;
        }

        PipeWireLink up = await session.Registry.CreateLink(toneOut, filterIn)
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Linked tone -> filter [{up.LinkId}].");

        PipeWireNode? sink = await GraphCommands.DefaultSinkNodeAsync(
            session, cancellationToken).ConfigureAwait(false);
        if (sink is not null)
        {
            ImmutableArray<PipeWirePort> sinkPorts = await WaitForPortsAsync(
                session.Registry, sink.NodeId, cancellationToken).ConfigureAwait(false);
            uint sinkIn = PortId(sinkPorts, PipeWirePortDirection.In);
            if (sinkIn != 0)
            {
                PipeWireLink down = await session.Registry.CreateLink(filterOut, sinkIn)
                    .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Linked filter -> '{sink.NodeName}' [{down.LinkId}].");
            }
            else
            {
                Console.WriteLine("Default sink has no input ports; running the chain unlinked.");
            }
        }
        else
        {
            Console.WriteLine("No default sink in this session; running the chain unlinked.");
        }

        long lastCycles = 0;
        long lastProcessed = 0;
        for (int left = seconds; left > 0; left--)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            long nowCycles = Interlocked.Read(ref cycles);
            long nowProcessed = Interlocked.Read(ref processed);
            Console.WriteLine($"  cycles/s={nowCycles - lastCycles} " +
                $"with-buffers/s={nowProcessed - lastProcessed}");
            lastCycles = nowCycles;
            lastProcessed = nowProcessed;
        }

        // Links die with the nodes being disposed above; nothing to remove by hand.
        Console.WriteLine("Done.");
        return 0;
    }

    private static uint PortId(ImmutableArray<PipeWirePort> ports, PipeWirePortDirection direction)
    {
        foreach (PipeWirePort port in ports)
        {
            if (port.PortDirection == direction)
                return port.PortId;
        }

        return 0;
    }

    private static async Task<ImmutableArray<PipeWirePort>> WaitForPortsAsync(
        PipeWireRegistry registry, uint nodeId, CancellationToken cancellationToken)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, bound.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            try
            {
                await registry.WaitForInitialEnumerationAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ImmutableArray<PipeWirePort> ports = registry.Current.GetPortsForNode(nodeId);
            if (ports.Length > 0)
                return ports;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return [];
    }

    private static async Task<uint?> WaitForNodeIdAsync(
        PipeWireAudioOutput tone, CancellationToken cancellationToken)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, bound.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            if (tone.NodeId is uint id)
                return id;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }
}
