using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;

namespace PipeWire.NET.Tests;

/// <summary>
/// Which thread each kind of callback actually arrives on, and what that means for two of them.
/// </summary>
/// <remarks>
/// <para>
/// The library has two thread stories and they are not the same one. Graph events, format changes
/// and buffer bookkeeping arrive on the context's own loop thread; a filter connected with
/// <see cref="PipeWireFilterFlags.RtProcess"/> is processed on the daemon's data loop instead, which
/// is a different thread with realtime scheduling and no allocation budget. Everything the library
/// does about locking, about what may be touched from a handler, and about what
/// <c>PipeWireContext.IsOnLoopThread</c> answers follows from that split.
/// </para>
/// <para>
/// These assert the split rather than assume it. A filter that quietly stopped honouring
/// <c>RtProcess</c> and ran on the loop thread would still pass every functional test in this
/// suite while turning a documented realtime guarantee into a lie.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class ThreadAffinityTests : PipeWireTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<PipeWireContext> ConnectAsync(string name, CancellationToken cancellationToken)
    {
        var ctx = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cancellationToken);
        return ctx;
    }

    /// <summary>An RT filter processes on the data loop, not on the thread its graph events use.</summary>
    /// <remarks>
    /// The two ids are collected from the same context so the comparison means something: one loop
    /// thread, one data-loop thread, and a process callback that must be on the second. The library
    /// reports <c>IsOnLoopThread</c> false there, which is what stops a filter from taking the loop
    /// lock and deadlocking the graph it is inside.
    /// </remarks>
    [TestMethod]
    public async Task AnRtFilter_ProcessesOffTheLoopThread()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext ctx = await ConnectAsync("pwnet-rt-affinity", cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        var loopThreads = new ConcurrentBag<int>();
        var loopThreadSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reg.GraphChanged += (_, _) =>
        {
            loopThreads.Add(Environment.CurrentManagedThreadId);
            loopThreadSeen.TrySetResult();
        };

        using PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_rt_affinity");
        filter.AddAudioPort(PipeWirePortDirection.Out, "output_FL");

        int processThread = 0;
        bool onLoopThreadInsideProcess = true;
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        filter.ProcessCallback = (PipeWireFilter f, uint _, in PipeWireGraphClock _) =>
        {
            processThread = Environment.CurrentManagedThreadId;
            onLoopThreadInsideProcess = ctx.IsOnLoopThread;
            _ = f.VideoCycle;
            processed.TrySetResult();
        };

        await filter.ConnectAsync(PipeWireFilterFlags.RtProcess, cts.Token);
        await filter.WaitForNodeIdAsync(cts.Token);

        // A filter with no peer is not scheduled, so give it one: the graph runs once something
        // drives it, and a virtual sink is the cheapest driver this suite already knows how to make.
        PipeWireNode sink = await reg.CreateVirtualSinkAsync(
            "pwnet rt affinity sink", cancellationToken: cts.Token);

        uint filterPort = await PortOfAsync(reg, filter.NodeId!.Value, PipeWirePortDirection.Out, cts.Token);
        uint sinkPort = await PortOfAsync(reg, sink.NodeId, PipeWirePortDirection.In, cts.Token);

        await reg.CreateLinkAsync(filterPort, sinkPort, cts.Token);

        await processed.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);
        await loopThreadSeen.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);

        Assert.AreNotEqual(0, processThread, "the process callback never ran");
        Assert.IsFalse(
            loopThreads.Contains(processThread),
            "an RtProcess filter was processed on the same thread the graph events arrive on");

        Assert.IsFalse(
            onLoopThreadInsideProcess,
            "the context claimed the data loop was its own loop thread, which would let a filter "
            + "take the loop lock from inside a realtime callback");

        await reg.DestroyGlobalAsync(sink.NodeId, cts.Token);
    }

    /// <summary>Two contexts run two loops, and neither one's callbacks land on the other's thread.</summary>
    /// <remarks>
    /// A context owns a thread loop of its own, so a host that runs one connection per subsystem has
    /// as many loop threads as contexts. What that has to buy is isolation: shared state anywhere in
    /// the library would show up here as one context's events arriving on the other's thread.
    /// </remarks>
    [TestMethod]
    public async Task TwoContexts_EachKeepTheirCallbacksOnTheirOwnLoop()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext first = await ConnectAsync("pwnet-affinity-one", cts.Token);
        await using PipeWireContext second = await ConnectAsync("pwnet-affinity-two", cts.Token);

        await using var firstReg = new PipeWireRegistry(first);
        await using var secondReg = new PipeWireRegistry(second);

        var firstThreads = new ConcurrentBag<int>();
        var secondThreads = new ConcurrentBag<int>();

        firstReg.GraphChanged += (_, _) => firstThreads.Add(Environment.CurrentManagedThreadId);
        secondReg.GraphChanged += (_, _) => secondThreads.Add(Environment.CurrentManagedThreadId);

        await firstReg.WaitForInitialEnumerationAsync(cts.Token);
        await secondReg.WaitForInitialEnumerationAsync(cts.Token);

        // Something both registries must report, so both bags are certain to fill.
        PipeWireNode sink = await firstReg.CreateVirtualSinkAsync(
            "pwnet affinity shared sink", cancellationToken: cts.Token);

        for (var i = 0; i < 200 && !secondReg.Current.Nodes.Any(n => n.NodeId == sink.NodeId); i++)
            await Task.Delay(50, cts.Token);

        Assert.IsTrue(
            secondReg.Current.Nodes.Any(n => n.NodeId == sink.NodeId),
            "the second context never saw a node the first one made");

        Assert.IsFalse(firstThreads.IsEmpty, "the first context reported nothing");
        Assert.IsFalse(secondThreads.IsEmpty, "the second context reported nothing");

        var shared = firstThreads.ToHashSet();
        shared.IntersectWith(secondThreads);

        Assert.AreEqual(
            0, shared.Count,
            "two contexts delivered callbacks on the same thread, so they are not two loops");

        await firstReg.DestroyGlobalAsync(sink.NodeId, cts.Token);
    }

    /// <summary>One context blocked inside its own callback does not hold the other one up.</summary>
    /// <remarks>
    /// The isolation that matters in practice: a host doing slow work in a handler stalls that
    /// connection and no other. A shared loop, or a lock taken across contexts, turns one slow
    /// handler into a stall of every connection in the process.
    /// </remarks>
    [TestMethod]
    public async Task AContextBlockedInAHandler_DoesNotStallAnother()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using PipeWireContext blocked = await ConnectAsync("pwnet-affinity-blocked", cts.Token);
        await using PipeWireContext free = await ConnectAsync("pwnet-affinity-free", cts.Token);

        await using var blockedReg = new PipeWireRegistry(blocked);
        await using var freeReg = new PipeWireRegistry(free);

        await blockedReg.WaitForInitialEnumerationAsync(cts.Token);
        await freeReg.WaitForInitialEnumerationAsync(cts.Token);

        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        PipeWireRegistry.GraphChangedHandler block = (_, _) =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(20));
        };

        blockedReg.GraphChanged += block;

        try
        {
            PipeWireNode sink = await freeReg.CreateVirtualSinkAsync(
                "pwnet affinity block sink", cancellationToken: cts.Token);

            // The blocked context is now sitting in its handler. The free one has to finish its own
            // round trip anyway, which is the whole assertion.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);

            PipeWireNode other = await freeReg.CreateVirtualSinkAsync(
                "pwnet affinity free sink", cancellationToken: cts.Token);

            Assert.AreNotEqual(
                0u, other.NodeId,
                "a context could not finish its own work while another sat in a handler");

            release.Set();

            await freeReg.DestroyGlobalAsync(other.NodeId, cts.Token);
            await freeReg.DestroyGlobalAsync(sink.NodeId, cts.Token);
        }
        finally
        {
            // Unsubscribed before the gate goes out of scope: the graph keeps changing after the
            // assertions, and a handler left attached would wait on a disposed event and be logged
            // as a thrown handler.
            blockedReg.GraphChanged -= block;
            release.Set();
        }
    }

    /// <summary>The id of one port of a node, once the daemon has published it.</summary>
    private static async Task<uint> PortOfAsync(
        PipeWireRegistry registry, uint nodeId, PipeWirePortDirection direction,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 200; i++)
        {
            PipeWirePort? port = registry.Current.Ports
                .FirstOrDefault(p => p.NodeId == nodeId && p.PortDirection == direction);

            if (port is not null) return port.PortId;

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail($"node {nodeId} never published a {direction} port");
        return 0;
    }
}
