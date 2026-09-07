using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Filters: a node of this process's own, inside the graph, processing audio as it passes through.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class FilterTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(25);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    /// <summary>
    /// Puts a filter into the graph and links its output to a sink, so the graph has a reason to
    /// drive it. Autoconnect is deliberately not used: linking explicitly is what a filter host
    /// actually does, and it does not depend on session-manager policy being present.
    /// </summary>
    private static async Task<(PipeWireNode Sink, uint FilterNodeId)> LinkToSinkAsync(
        PipeWireRegistry registry, PipeWireFilter filter, string sinkName, CancellationToken cancellationToken)
    {
        PipeWireNode sink = await registry.CreateVirtualNode("FilterSink")
            .WithName(sinkName).ExecuteAsync(cancellationToken);

        await filter.ConnectAsync(cancellationToken: cancellationToken);
        uint filterNodeId = await filter.WaitForNodeIdAsync(cancellationToken);

        // Both ends' ports reach the registry a moment after their nodes do, so both are waited
        // for - the sink's are no more immediate than the filter's.
        PipeWirePort? source = null;
        PipeWirePort? target = null;
        while (source is null || target is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await registry.WaitForInitialEnumerationAsync(cancellationToken);

            PipeWireGraphSnapshot graph = registry.Current;
            source ??= graph.GetPortsForNode(filterNodeId).FirstOrDefault(p => p.IsDataOutput);
            target ??= graph.GetPortsForNode(sink.NodeId).FirstOrDefault(p => p.IsDataInput);

            if (source is null || target is null)
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        await registry.CreateLink(source, target).ExecuteAsync(cancellationToken);
        return (sink, filterNodeId);
    }

    [TestMethod]
    public async Task AConnectedFilter_AppearsInTheGraphWithItsPorts()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-filter", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_filter");

        // Stereo is two ports, not one interleaved port - that is what makes a filter routable per
        // channel, and it is the thing most likely to be got wrong by analogy with streams.
        filter.AddAudioPort(PipeWirePortDirection.In, "input_FL");
        filter.AddAudioPort(PipeWirePortDirection.In, "input_FR");
        filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");
        filter.AddAudioPort(PipeWirePortDirection.Out, "output_FR");

        Assert.AreEqual(4, filter.Ports.Count);
        Assert.IsNull(filter.NodeId, "a filter is not in the graph until it is connected");

        await filter.ConnectAsync(cancellationToken: cts.Token);
        uint nodeId = await filter.WaitForNodeIdAsync(cts.Token);

        // The node id the daemon assigned, read back through the loop lock. Null before this
        // point is "not yet", not an error.
        Assert.AreEqual(nodeId, filter.NodeId);

        // The registry has to see it as an ordinary node, because that is what it is to everything
        // else in the graph.
        ImmutableArray<PipeWirePort> ports = [];
        while (ports.Length < 4)
        {
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            ports = registry.Current.GetPortsForNode(nodeId);
            if (ports.Length < 4) await Task.Delay(TimeSpan.FromMilliseconds(25), cts.Token);
        }

        Assert.IsNotNull(registry.Current.GetNode(nodeId), "the filter must appear as a node");
        Assert.AreEqual(2, ports.Count(p => p.IsDataInput));
        Assert.AreEqual(2, ports.Count(p => p.IsDataOutput));
    }

    [TestMethod]
    public async Task AFilterLinkedIntoTheGraph_IsDrivenAndSeesItsBuffers()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-filter-run", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_filter_run");
        PipeWireFilterPort output = filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long cycles = 0;
        long buffered = 0;

        filter.ProcessCallback = (_, sampleCount) =>
        {
            // The realtime thread. Nothing here allocates: the buffer is written in place, and the
            // only signals out are counters and a completion source that already exists.
            Interlocked.Increment(ref cycles);

            Span<float> samples = output.GetSamples(sampleCount);
            if (!samples.IsEmpty)
            {
                samples.Clear();
                Interlocked.Increment(ref buffered);
                ran.TrySetResult();
            }
        };

        (PipeWireNode sink, _) = await LinkToSinkAsync(registry, filter, "pwnet_filter_run_sink", cts.Token);

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        Assert.IsTrue(Interlocked.Read(ref cycles) > 0, "the filter never processed a cycle");
        Assert.IsTrue(Interlocked.Read(ref buffered) > 0, "the filter never received a buffer to write");

        await registry.DestroyGlobalAsync(sink.NodeId, cts.Token);
    }

    [TestMethod]
    public async Task AProcessCallbackThatThrows_DoesNotTakeTheProcessDown()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-filter-throw", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_filter_throw");
        filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");

        var threw = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        filter.ProcessCallback = (_, _) =>
        {
            threw.TrySetResult();

            // An exception crossing a reverse P/Invoke aborts the process. That the test survives at
            // all is the assertion; the ones after it prove the graph kept running too.
            throw new InvalidOperationException("deliberate");
        };

        (PipeWireNode sink, _) = await LinkToSinkAsync(registry, filter, "pwnet_filter_throw_sink", cts.Token);

        await threw.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        await registry.WaitForInitialEnumerationAsync(cts.Token);
        await registry.DestroyGlobalAsync(sink.NodeId, cts.Token);
    }

    [TestMethod]
    public async Task AddingAPortAfterConnecting_IsRefused()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-filter-late", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_filter_late");
        filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");
        await filter.ConnectAsync(cancellationToken: cts.Token);

        // Ports are part of what the filter negotiated when it connected, so adding one afterwards
        // would describe a node the graph has already agreed the shape of.
        Assert.ThrowsExactly<InvalidOperationException>(
            () => filter.AddAudioPort(PipeWirePortDirection.Out, "output_FR"));
    }

    [TestMethod]
    public async Task AFilterPortWithNoBuffer_ReportsAnEmptySpanRatherThanCrashing()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-filter-nobuf", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_filter_nobuf");
        PipeWireFilterPort port = filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");

        // Never connected, so the port has no buffer at all. A filter that assumed one would read a
        // null pointer; the contract is an empty span.
        Assert.IsTrue(port.GetSamples(1024).IsEmpty);
        Assert.IsTrue(port.GetSamples(0).IsEmpty, "a zero-sample cycle must not produce a buffer");
    }

    [TestMethod]
    public async Task AFilterPortOfADisposedFilter_RefusesReadsRatherThanReadingFreedMemory()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-filter-disposed", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_filter_disposed");
        PipeWireFilterPort port = filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");
        await filter.DisposeAsync();

        // The port data belongs to the filter and dies with it. Reading it afterwards would be a
        // use-after-free; the contract is ObjectDisposedException before any native call.
        Assert.ThrowsExactly<ObjectDisposedException>(() => port.GetSamples(64));
    }

    [TestMethod]
    public async Task AMidiPort_RefusesAudioReadsRatherThanAliasingSequences()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-filter-midi", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_filter_midi");
        PipeWireFilterPort midi = filter.AddMidiPort(PipeWirePortDirection.In, "midi-in");
        PipeWireFilterPort control = filter.AddControlPort(PipeWirePortDirection.Out, "control-out");

        // A sequence buffer reinterpreted as floats is garbage with a valid-looking type. Refusal
        // is the contract until sequences get a typed accessor of their own.
        Assert.AreEqual(PipeWireDspFormat.Midi, midi.Format);
        Assert.AreEqual(PipeWireDspFormat.Control, control.Format);
        Assert.ThrowsExactly<InvalidOperationException>(() => midi.GetSamples(64));
        Assert.ThrowsExactly<InvalidOperationException>(() => control.GetSamples(64));
    }

    [TestMethod]
    public async Task DisposingTheContextBeforeTheFilter_IsSafe()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        var ctx = new PipeWireContext("pwnet-filter-order", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_filter_order");
        filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");
        await filter.ConnectAsync(cancellationToken: cts.Token);

        // The wrong order on purpose: the filter handle holds the core, which holds the context,
        // which holds the loop, so none of them can be gone while the filter is alive.
        await ctx.DisposeAsync();
        await filter.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("RequiresPipeWire168")]
    public async Task AMidiAndAControlPort_JoinTheGraphBesideAudio()
    {
        // MIDI and control ports are not audio ports with a different name,
        // they declare DSP formats the graph links by.
        // 1.0.5 reports a fresh unlinked filter as driving where 1.6.8 does not,
        // so this expectation only holds where the daemon behaves the newer way.
        RequireLinux();
        SessionGates.RequireDaemonAtLeast(1, 6, 8);
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-filter-ports", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        await using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_filter_ports");
        filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");
        PipeWireFilterPort midi = filter.AddMidiPort(PipeWirePortDirection.In, "midi_in");
        PipeWireFilterPort control = filter.AddControlPort(PipeWirePortDirection.In, "control_in");
        PipeWireFilterPort extra = filter.AddPort(
            PipeWirePortDirection.Out, "midi_out", PipeWireDspFormat.Midi,
            new Dictionary<string, string> { ["port.alias"] = "pwnet_midi_out" });

        Assert.AreEqual(4, filter.Ports.Count);
        Assert.AreEqual("midi_in", midi.Name);
        Assert.AreEqual("control_in", control.Name);
        Assert.AreEqual("midi_out", extra.Name);

        // Neither an input nor an output, and no such DSP format: both are caller mistakes the
        // daemon must never see.
        Assert.ThrowsExactly<ArgumentException>(
            () => filter.AddPort((PipeWirePortDirection)999, "bad", PipeWireDspFormat.Midi));
        Assert.ThrowsExactly<ArgumentException>(
            () => filter.AddPort(PipeWirePortDirection.In, "bad", (PipeWireDspFormat)999));

        await filter.ConnectAsync(cancellationToken: cts.Token);
        await filter.WaitForNodeIdAsync(cts.Token);

        // On a live filter these reach the daemon instead of the guards above. Not driving
        // anything (nothing is linked), so triggering is a no-op cycle request and deactivating
        // is restored before leaving.
        Assert.IsFalse(filter.IsDriving);
        filter.TriggerProcess();
        filter.SetActive(false);
        filter.SetActive(true);
    }

    [TestMethod]
    public async Task AnUnconnectedFilter_ReportsItsStateHonestly()
    {
        // Every guard on the filter answers from local state: nothing here reaches the daemon,
        // so a filter that was never connected must still refuse work rather than crashing in it.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-filter-state", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        await using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_filter_state");

        Assert.IsNull(filter.NodeId, "a filter that never connected has no node");
        Assert.AreEqual(PipeWireFilterState.Unconnected, filter.State);
        Assert.IsFalse(filter.IsDriving, "a filter that never connected drives nothing");

        Assert.ThrowsExactly<InvalidOperationException>(() => filter.TriggerProcess());
        Assert.ThrowsExactly<InvalidOperationException>(() => filter.SetActive(true));

        await filter.DisposeAsync();

        // State answers from local handles and degrades to unconnected; the rest refuse a
        // disposed object outright.
        Assert.AreEqual(PipeWireFilterState.Unconnected, filter.State);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = filter.IsDriving);
        Assert.ThrowsExactly<ObjectDisposedException>(() => filter.TriggerProcess());
        Assert.ThrowsExactly<ObjectDisposedException>(() => filter.SetActive(true));
    }

    [TestMethod]
    public async Task CreatingAFilterOnAnUnstartedContext_IsRefused()
    {
        // Connecting is what fails, not creating: without a core there is nothing to build on.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-filter-cold", ConsoleTestLoggerFactory.Instance);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => PipeWireFilter.Create(ctx, "pwnet_filter_cold"));
    }
}
