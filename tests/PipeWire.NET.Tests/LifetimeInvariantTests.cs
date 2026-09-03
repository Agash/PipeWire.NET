using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// The invariants the ownership chain and the publish pipeline are supposed to guarantee, tested as
/// contracts rather than assumed from the design.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class LifetimeInvariantTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    [TestMethod]
    public async Task TheSnapshotVersionNeverGoesBackwards_AcrossEveryEventPath()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-monotonic", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);

        long highest = 0;
        var faults = new List<string>();

        // Every handler runs on the loop thread, so this observes the versions in publish order.
        registry.GraphChanged += (_, snapshot) =>
        {
            if (snapshot.Version <= highest)
                faults.Add($"version went {highest} -> {snapshot.Version}");
            highest = snapshot.Version;

            // The invariant that makes publish-before-notify worth having: a handler must never see
            // an event describing a graph that Current does not yet contain.
            if (registry.Current.Version < snapshot.Version)
                faults.Add($"Current is behind the event: {registry.Current.Version} < {snapshot.Version}");
        };

        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode sink = await registry.CreateVirtualNode("Monotonic")
            .WithName("pwnet_monotonic_sink").ExecuteAsync(cts.Token);
        PipeWireNode source = await registry.CreateVirtualNode("MonotonicSrc")
            .WithName("pwnet_monotonic_src").ExecuteAsync(cts.Token);

        await registry.DestroyGlobalAsync(source.NodeId, cts.Token);
        await registry.DestroyGlobalAsync(sink.NodeId, cts.Token);

        Assert.IsTrue(faults.Count == 0, string.Join("; ", faults));
        Assert.IsTrue(highest > 0, "nothing was published, so nothing was actually checked");
    }

    [TestMethod]
    public async Task ACreationWaiterCannotMissItsGlobal_HoweverTheEventsInterleave()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // The race is between the bound event registering a waiter and OnGlobal completing it. It
        // has no deterministic trigger from outside, so it is provoked by creating repeatedly and
        // concurrently: a miss shows up as a creation that never returns, which the budget catches.
        await using var ctx = new PipeWireContext("pwnet-waiter", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);

        for (int round = 0; round < 3; round++)
        {
            Task<PipeWireNode>[] creations =
            [
                .. Enumerable.Range(0, 6).Select(i =>
                    registry.CreateVirtualNode($"Waiter{round}_{i}")
                            .WithName($"pwnet_waiter_{round}_{i}")
                            .ExecuteAsync(cts.Token)),
            ];

            PipeWireNode[] created = await Task.WhenAll(creations);

            foreach (PipeWireNode node in created)
            {
                Assert.IsNotNull(registry.Current.GetNode(node.NodeId),
                    $"node {node.NodeId} was reported created but is not in the graph");
                await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
            }
        }
    }

    [TestMethod]
    public async Task DisposingTheContextFirst_LeavesBoundControlsSafeToDispose()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        var ctx = new PipeWireContext("pwnet-order", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode node = await registry.CreateVirtualNode("Order")
            .WithName("pwnet_order_sink").ExecuteAsync(cts.Token);

        PipeWireNodeControl control = registry.BindNode(node.NodeId);

        // Deliberately the wrong order. The handle chain is what makes this survive: the bound proxy
        // holds the core, which holds the context, which holds the loop, so none of them can have
        // been freed while the control is still alive.
        await ctx.DisposeAsync();
        await control.DisposeAsync();
        await registry.DisposeAsync();
    }

    [TestMethod]
    public async Task DisposingTheContextConcurrentlyWithTeardown_DoesNotThrowOutOfDispose()
    {
        RequireLinux();

        // Its own budget: eight rounds need more than 20s.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // The window this targets: a teardown path checks the context is alive, then takes the loop
        // lock, and disposing between the two must not throw out of a Dispose.
        for (int round = 0; round < 8; round++)
        {
            // Per-round token: a cancellation from a torn-down context propagates as an
            // ObjectDisposedException once the in-flight creation hooks the shutdown token.
            using var roundCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, roundCts.Token);

            var ctx = new PipeWireContext($"pwnet-teardown-{round}", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync(linkedCts.Token);
            var registry = new PipeWireRegistry(ctx);

            Task creating = Task.Run(async () =>
            {
                try
                {
                    PipeWireNode node = await registry.CreateVirtualNode($"Teardown{round}")
                        .WithName($"pwnet_teardown_{round}").ExecuteAsync(linkedCts.Token);
                    await registry.DestroyGlobalAsync(node.NodeId, linkedCts.Token);
                }
                catch (ObjectDisposedException) { }
                catch (Exception e) when (e is InvalidOperationException or PipeWireException) { }
                catch (OperationCanceledException) { }
            }, linkedCts.Token);

            Task disposing = Task.Run(async () => await ctx.DisposeAsync(), linkedCts.Token);

            await Task.WhenAll(creating, disposing);
            await registry.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task DisposingTheContextWithACreationInFlight_FailsTheCreationFast()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        var ctx = new PipeWireContext("pwnet-disposing-inflight", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        // No delay: the creation needs a full daemon round trip while disposal only needs to
        // signal, so disposal wins and the shutdown hook surfaces ObjectDisposedException to the
        // in-flight wait instead of leaving it parked on a stopped loop.
        Task<PipeWireNode> creation = Task.Run(() => registry.CreateVirtualNode("Disposing")
            .WithName("pwnet_disposing_inflight").ExecuteAsync(cts.Token));
        await ctx.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await creation);
        await registry.DisposeAsync();
    }

    [TestMethod]
    public async Task StartingADisposedContext_IsRefused()
    {
        // Disposal wins over a start that never happened: there is no loop thread to start and
        // no connection to make, so the call fails at the gate rather than halfway through
        // native setup.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        var ctx = new PipeWireContext("pwnet-deadstart", ConsoleTestLoggerFactory.Instance);
        await ctx.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await ctx.StartAsync(cts.Token));
    }

    [TestMethod]
    public async Task AControlLeftToTheFinalizer_ReleasesWithoutAborting()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-finalize", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode node = await registry.CreateVirtualNode("Finalize")
            .WithName("pwnet_finalize_sink").ExecuteAsync(cts.Token);

        // Bound and dropped without disposing, which is what an application will eventually do by
        // accident. The SafeHandle chain has to hold everything alive until the finalizer runs.
        BindAndAbandon(registry, node.NodeId);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Still usable afterwards: the finalizer must not have taken anything shared with it.
        Assert.IsNotNull(registry.Current.GetNode(node.NodeId));
        await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
    }

    private static void BindAndAbandon(PipeWireRegistry registry, uint nodeId)
    {
        PipeWireNodeControl control = registry.BindNode(nodeId);
        GC.KeepAlive(control.Id);
    }

    [TestMethod]
    public async Task DisposingAFilterWhileItsCallbackIsRunning_DoesNotFreeWhatTheCallbackIsUsing()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Callback state is freed by the handle after the native object is destroyed, and the loop
        // lock serialises destruction against dispatch.
        for (int round = 0; round < 8; round++)
        {
            await using var ctx = new PipeWireContext($"pwnet-cb-{round}", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync(cts.Token);
            await using var registry = new PipeWireRegistry(ctx);
            await registry.WaitForInitialEnumerationAsync(cts.Token);

            long entered = 0;
            long inside = 0;

            PipeWireFilter filter = PipeWireFilter.Create(ctx, $"pwnet_cb_{Environment.ProcessId}_{round}");
            filter.ProcessCallback = (f, samples) =>
            {
                Interlocked.Increment(ref inside);
                Interlocked.Increment(ref entered);

                // Touch state the disposal would free, for as long as the callback is on the stack.
                foreach (PipeWireFilterPort port in f.Ports)
                {
                    try { GC.KeepAlive(port.GetSamples(samples).Length); }
                    catch (ObjectDisposedException) { /* the documented answer once the filter is gone. */ }
                }

                Interlocked.Decrement(ref inside);
            };

            filter.AddAudioPort(PipeWirePortDirection.In, "in");
            filter.AddAudioPort(PipeWirePortDirection.Out, "out");
            await filter.ConnectAsync(cancellationToken: cts.Token);

            // Dispose without waiting for quiet: the point is to overlap.
            await filter.DisposeAsync();

            Assert.AreEqual(0, Interlocked.Read(ref inside),
                "a callback was still running after DisposeAsync returned");

            // And the graph is still usable, which a corrupted listener list would not leave it.
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsTrue(registry.Current.Version > 0);
        }
    }
}
