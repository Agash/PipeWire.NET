using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// The context's lifecycle protocol: admission order, one-shot scopes, start/dispose races.
/// </summary>
/// <remarks>
/// Start and lock scopes must take the lifecycle gate and native loop mutex in the same order.
/// Every test here joins its threads with a timeout and unblocks on failure, so a regression
/// fails the test rather than the run.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class LifecycleLockTests
{
    private static readonly TimeSpan JoinBudget = TimeSpan.FromSeconds(15);

    [TestMethod]
    public async Task HoldingAScopeAcrossAnotherThreadsStart_CompletesBoth()
    {

        using var ctx = new PipeWireContext("PipeWire.NET.Test.LifecycleStart");
        using var starterEntered = new ManualResetEventSlim(false);

        // Holds the native lock while a second thread starts the context, then re-locks on the
        // same thread.
        Task holder = Task.Run(() =>
        {
            using (ctx.Lock())
            {
                starterEntered.Set();
                Thread.Sleep(500);
                using (ctx.Lock())
                {
                }
            }
        });

        starterEntered.Wait(JoinBudget);
        Task starter = Task.Run(() =>
        {
            Task t = ctx.StartAsync();
            t.GetAwaiter().GetResult();
        });

        await holder.WaitAsync(JoinBudget);
        await starter.WaitAsync(JoinBudget);
        ctx.Dispose();
    }

    [TestMethod]
    public async Task TwoConcurrentStarts_BothReportSuccess()
    {
        using var ctx = new PipeWireContext("PipeWire.NET.Test.LifecycleRacingStarts");

        Task first = Task.Run(() => ctx.StartAsync());
        Task second = Task.Run(() => ctx.StartAsync());

        await first.WaitAsync(JoinBudget);
        await second.WaitAsync(JoinBudget);
        ctx.Dispose();
    }

    [TestMethod]
    public void DisposingWhileAScopeIsHeld_WaitsForTheScope()
    {
        using var ctx = new PipeWireContext("PipeWire.NET.Test.LifecycleDrain");
        using var cts = new CancellationTokenSource(JoinBudget);
        ctx.StartAsync(cts.Token).GetAwaiter().GetResult();

        // A ref struct cannot cross an await, so this test blocks instead.
        Assert.IsTrue(ctx.TryLock(out PipeWireContext.LoopLock scope));

        // Disposal must wait the scope out rather than tear the loop down under it. It runs on
        // its own thread because the wait is the behavior under test.
        Task disposal = Task.Run(() => ctx.Dispose());
        Thread.Sleep(300);
        Assert.IsFalse(disposal.IsCompleted, "disposal tore down while a scope was still held");

        scope.Dispose();
        Assert.IsTrue(disposal.Wait(JoinBudget), "disposal did not finish after its scope closed");
    }

    [TestMethod]
    public async Task DisposingACopyAndTheOriginal_UnlocksExactlyOnce()
    {
        using var ctx = new PipeWireContext("PipeWire.NET.Test.LifecycleCopy");
        using var cts = new CancellationTokenSource(JoinBudget);
        await ctx.StartAsync(cts.Token);

        // Copies share one lease: both disposals together release exactly once. A double unlock
        // corrupts the native mutex, which surfaces as a hang or crash in later scopes - so the
        // proof is that the context keeps working afterwards and disposes cleanly.
        PipeWireContext.LoopLock original = ctx.Lock();
        PipeWireContext.LoopLock copy = original;
        copy.Dispose();
        original.Dispose();

        using (ctx.Lock())
        {
        }

        ctx.Dispose();
        Assert.IsTrue(ctx.IsDisposed);
    }

    [TestMethod]
    public async Task AbandonedStreamsAndFilters_FinalizeWithoutWaitingForABusyLoop()
    {
        // Stream and filter destroys are heavier (disconnect plus listener teardown).
        using var ctx = new PipeWireContext("PipeWire.NET.Test.LifecycleReaperMedia");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await ctx.StartAsync(cts.Token);

        var abandoned = AbandonMedia(ctx);
        GC.Collect();

        Assert.IsTrue(ctx.TryLock(out PipeWireContext.LoopLock scope));

        Task finalize = Task.Run(GC.WaitForPendingFinalizers);
        bool done = finalize.Wait(TimeSpan.FromSeconds(30));
        scope.Dispose();

        Assert.IsTrue(done, "finalizers never finished; abandoned handles are waiting on the held loop");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (WeakReference w in abandoned)
            Assert.IsFalse(w.IsAlive, "an abandoned stream or filter was never collected");

        ctx.Dispose();
        Assert.IsTrue(ctx.IsDisposed);
    }

    private static System.Collections.Generic.List<WeakReference> AbandonMedia(PipeWireContext ctx)
    {
        var abandoned = new System.Collections.Generic.List<WeakReference>();

        var output = new PipeWireAudioOutput(ctx, "PipeWire.NET.Test.Abandoned",
            sampleRate: 48000, channels: 2, format: PipeWire.NET.Media.AudioSampleFormat.F32Le);
        output.Connect(autoConnect: false);
        abandoned.Add(new WeakReference(output));

        PipeWireFilter filter = PipeWireFilter.Create(ctx, "pwnet_abandoned_filter");
        abandoned.Add(new WeakReference(filter));

        return abandoned;
    }

    [TestMethod]
    public async Task LockingAfterDispose_RefusesInsteadOfLocking()
    {
        var ctx = new PipeWireContext("PipeWire.NET.Test.LifecycleAfterDispose");
        using var cts = new CancellationTokenSource(JoinBudget);
        await ctx.StartAsync(cts.Token);
        ctx.Dispose();

        Assert.IsFalse(ctx.TryLock(out _));
        Assert.ThrowsExactly<ObjectDisposedException>(() => ctx.Lock());
        Assert.ThrowsExactly<ObjectDisposedException>(() => ctx.StartAsync().GetAwaiter().GetResult());
    }

    [TestMethod]
    public unsafe void ADisposedUnstartedContext_RefusesItsHandles()
    {
        // No daemon needed: construction alone takes no connection, and disposal tears down
        // nothing but the loop. Every handle accessor after that must refuse rather than hand
        // out a pointer from under teardown. Pointer accessors assert by hand: a lambda cannot
        // carry a native pointer.
        var ctx = new PipeWireContext("PipeWire.NET.Test.LifecycleHandles");
        ctx.Dispose();

        Assert.IsTrue(ctx.IsDisposed);
        Assert.IsFalse(ctx.TryLock(out _));
        Assert.ThrowsExactly<ObjectDisposedException>(() => ctx.Lock());
        Assert.ThrowsExactly<ObjectDisposedException>(() => { _ = ctx.LoopOwner; });
        Assert.IsFalse(ctx.IsOnLoopThread);

        try { _ = (nint)ctx.CoreHandle; Assert.Fail("CoreHandle must refuse"); }
        catch (ObjectDisposedException) { }
        try { _ = (nint)ctx.ContextHandle; Assert.Fail("ContextHandle must refuse"); }
        catch (ObjectDisposedException) { }
        try { _ = (nint)ctx.LoopHandle; Assert.Fail("LoopHandle must refuse"); }
        catch (ObjectDisposedException) { }
    }

    [TestMethod]
    public async Task AbandonedHandles_FinalizeWithoutWaitingForABusyLoop()
    {
        // The finalizer must never block on the loop mutex: abandoned proxy handles go to the
        // reaper, which waits off-thread.
        using var ctx = new PipeWireContext("PipeWire.NET.Test.LifecycleReaper");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode node = await registry.CreateVirtualNodeAsync(
            "ReaperSrc", "pwnet_reaper_src", cts.Token);

        // Abandoned in a separate frame: values created inside this async method can stay rooted
        // by its state machine, which would make the collection below vacuous. Nothing here
        // outlives the call, so everything it made is collectable when it returns.
        System.Collections.Generic.List<WeakReference> abandoned =
            AbandonBindings(registry, node.NodeId);

        // Queue the finalizers without running them: allocated but unreachable after this.
        GC.Collect();

        Assert.IsTrue(ctx.TryLock(out PipeWireContext.LoopLock scope));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Bounded so a failure fails the test instead of hanging the run.
        Task finalize = Task.Run(GC.WaitForPendingFinalizers);
        bool done = finalize.Wait(TimeSpan.FromSeconds(30));
        sw.Stop();

        scope.Dispose();

        Assert.IsTrue(done, "finalizers never finished; abandoned handles are waiting on the held loop");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(4),
            $"finalizers took {sw.Elapsed} with the loop held; abandoned handles must not block on it");

        // And they really did run: still-alive references mean the measurement above was vacuous.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (WeakReference w in abandoned)
            Assert.IsFalse(w.IsAlive, "an abandoned binding was never collected");

        // The loop still works afterwards, and everything tears down cleanly.
        await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        ctx.Dispose();
        Assert.IsTrue(ctx.IsDisposed);
    }

    private static System.Collections.Generic.List<WeakReference> AbandonBindings(
        PipeWireRegistry registry, uint nodeId)
    {
        var abandoned = new System.Collections.Generic.List<WeakReference>();
        for (int i = 0; i < 8; i++)
            abandoned.Add(new WeakReference(registry.BindNode(nodeId)));

        return abandoned;
    }
}
