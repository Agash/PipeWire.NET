using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Lifecycle operations performed from inside the callbacks that report them.
/// </summary>
/// <remarks>
/// Handlers run on the loop thread. Anything they call that takes the loop lock succeeds, because
/// the lock is recursive, but anything that waits for the daemon to answer cannot: the thread that
/// would dispatch the answer is the one blocked waiting for it. That distinction is invisible from
/// the call site, so it is pinned here rather than left to be discovered in a host application.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class ReentrancyTests
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

    private static string Unique(string p) => $"{p}_{Environment.ProcessId}_{Random.Shared.Next():x}";

    [TestMethod]
    public async Task ReadingAndBindingFromInsideAGraphCallback_Works()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-reentrant-read", cts.Token);
        await using (ctx)
        await using (registry)
        {
            var faults = new ConcurrentQueue<string>();
            int bound = 0;

            void OnAdded(PipeWireNode n)
            {
                try
                {
                    // Reading the snapshot and binding both take the loop lock, which this thread
                    // already holds. Recursive, so both are legal here.
                    _ = registry.Current.GetNode(n.NodeId);
                    _ = registry.Current.GetPortsForNode(n.NodeId);

                    using PipeWireNodeControl control = registry.BindNode(n.NodeId);
                    Interlocked.Increment(ref bound);
                }
                catch (ArgumentException)
                {
                    // The node went away between the event and the bind. Not a fault.
                }
                catch (Exception ex)
                {
                    faults.Enqueue($"{ex.GetType().Name}: {ex.Message}");
                }
            }

            registry.NodeAdded += OnAdded;
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    PipeWireNode node = await registry.CreateVirtualNode("Reentrant")
                        .WithName(Unique("pwnet_reentrant")).ExecuteAsync(cts.Token);
                    await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
                }

                await registry.WaitForInitialEnumerationAsync(cts.Token);
            }
            finally
            {
                registry.NodeAdded -= OnAdded;
            }

            Assert.IsTrue(faults.IsEmpty, string.Join("; ", faults));
            Assert.IsTrue(Volatile.Read(ref bound) > 0, "no callback ever bound a node");
        }
    }

    [TestMethod]
    public async Task SubscribingAndUnsubscribingFromInsideAHandler_DoesNotDisturbTheOthers()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-reentrant-sub", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // Mutating the invocation list while it is being invoked. A delegate is immutable, so
            // the in-flight invocation runs the list it started with; what must not happen is a
            // handler being skipped or the mutation throwing.
            int stable = 0;
            var faults = new ConcurrentQueue<string>();
            Action<PipeWireNode>? transient = null;

            void Stable(PipeWireNode _) => Interlocked.Increment(ref stable);

            void Mutating(PipeWireNode _)
            {
                try
                {
                    if (transient is null)
                    {
                        transient = static _ => { };
                        registry.NodeAdded += transient;
                    }
                    else
                    {
                        registry.NodeAdded -= transient;
                        transient = null;
                    }
                }
                catch (Exception ex)
                {
                    faults.Enqueue($"{ex.GetType().Name}: {ex.Message}");
                }
            }

            registry.NodeAdded += Mutating;
            registry.NodeAdded += Stable;
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    PipeWireNode node = await registry.CreateVirtualNode("Mutate")
                        .WithName(Unique("pwnet_mutate")).ExecuteAsync(cts.Token);
                    await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
                }

                await registry.WaitForInitialEnumerationAsync(cts.Token);
            }
            finally
            {
                registry.NodeAdded -= Stable;
                registry.NodeAdded -= Mutating;
                if (transient is not null) registry.NodeAdded -= transient;
            }

            Assert.IsTrue(faults.IsEmpty, string.Join("; ", faults));
            Assert.IsTrue(Volatile.Read(ref stable) > 0,
                "the handler that never moved stopped being invoked");
        }
    }

    [TestMethod]
    public async Task AMetadataHandlerWritingBackToTheStore_DoesNotDeadlock()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-reentrant-meta", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null) Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await store!.ReadyAsync(cts.Token);

                string trigger = Unique("pwnet.reentrant.trigger");
                string echo = Unique("pwnet.reentrant.echo");
                var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var faults = new ConcurrentQueue<string>();

                void OnChanged(PipeWireMetadataStore s, PipeWireMetadataEntry e)
                {
                    if (e.Key != trigger || e.Value is null) return;

                    // A write from inside the handler. It must not be awaited here: this is the
                    // loop thread, and the round-trip it waits on is answered by this same thread.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await s.SetAsync(echo, "seen", cancellationToken: cts.Token);
                            wrote.TrySetResult();
                        }
                        catch (Exception ex) { faults.Enqueue($"{ex.GetType().Name}: {ex.Message}"); }
                    }, cts.Token);
                }

                store.EntryChanged += OnChanged;
                try
                {
                    await store.SetAsync(trigger, "go", cancellationToken: cts.Token);
                    await wrote.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

                    Assert.IsTrue(faults.IsEmpty, string.Join("; ", faults));
                    Assert.AreEqual("seen", store.Get(echo));
                }
                finally
                {
                    store.EntryChanged -= OnChanged;
                    await store.SetAsync(trigger, null, cancellationToken: CancellationToken.None);
                    await store.SetAsync(echo, null, cancellationToken: CancellationToken.None);
                }
            }
        }
    }

    [TestMethod]
    public async Task DisposingAControlFromInsideItsOwnCallback_TearsDownWithoutCrashing()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-reentrant-dispose", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // Destroying a proxy on the loop thread can happen while the daemon dispatches through
            // that proxy's own listener, and the destroy frees the hook the dispatch is walking.
            var faults = new ConcurrentQueue<string>();

            for (int round = 0; round < 6; round++)
            {
                PipeWireNode node = await registry.CreateVirtualNode("SelfDispose")
                    .WithName(Unique("pwnet_selfdispose")).ExecuteAsync(cts.Token);

                PipeWireNodeControl control = registry.BindNode(node.NodeId);
                var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                control.InfoChanged += _ =>
                {
                    try
                    {
                        control.Dispose();
                        attempted.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        faults.Enqueue($"{ex.GetType().Name}: {ex.Message}");
                        attempted.TrySetResult();
                    }
                };

                await control.ReadyAsync(cts.Token);
                await attempted.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);

                // Disposing again from a normal thread must be a no-op, not a second teardown.
                control.Dispose();

                await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
            }

            Assert.IsTrue(faults.IsEmpty, string.Join("; ", faults));

            // And the connection still works, which a corrupted listener list would not leave it.
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsTrue(registry.Current.Nodes.Length > 0);
        }
    }

    [TestMethod]
    public async Task DisposingTheContextFromItsOwnCallback_SaysSoRatherThanHanging()
    {
        // pw_thread_loop_stop joins the loop thread. Asked for from inside a callback, that is the
        // thread waiting for itself: the process stops with nothing in the log to explain it, and
        // no timeout anywhere to break it. There is no correct way to satisfy the request, so the
        // only useful answer is a clear refusal. The wait below is the real assertion: if this ever
        // regresses to a join, the test times out instead of hanging the run for ever.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-dispose-self", cts.Token);

        var attempted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnAdded(PipeWireNode _)
        {
            if (attempted.Task.IsCompleted) return;

            try
            {
                ctx.Dispose();
                attempted.TrySetResult(null);
            }
            catch (Exception ex)
            {
                attempted.TrySetResult(ex);
            }
        }

        registry.NodeAdded += OnAdded;
        try
        {
            PipeWireNode node = await registry.CreateVirtualNode("DisposeSelf")
                .WithName(Unique("pwnet_dispose_self")).ExecuteAsync(cts.Token);

            Exception? thrown = await attempted.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

            Assert.IsInstanceOfType<InvalidOperationException>(thrown,
                "disposing from the loop thread has to be refused, not attempted");
            Assert.Contains("loop thread", thrown!.Message, StringComparison.Ordinal);

            // Refused, so the context is untouched and still works.
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsNotNull(registry.Current.GetNode(node.NodeId));
            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
        finally
        {
            registry.NodeAdded -= OnAdded;
            await registry.DisposeAsync();
            await ctx.DisposeAsync();
        }
    }
}
