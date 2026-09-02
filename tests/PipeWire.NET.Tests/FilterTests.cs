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
        PipeWireNode sink = await registry.CreateVirtualStereoNode("FilterSink")
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

        await registry.RemoveObjectAsync(sink.NodeId, cts.Token);
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
        await registry.RemoveObjectAsync(sink.NodeId, cts.Token);
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
}
