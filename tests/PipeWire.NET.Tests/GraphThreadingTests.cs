using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Threading behaviour around the single <c>pw_thread_loop</c>: re-entrancy from callbacks, readers
/// racing the writer, and teardown from the wrong thread.
/// </summary>
/// <remarks>
/// Callbacks run on the loop thread while it holds the loop lock. Anything a consumer is allowed to
/// do from a handler therefore has to be safe to do while that lock is held, and these tests pin
/// which operations those are rather than leaving it to be discovered in an application.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class GraphThreadingTests
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
    public async Task ReadingTheGraphFromInsideAHandler_DoesNotDeadlock()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-reentrant", cts.Token);

        await using (context)
        await using (registry)
        {
            // The handler runs on the loop thread with the loop lock held. Reading Current is the
            // single most likely thing a consumer does here, so it must not need the lock at all.
            var readsFromHandler = 0;
            Exception? faulted = null;

            registry.GraphChanged += (sender, snapshot) =>
            {
                try
                {
                    int nodes = snapshot.Nodes.Length;
                    int ports = sender.Current.Ports.Length;
                    int forNode = sender.Current.GetPortsForNode(1).Length;
                    if (nodes < 0 || ports < 0 || forNode < 0) throw new InvalidOperationException("impossible");
                    Interlocked.Increment(ref readsFromHandler);
                }
                catch (Exception ex) { faulted ??= ex; }
            };

            PipeWireNode node = await registry.CreateVirtualNodeAsync("RE", "pwnet_reentrant", cts.Token);
            await WaitForAsync(registry, g => g.GetPortsForNode(node.NodeId).Length == 4, cts.Token);

            Assert.IsNull(faulted, $"reading the graph from a handler threw: {faulted}");
            Assert.IsTrue(Volatile.Read(ref readsFromHandler) > 0, "no handler ever ran");
        }
    }

    [TestMethod]
    public async Task HandlersRunOnTheLoopThread_NotTheCallersThread()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-threadid", cts.Token);

        await using (context)
        await using (registry)
        {
            int callerThread = Environment.CurrentManagedThreadId;
            var handlerThreads = new ConcurrentBag<int>();

            registry.PortAdded += _ => handlerThreads.Add(Environment.CurrentManagedThreadId);

            PipeWireNode node = await registry.CreateVirtualNodeAsync("TI", "pwnet_threadid", cts.Token);
            await WaitForAsync(registry, g => g.GetPortsForNode(node.NodeId).Length == 4, cts.Token);

            Assert.IsFalse(handlerThreads.IsEmpty, "no PortAdded handler ran");
            Assert.IsFalse(handlerThreads.Contains(callerThread),
                "handlers must not run on the caller's thread; consumers marshal off the loop themselves");
            Assert.AreEqual(1, handlerThreads.Distinct().Count(),
                "every callback must arrive on the one loop thread");
        }
    }

    [TestMethod]
    public async Task AThrowingHandler_DoesNotKillTheLoopOrLoseLaterEvents()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-throwing", cts.Token);

        await using (context)
        await using (registry)
        {
            // An exception escaping a reverse P/Invoke aborts the process, so the registry has to
            // swallow it at the boundary. Surviving is necessary but not sufficient: the loop must
            // still deliver everything afterwards.
            var seen = 0;
            registry.PortAdded += _ => { Interlocked.Increment(ref seen); throw new InvalidOperationException("boom"); };

            PipeWireNode first = await registry.CreateVirtualNodeAsync("T1", "pwnet_throw_1", cts.Token);
            await WaitForAsync(registry, g => g.GetPortsForNode(first.NodeId).Length == 4, cts.Token);

            PipeWireNode second = await registry.CreateVirtualNodeAsync("T2", "pwnet_throw_2", cts.Token);
            PipeWireGraphSnapshot after = await WaitForAsync(
                registry, g => g.GetPortsForNode(second.NodeId).Length == 4, cts.Token);

            Assert.AreEqual(4, after.GetPortsForNode(second.NodeId).Length,
                "events after a throwing handler must still be delivered");
            Assert.IsTrue(Volatile.Read(ref seen) >= 8, $"expected at least 8 port events, saw {seen}");
        }
    }

    [TestMethod]
    public async Task ManyReadersRacingTheWriter_NeverSeeATornGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-race", cts.Token);

        await using (context)
        await using (registry)
        {
            using var stop = new CancellationTokenSource();
            Exception? faulted = null;
            long reads = 0;

            // Eight threads walking the graph while the loop thread republishes it underneath them.
            Task[] readers = [.. Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        PipeWireGraphSnapshot g = registry.Current;

                        // Every index must agree with the arrays inside one snapshot, whatever the
                        // writer is doing concurrently.
                        foreach (PipeWirePort port in g.Ports)
                            if (g.GetPort(port.PortId) is null)
                                throw new InvalidOperationException($"port {port.PortId} missing from its own index");

                        foreach (PipeWireLink link in g.Links)
                            if (!g.GetOutputLinksForPort(link.LinkOutputPort).Contains(link))
                                throw new InvalidOperationException($"link {link.LinkId} missing from its own index");

                        Interlocked.Increment(ref reads);
                    }
                }
                catch (Exception ex) { faulted ??= ex; }
            }))];

            for (int i = 0; i < 20; i++)
            {
                PipeWireNode n = await registry.CreateVirtualNodeAsync($"R{i}", $"pwnet_race_{i}", cts.Token);
                await registry.DestroyGlobalAsync(n.NodeId, cts.Token);
            }

            await stop.CancelAsync();
            await Task.WhenAll(readers);

            Assert.IsNull(faulted, $"a reader saw an inconsistent graph: {faulted}");
            Assert.IsTrue(Volatile.Read(ref reads) > 100, $"readers barely ran ({reads} reads); the race was not exercised");
        }
    }

    [TestMethod]
    [TestCategory("RequiresPipeWire168")]
    public async Task CreatingFromInsideAHandler_CompletesRatherThanDeadlocking()
    {
        RequireLinux();
        // Destroying from inside the handler races the same way the hostile creation tests do,
        // and 1.0.5 answers with a hang followed by a dead daemon.
        SessionGates.RequireDaemonAtLeast(1, 6, 8);
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-nested", cts.Token);

        await using (context)
        await using (registry)
        {
            // Issuing a request from a handler means taking the loop lock while already holding it.
            // pw_thread_loop uses a recursive mutex, so this is legal; awaiting the *result* from
            // the loop thread would not be, which is why the handler fires and forgets.
            var issued = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var once = 0;

            registry.NodeAdded += node =>
            {
                if (node.NodeName != "pwnet_nested_trigger") return;
                if (Interlocked.Exchange(ref once, 1) != 0) return;

                try
                {
                    _ = registry.DestroyGlobalAsync(node.NodeId, CancellationToken.None);
                    issued.TrySetResult(true);
                }
                catch (Exception ex) { issued.TrySetException(ex); }
            };

            PipeWireNode trigger = await registry.CreateVirtualNodeAsync(
                "Nested", "pwnet_nested_trigger", cts.Token);

            Assert.IsTrue(await issued.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token),
                "the handler never issued its request");

            await WaitForAsync(registry, g => g.GetNode(trigger.NodeId) is null, cts.Token);
        }
    }

    [TestMethod]
    public async Task ConcurrentWatchersAndMutators_AllMakeProgress()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-multiwatch", cts.Token);

        await using (context)
        await using (registry)
        {
            using var stop = new CancellationTokenSource();
            var counts = new int[6];

            Task[] watchers = [.. Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
            {
                try
                {
                    await foreach (PipeWireGraphSnapshot _ in registry.WatchAsync(stop.Token))
                        Interlocked.Increment(ref counts[i]);
                }
                catch (OperationCanceledException)
                {
                    // The expected end: `stop` was cancelled. Any other exception fails the task,
                    // and Task.WhenAll below surfaces it.
                }
            }, CancellationToken.None))];

            for (int i = 0; i < 10; i++)
            {
                PipeWireNode n = await registry.CreateVirtualNodeAsync($"MW{i}", $"pwnet_mw_{i}", cts.Token);
                await registry.DestroyGlobalAsync(n.NodeId, cts.Token);
            }

            await stop.CancelAsync();
            await Task.WhenAll(watchers);

            for (int i = 0; i < counts.Length; i++)
                Assert.IsTrue(counts[i] > 0, $"watcher {i} received nothing; a slow consumer starved it");
        }
    }

    [TestMethod]
    public async Task DisposingFromAThreadPoolThreadWhileEventsFlow_IsClean()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-dispose-race", cts.Token);

        await using (context)
        {
            // Keep the graph changing, then tear down from a different thread than the one that
            // built it. Disposal takes the loop lock, so this is where an ordering bug shows up.
            // Count what the churn achieved. Without this the test passes even if the very first
            // iteration threw, which would mean the disposal never raced anything.
            int churned = 0;
            Exception? churnEnded = null;
            Task churn = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < 30; i++)
                    {
                        PipeWireNode n = await registry.CreateVirtualNodeAsync($"DR{i}", $"pwnet_dr_{i}", cts.Token);
                        await registry.DestroyGlobalAsync(n.NodeId, cts.Token);
                        Interlocked.Increment(ref churned);
                    }
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
                {
                    churnEnded = ex;      // the expected way for disposal to stop the loop
                }
            }, CancellationToken.None);

            await Task.Delay(150, cts.Token);
            await Task.Run(async () => await registry.DisposeAsync(), CancellationToken.None);
            await churn;

            Assert.IsTrue(Volatile.Read(ref churned) > 0,
                "the churn loop never completed an iteration, so disposal raced nothing");
            Assert.IsTrue(churnEnded is not null || Volatile.Read(ref churned) == 30,
                $"the churn stopped after {churned} iterations without a disposal-related exception");

            // Reaching here without an abort is the point, but assert the registry is actually shut
            // so a disposal that silently did nothing cannot pass.
            Assert.ThrowsExactly<ObjectDisposedException>(
                () => registry.DestroyGlobalAsync(1, CancellationToken.None));
        }
    }
}
