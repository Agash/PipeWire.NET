using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// What happens when a consumer misbehaves. Every event is raised from a reverse P/Invoke on the
/// loop thread, so an exception escaping a handler aborts the process rather than failing a call.
/// </summary>
/// <remarks>
/// These tests install handlers that throw on purpose and then require the graph to keep working.
/// Surviving is necessary but not sufficient: each one goes on to prove that later events were
/// still delivered, because a loop that silently stopped dispatching would otherwise look healthy.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class RegistryRobustnessTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<(PipeWireContext Context, PipeWireRegistry Registry)> ConnectAsync(
        string name, CancellationToken cancellationToken)
    {
        var context = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cancellationToken);
        var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cancellationToken);
        return (context, registry);
    }

    private static async Task<PipeWireGraphSnapshot> WaitForAsync(
        PipeWireRegistry registry, Func<PipeWireGraphSnapshot, bool> until, CancellationToken cancellationToken)
    {
        await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(cancellationToken))
            if (until(graph))
                return graph;

        throw new InvalidOperationException("the snapshot stream ended before the condition held");
    }

    [TestMethod]
    public async Task EveryEventHandlerThrowing_DoesNotStopTheGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-throwall", cts.Token);

        await using (context)
        await using (registry)
        {
            // Every event, all throwing. If any of them escapes, this process is gone.
            var raised = new Dictionary<string, int>
            {
                ["NodeAdded"] = 0, ["PortAdded"] = 0, ["LinkAdded"] = 0,
                ["NodeRemoved"] = 0, ["PortRemoved"] = 0, ["LinkRemoved"] = 0,
                ["GraphChanged"] = 0,
            };

            void Bang(string which)
            {
                lock (raised) raised[which]++;
                throw new InvalidOperationException($"{which} handler is hostile");
            }

            registry.NodeAdded += _ => Bang("NodeAdded");
            registry.PortAdded += _ => Bang("PortAdded");
            registry.LinkAdded += _ => Bang("LinkAdded");
            registry.NodeRemoved += _ => Bang("NodeRemoved");
            registry.PortRemoved += _ => Bang("PortRemoved");
            registry.LinkRemoved += _ => Bang("LinkRemoved");
            registry.GraphChanged += (_, _) => Bang("GraphChanged");

            // Exercise the full lifecycle so every one of those handlers gets its turn.
            PipeWireNode a = await registry.CreateVirtualNodeAsync("HA", "pwnet_hostile_a", cts.Token);
            PipeWireNode b = await registry.CreateVirtualNodeAsync("HB", "pwnet_hostile_b", cts.Token);

            PipeWireGraphSnapshot ready = await WaitForAsync(
                registry,
                g => g.GetPortsForNode(a.NodeId).Length == 4 && g.GetPortsForNode(b.NodeId).Length == 4,
                cts.Token);

            PipeWireLink link = await registry.CreateLinkAsync(
                ready.GetPortsForNode(a.NodeId, PipeWirePortDirection.Out).OrderBy(p => p.PortId).First(),
                ready.GetPortsForNode(b.NodeId, PipeWirePortDirection.In).OrderBy(p => p.PortId).First(),
                cts.Token);

            await registry.DestroyGlobalAsync(link.LinkId, cts.Token);
            await registry.DestroyGlobalAsync(a.NodeId, cts.Token);
            await registry.DestroyGlobalAsync(b.NodeId, cts.Token);

            PipeWireGraphSnapshot end = await WaitForAsync(
                registry,
                g => g.GetNode(a.NodeId) is null && g.GetNode(b.NodeId) is null && g.GetLink(link.LinkId) is null,
                cts.Token);

            // Surviving proves the boundary holds. These prove the loop kept delivering.
            lock (raised)
            {
                foreach ((string which, int count) in raised)
                    Assert.IsTrue(count > 0, $"{which} never fired, so its throwing path was not exercised");
            }

            Assert.IsNull(end.GetNode(a.NodeId), "removals must still land despite hostile handlers");
            Assert.IsNull(end.GetLink(link.LinkId));
        }
    }

    [TestMethod]
    public async Task AHandlerThatThrowsOnEveryEvent_StillLeavesTheGraphConsistent()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-consistent", cts.Token);

        await using (context)
        await using (registry)
        {
            registry.GraphChanged += (_, _) => throw new InvalidOperationException("no");
            registry.PortAdded += _ => throw new InvalidOperationException("no");

            PipeWireNode node = await registry.CreateVirtualNodeAsync("C", "pwnet_consistent", cts.Token);
            PipeWireGraphSnapshot graph = await WaitForAsync(
                registry, g => g.GetPortsForNode(node.NodeId).Length == 4, cts.Token);

            // A handler failing must not leave a partially indexed snapshot behind.
            foreach (PipeWirePort port in graph.GetPortsForNode(node.NodeId))
            {
                Assert.IsNotNull(graph.GetPort(port.PortId), "port missing from its own index");
                Assert.AreEqual(node.NodeId, port.NodeId);
            }
            Assert.IsNotNull(graph.GetNode(node.NodeId));
        }
    }

    [TestMethod]
    public async Task AThrowingSubscriber_DoesNotStarveTheOnesRegisteredAfterIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-starve", cts.Token);

        await using (context)
        await using (registry)
        {
            // Order matters: the hostile handler goes first, so a single Invoke on the multicast
            // delegate would stop before ever reaching the good one.
            var good = 0;
            registry.GraphChanged += (_, _) => throw new InvalidOperationException("hostile");
            registry.GraphChanged += (_, _) => Interlocked.Increment(ref good);

            // Recorded rather than counted, and filtered afterwards. The session is shared, so
            // every other class creating a node delivers four more port events to this subscriber;
            // an exact total would be measuring the rest of the suite. Filtering inside the handler
            // is no good either, because the ports can be announced before the id is known here.
            var seenPorts = new System.Collections.Concurrent.ConcurrentBag<uint>();
            registry.PortAdded += _ => throw new InvalidOperationException("hostile");
            registry.PortAdded += port => seenPorts.Add(port.NodeId);

            PipeWireNode node = await registry.CreateVirtualNodeAsync("SV", "pwnet_starve", cts.Token);
            await WaitForAsync(registry, g => g.GetPortsForNode(node.NodeId).Length == 4, cts.Token);

            Assert.IsTrue(Volatile.Read(ref good) > 0,
                "a subscriber registered after a throwing one still has to be called");
            Assert.AreEqual(4, seenPorts.Count(id => id == node.NodeId),
                "every port event for this node must reach the well-behaved subscriber");
        }
    }

    [TestMethod]
    public async Task AThrowingSubscriber_DoesNotBreakWatchAsync()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-watchstarve", cts.Token);

        await using (context)
        await using (registry)
        {
            // WatchAsync forwards snapshots through GraphChanged like any other subscriber, so a
            // hostile handler registered first must not stop the stream.
            registry.GraphChanged += (_, _) => throw new InvalidOperationException("hostile");

            PipeWireNode node = await registry.CreateVirtualNodeAsync("WS", "pwnet_watchstarve", cts.Token);
            PipeWireGraphSnapshot graph = await WaitForAsync(
                registry, g => g.GetPortsForNode(node.NodeId).Length == 4, cts.Token);

            Assert.AreEqual(4, graph.GetPortsForNode(node.NodeId).Length);
        }
    }

    [TestMethod]
    public async Task AHandlerThatDisposesTheRegistry_DoesNotDeadlockOrAbort()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-suicidal", cts.Token);

        await using (context)
        {
            // Disposal from a callback means taking the loop lock while already holding it. The
            // mutex is recursive, so this is legal; the point is that nothing hangs or aborts.
            var disposedFromHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var once = 0;

            registry.NodeAdded += node =>
            {
                if (node.NodeName != "pwnet_suicidal") return;
                if (Interlocked.Exchange(ref once, 1) != 0) return;

                // Fire and forget: awaiting from the loop thread would be the deadlock.
                _ = Task.Run(async () =>
                {
                    try { await registry.DisposeAsync(); disposedFromHandler.TrySetResult(); }
                    catch (Exception ex) { disposedFromHandler.TrySetException(ex); }
                }, CancellationToken.None);
            };

            try
            {
                await registry.CreateVirtualNodeAsync("S", "pwnet_suicidal", cts.Token);
            }
            catch (ObjectDisposedException)
            {
                // Legitimate: disposal won the race with the creation completing.
            }

            await disposedFromHandler.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

            Assert.ThrowsExactly<ObjectDisposedException>(
                () => registry.DestroyGlobalAsync(1, CancellationToken.None),
                "the registry really is disposed afterwards");
        }
    }

    [TestMethod]
    public async Task CreateLink_RejectsBothWrongDirectionsIndependently()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-dirs", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNodeAsync("D", "pwnet_dirs", cts.Token);
            PipeWireGraphSnapshot graph = await WaitForAsync(
                registry, g => g.GetPortsForNode(node.NodeId).Length == 4, cts.Token);

            PipeWirePort output = graph.GetPortsForNode(node.NodeId, PipeWirePortDirection.Out).First();
            PipeWirePort input = graph.GetPortsForNode(node.NodeId, PipeWirePortDirection.In).First();

            // An input in the output slot, and an output in the input slot, are separate checks and
            // each must name the argument at fault rather than failing generically.
            ArgumentException swapped = Assert.ThrowsExactly<ArgumentException>(
                () => registry.CreateLink(input, output));
            Assert.AreEqual("output", swapped.ParamName);

            ArgumentException badInput = Assert.ThrowsExactly<ArgumentException>(
                () => registry.CreateLink(output, output));
            Assert.AreEqual("input", badInput.ParamName);
        }
    }

    [TestMethod]
    public async Task CreateLink_RejectsNullPortsBeforeTouchingTheDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-nullports", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNodeAsync("N", "pwnet_nullports", cts.Token);
            PipeWireGraphSnapshot graph = await WaitForAsync(
                registry, g => g.GetPortsForNode(node.NodeId).Length == 4, cts.Token);
            PipeWirePort real = graph.GetPortsForNode(node.NodeId, PipeWirePortDirection.Out).First();

            Assert.ThrowsExactly<ArgumentNullException>(() => registry.CreateLink(null!, real));
            Assert.ThrowsExactly<ArgumentNullException>(() => registry.CreateLink(real, null!));
        }
    }

    [TestMethod]
    public async Task UsingADisposedRegistry_ThrowsRatherThanMisbehaving()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-afterdispose", cts.Token);

        await using (context)
        {
            PipeWireGraphSnapshot last = registry.Current;
            await registry.DisposeAsync();

            // Reads keep working: the snapshot is immutable and holding it is legal after disposal.
            Assert.AreEqual(last.Version, registry.Current.Version,
                "the last published snapshot survives disposal");

            // Mutations do not.
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => registry.DestroyGlobalAsync(1, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => registry.CreateVirtualNodeAsync("X", "pwnet_after", CancellationToken.None));
        }
    }
}
