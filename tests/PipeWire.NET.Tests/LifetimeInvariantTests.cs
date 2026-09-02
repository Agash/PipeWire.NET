using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// The invariants the ownership chain and the publish pipeline are supposed to guarantee, tested as
/// contracts rather than assumed from the design.
/// </summary>
/// <remarks>
/// Ordinary functional tests do not reach these: they are about what happens when a callback, a
/// disposal, a cancellation and a finalizer interleave, which is the state space this library is
/// complicated enough to have.
/// </remarks>
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

        // Drive every path that publishes: node creation, port arrival, linking, unlinking, removal.
        PipeWireNode sink = await registry.CreateVirtualStereoNode("Monotonic")
            .WithName("pwnet_monotonic_sink").ExecuteAsync(cts.Token);
        PipeWireNode source = await registry.CreateVirtualStereoNode("MonotonicSrc")
            .WithName("pwnet_monotonic_src").ExecuteAsync(cts.Token);

        await registry.RemoveObjectAsync(source.NodeId, cts.Token);
        await registry.RemoveObjectAsync(sink.NodeId, cts.Token);

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
                    registry.CreateVirtualStereoNode($"Waiter{round}_{i}")
                            .WithName($"pwnet_waiter_{round}_{i}")
                            .ExecuteAsync(cts.Token)),
            ];

            PipeWireNode[] created = await Task.WhenAll(creations);

            foreach (PipeWireNode node in created)
            {
                Assert.IsNotNull(registry.Current.GetNode(node.NodeId),
                    $"node {node.NodeId} was reported created but is not in the graph");
                await registry.RemoveObjectAsync(node.NodeId, cts.Token);
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

        PipeWireNode node = await registry.CreateVirtualStereoNode("Order")
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
        using var cts = new CancellationTokenSource(Budget);

        // The window this targets: a teardown path checks the context is alive, then takes the loop
        // lock. Disposing between the two used to throw ObjectDisposedException out of a Dispose.
        for (int round = 0; round < 8; round++)
        {
            var ctx = new PipeWireContext($"pwnet-teardown-{round}", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync(cts.Token);
            var registry = new PipeWireRegistry(ctx);

            Task creating = Task.Run(async () =>
            {
                try
                {
                    PipeWireNode node = await registry.CreateVirtualStereoNode($"Teardown{round}")
                        .WithName($"pwnet_teardown_{round}").ExecuteAsync(cts.Token);
                    await registry.RemoveObjectAsync(node.NodeId, cts.Token);
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                catch (OperationCanceledException) { }
            }, cts.Token);

            Task disposing = Task.Run(async () => await ctx.DisposeAsync(), cts.Token);

            await Task.WhenAll(creating, disposing);
            await registry.DisposeAsync();
        }
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

        PipeWireNode node = await registry.CreateVirtualStereoNode("Finalize")
            .WithName("pwnet_finalize_sink").ExecuteAsync(cts.Token);

        // Bound and dropped without disposing, which is what an application will eventually do by
        // accident. The SafeHandle chain has to hold everything alive until the finalizer runs.
        BindAndAbandon(registry, node.NodeId);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Still usable afterwards: the finalizer must not have taken anything shared with it.
        Assert.IsNotNull(registry.Current.GetNode(node.NodeId));
        await registry.RemoveObjectAsync(node.NodeId, cts.Token);
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
        // lock serialises destruction against dispatch. That invariant is what makes disposal safe
        // while callbacks are in flight; it is not obvious from either side alone, and it becomes
        // load-bearing the moment a filter runs its process callback on the realtime thread.
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
