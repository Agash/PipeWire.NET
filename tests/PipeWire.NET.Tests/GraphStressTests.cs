using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Generated;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Adversarial tests: hostile input, churn, concurrency and teardown races. These exist to break the
/// graph layer, not to demonstrate it working.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class GraphStressTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

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

    /// <summary>Open file descriptors for this process; the cheapest leak detector on Linux.</summary>
    private static int OpenFileDescriptors() =>
        Directory.GetFileSystemEntries($"/proc/{Environment.ProcessId}/fd").Length;

    [TestMethod]
    [DataRow(200)]
    [DataRow(4096)]
    [DataRow(64 * 1024)]
    public async Task ALongDescription_IsAcceptedRatherThanHittingOurOwnBuffer(int length)
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-long", cts.Token);

        await using (context)
        await using (registry)
        {
            // spa_dict_item is a pair of const char* with no length limit, so the library must not
            // impose one of its own. A fixed stackalloc used to fail here well before the daemon did.
            var huge = new string('x', length);

            PipeWireNode node = await registry.CreateVirtualStereoNodeAsync(
                huge, $"pwnet_long_{length}", cts.Token);

            Assert.IsNotNull(registry.Current.GetNode(node.NodeId));
            Assert.AreEqual(length, node.Description?.Length,
                "the description must survive the round-trip intact");
        }
    }

    [TestMethod]
    public async Task AMultiByteDescription_IsMeasuredInBytesNotChars()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-utf8", cts.Token);

        await using (context)
        await using (registry)
        {
            // Four bytes per char, so a char-based size estimate would under-reserve by 4x.
            var emoji = string.Concat(Enumerable.Repeat("🔊", 300));

            PipeWireNode node = await registry.CreateVirtualStereoNodeAsync(emoji, "pwnet_utf8", cts.Token);
            Assert.AreEqual(emoji, node.Description);
        }
    }

    [TestMethod]
    public async Task RepeatedCreateAndDestroy_LeaksNeitherDescriptorsNorMemory()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-churn", cts.Token);

        await using (context)
        await using (registry)
        {
            // Warm up so first-call allocations are not counted as growth.
            for (int i = 0; i < 5; i++)
            {
                PipeWireNode warm = await registry.CreateVirtualStereoNodeAsync($"W{i}", $"pwnet_w{i}", cts.Token);
                await registry.RemoveObjectAsync(warm.NodeId, cts.Token);
                await WaitForAsync(registry, g => g.GetNode(warm.NodeId) is null, cts.Token);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int fdsBefore = OpenFileDescriptors();
            long heapBefore = GC.GetTotalMemory(forceFullCollection: true);

            const int Iterations = 60;
            for (int i = 0; i < Iterations; i++)
            {
                PipeWireNode node = await registry.CreateVirtualStereoNodeAsync(
                    $"Churn {i}", $"pwnet_churn_{i}", cts.Token);
                await registry.RemoveObjectAsync(node.NodeId, cts.Token);
                await WaitForAsync(registry, g => g.GetNode(node.NodeId) is null, cts.Token);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int fdsAfter = OpenFileDescriptors();
            long heapAfter = GC.GetTotalMemory(forceFullCollection: true);

            Console.Error.WriteLine(
                $"churn x{Iterations}: fds {fdsBefore} -> {fdsAfter}, heap {heapBefore} -> {heapAfter}");

            Assert.IsTrue(fdsAfter <= fdsBefore + 2,
                $"file descriptors grew {fdsBefore} -> {fdsAfter} over {Iterations} create/destroy cycles");

            // Each cycle allocating even 1KB that never returns would show as >60KB here.
            Assert.IsTrue(heapAfter - heapBefore < 512 * 1024,
                $"managed heap grew {heapAfter - heapBefore} bytes over {Iterations} cycles");
        }
    }

    [TestMethod]
    public async Task ConcurrentCreates_AllSucceedAndAllReachTheGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-parallel", cts.Token);

        await using (context)
        await using (registry)
        {
            const int Degree = 12;
            Task<PipeWireNode>[] creates = [.. Enumerable.Range(0, Degree).Select(
                i => Task.Run(() => registry.CreateVirtualStereoNodeAsync($"P{i}", $"pwnet_par_{i}", cts.Token), cts.Token))];

            PipeWireNode[] nodes = await Task.WhenAll(creates);

            Assert.AreEqual(Degree, nodes.Select(n => n.NodeId).Distinct().Count(),
                "concurrent creations must not collide on an id");

            PipeWireGraphSnapshot graph = registry.Current;
            foreach (PipeWireNode node in nodes)
                Assert.IsNotNull(graph.GetNode(node.NodeId), $"node {node.NodeId} never reached the graph");
        }
    }

    [TestMethod]
    public async Task DisposingWhileCreationsAreInFlight_DoesNotCrash()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-teardown", cts.Token);

        await using (context)
        {
            Task<PipeWireNode>[] creates = [.. Enumerable.Range(0, 8).Select(
                i => registry.CreateVirtualStereoNodeAsync($"T{i}", $"pwnet_tear_{i}", cts.Token))];

            // Tear down underneath them. Whatever each task does, the process must survive: an
            // ObjectDisposedException or a completed node are both fine, an abort is not.
            await registry.DisposeAsync();

            // Every task must settle promptly. A generous but finite bound is the point: disposal
            // has to fail whatever is waiting on a global that will now never arrive, otherwise a
            // caller passing CancellationToken.None waits forever. Bounding it here is what turns
            // that hang into a failure instead of a slow pass.
            int completed = 0;
            var refusals = new List<string>();
            var settle = Task.WhenAll(creates);
            Task finished = await Task.WhenAny(settle, Task.Delay(TimeSpan.FromSeconds(10), cts.Token));

            Assert.AreSame(settle, finished,
                "creations in flight did not settle within 10s of disposal; disposal is not failing them");

            // Naming the acceptable failures rather than catching a broad set: anything else
            // escaping here fails the test, which is the point of running it.
            foreach (Task<PipeWireNode> create in creates)
            {
                try
                {
                    PipeWireNode node = await create;
                    Assert.AreNotEqual(0u, node.NodeId, "a completed creation must yield a real node");
                    completed++;
                }
                catch (ObjectDisposedException) { refusals.Add(nameof(ObjectDisposedException)); }
                catch (OperationCanceledException) { refusals.Add(nameof(OperationCanceledException)); }
            }

            Assert.AreEqual(creates.Length, completed + refusals.Count,
                "every in-flight creation must settle one way or the other");
            Assert.IsTrue(refusals.Count > 0,
                $"disposal under load refused nothing; all {completed} creations beat it, so the "
                + "race this test exists for was never exercised");
        }
    }

    [TestMethod]
    public async Task DisposingWithNoCancellationToken_StillReleasesInFlightCreations()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-notoken", cts.Token);

        await using (context)
        {
            // CancellationToken.None is the case with no escape hatch: if disposal does not fail
            // the waiters, nothing ever will.
            Task<PipeWireNode>[] creates = [.. Enumerable.Range(0, 6).Select(
                i => registry.CreateVirtualStereoNodeAsync($"N{i}", $"pwnet_notok_{i}", CancellationToken.None))];

            await registry.DisposeAsync();

            Task settle = Task.WhenAll(creates);
            Task finished = await Task.WhenAny(settle, Task.Delay(TimeSpan.FromSeconds(10), cts.Token));
            Assert.AreSame(settle, finished,
                "with no token, only disposal can release an in-flight creation - and it did not");

            // Classify every outcome. Swallowing them lets an unexpected failure, or a task that
            // resolved to nonsense after disposal, pass as success.
            var outcomes = new List<string>();
            foreach (Task<PipeWireNode> create in creates)
            {
                try
                {
                    PipeWireNode node = await create;
                    Assert.AreNotEqual(0u, node.NodeId, "a completed creation must yield a real node");
                    outcomes.Add("created");
                }
                catch (ObjectDisposedException) { outcomes.Add("disposed"); }
                catch (OperationCanceledException) { outcomes.Add("cancelled"); }
            }

            Assert.AreEqual(creates.Length, outcomes.Count);
            Assert.IsTrue(outcomes.Contains("disposed"),
                "with no token, disposal is the only thing that can release these; it released none. "
                + $"outcomes: [{string.Join(", ", outcomes)}]");
        }
    }

    [TestMethod]
    public async Task CancellingMidCreation_LeavesNothingBehind()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-cancel", cts.Token);

        await using (context)
        await using (registry)
        {
            int fdsBefore = OpenFileDescriptors();
            int cancelled = 0, created = 0;

            for (int i = 0; i < 25; i++)
            {
                using var tight = new CancellationTokenSource();
                // Cancel after a delay short enough to land inside the round-trip most of the time.
                tight.CancelAfter(TimeSpan.FromMilliseconds(i % 5));
                try
                {
                    PipeWireNode node = await registry.CreateVirtualStereoNodeAsync(
                        $"C{i}", $"pwnet_cx_{i}", tight.Token);
                    created++;
                    await registry.RemoveObjectAsync(node.NodeId, cts.Token);
                }
                catch (OperationCanceledException) { cancelled++; }
            }

            // Without this the test degrades silently into a plain create/remove loop whenever the
            // cancellations all lose the race, and then proves nothing about the cancelled path.
            Assert.IsTrue(cancelled > 0,
                $"no creation was actually cancelled ({created} completed); the test exercised nothing");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int fdsAfter = OpenFileDescriptors();
            Console.Error.WriteLine($"cancel churn: fds {fdsBefore} -> {fdsAfter}");
            Assert.IsTrue(fdsAfter <= fdsBefore + 2,
                $"cancelled creations leaked descriptors: {fdsBefore} -> {fdsAfter}");
        }
    }

    [TestMethod]
    public async Task ManyWatchersSubscribingAndLeaving_DoNotAccumulate()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-watchers", cts.Token);

        await using (context)
        await using (registry)
        {
            long heapBefore = GC.GetTotalMemory(forceFullCollection: true);

            for (int i = 0; i < 200; i++)
            {
                using var stop = new CancellationTokenSource();
                await foreach (PipeWireGraphSnapshot _ in registry.WatchAsync(stop.Token))
                    break;   // take the priming snapshot and abandon the stream
            }

            long heapAfter = GC.GetTotalMemory(forceFullCollection: true);
            Console.Error.WriteLine($"watchers: heap {heapBefore} -> {heapAfter}");

            // A handler left subscribed per iteration would retain its channel and grow steadily.
            Assert.IsTrue(heapAfter - heapBefore < 512 * 1024,
                $"abandoned watchers retained {heapAfter - heapBefore} bytes");
        }
    }

    [TestMethod]
    public async Task ASnapshotHeldAcrossHeavyChurn_StaysInternallyConsistent()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-consistency", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode a = await registry.CreateVirtualStereoNodeAsync("CA", "pwnet_ca", cts.Token);
            PipeWireGraphSnapshot held = await WaitForAsync(
                registry, g => g.GetPortsForNode(a.NodeId).Length == 4, cts.Token);

            int portsAtCapture = held.Ports.Length;

            for (int i = 0; i < 15; i++)
            {
                PipeWireNode churn = await registry.CreateVirtualStereoNodeAsync($"CC{i}", $"pwnet_cc_{i}", cts.Token);
                await registry.RemoveObjectAsync(churn.NodeId, cts.Token);
            }

            Assert.AreEqual(portsAtCapture, held.Ports.Length, "a held snapshot must not change");

            // Every port index entry must still resolve against the same snapshot.
            foreach (PipeWirePort port in held.Ports)
                Assert.IsNotNull(held.GetPort(port.PortId), $"port {port.PortId} is in Ports but not the index");

            foreach (PipeWireLink link in held.Links)
            {
                Assert.IsTrue(held.GetOutputLinksForPort(link.LinkOutputPort).Contains(link));
                Assert.IsTrue(held.GetInputLinksForPort(link.LinkInputPort).Contains(link));
            }
        }
    }

    [TestMethod]
    public async Task RemovingAnIdThatNeverExisted_IsReportedAsAFailure()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-bogus", cts.Token);

        await using (context)
        await using (registry)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => registry.RemoveObjectAsync(999_999, cts.Token));
        }
    }

    [TestMethod]
    public async Task RemovingAnObjectTwice_IsRefusedRatherThanFatal()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext context, PipeWireRegistry registry) = await ConnectAsync("pwnet-double", cts.Token);

        await using (context)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualStereoNodeAsync("DD", "pwnet_dd", cts.Token);

            await registry.RemoveObjectAsync(node.NodeId, cts.Token);
            await WaitForAsync(registry, g => g.GetNode(node.NodeId) is null, cts.Token);

            // The id is gone from the daemon, so the second destroy must be *reported* as refused.
            // destroy_global returns SPA_ASYNC_BIT | seq for both outcomes, so an implementation
            // that inspects the return value alone silently reports success here.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => registry.RemoveObjectAsync(node.NodeId, cts.Token));
        }
    }
}
