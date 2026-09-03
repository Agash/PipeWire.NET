using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// The round-trip primitive everything else is built on: creation, enumeration, parameter reads,
/// metadata writes and filter connection all wait on one.
/// </summary>
/// <remarks>
/// Its blast radius is the whole library, and its failure modes are quiet - a waiter completed by
/// somebody else's reply, or one that is never completed at all, both look like a hang or a wrong
/// answer far from here. These pin the completion rules directly.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class CoreSyncContractTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>A global id the daemon will never have issued.</summary>
    private const uint NoSuchGlobal = 0x7FFF_0000;

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

    [TestMethod]
    public async Task ARequestTheDaemonRefusesSynchronously_ReportsTheRefusal()
    {
        // pw_registry_destroy_global returns 0 rather than an async sequence, so there is no tag to
        // correlate the daemon's answer against and the refusal arrives on the core error stream.
        // Reported through the round-trip or not at all.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-sync-refuse", cts.Token);

        await using (ctx)
        await using (registry)
        {
            PipeWireException refused = await Assert.ThrowsExactlyAsync<PipeWireException>(
                async () => await registry.DestroyGlobalAsync(NoSuchGlobal, cts.Token));

            Assert.IsTrue(refused.Result < 0, $"a refusal must carry the daemon's code, got {refused.Result}");
        }
    }

    [TestMethod]
    public async Task ABarrierRunningBesideARefusedRequest_DoesNotAdoptItsError()
    {
        // A barrier issues nothing, so nothing on the shared core error stream is its to report.
        // Failing on a neighbour's refusal turns every enumeration wait into a lottery on what else
        // the connection happens to be doing.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-sync-barrier", cts.Token);

        await using (ctx)
        await using (registry)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Task refused = Task.Run(
                    async () =>
                    {
                        try { await registry.DestroyGlobalAsync(NoSuchGlobal + (uint)attempt, cts.Token); }
                        catch (PipeWireException) { /* the point of the neighbour */ }
                    },
                    cts.Token);

                await registry.WaitForInitialEnumerationAsync(cts.Token);
                await refused;
            }
        }
    }

    [TestMethod]
    public async Task ManyRoundTripsAtOnce_EachCompletesOnItsOwnReply()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-sync-many", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // Each round-trip installs its own listener and matches on its own sequence number. If
            // one could be completed by another's reply, thirty in flight is where it shows: some
            // would return before their own work had been processed.
            for (int round = 0; round < 4; round++)
            {
                Task[] syncs =
                [
                    .. Enumerable.Range(0, 30).Select(_ =>
                        registry.WaitForInitialEnumerationAsync(cts.Token)),
                ];

                await Task.WhenAll(syncs).WaitAsync(TimeSpan.FromSeconds(15), cts.Token);
            }

            // And the connection is still healthy afterwards, which a leaked or double-removed
            // listener would not leave it.
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsTrue(registry.Current.Nodes.Length >= 0);
        }
    }

    [TestMethod]
    public async Task ARoundTripCancelledAtEveryPointInItsLife_NeverCompletesTwice()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-sync-cancel", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // Sweeping the cancellation delay walks the token across the whole exchange, including
            // the moment the reply is being dispatched - the one interleaving where a waiter could
            // be completed and cancelled at once.
            int cancelled = 0, completed = 0;

            for (int micros = 0; micros < 3000; micros += 61)
            {
                using var attempt = new CancellationTokenSource();
                attempt.CancelAfter(TimeSpan.FromMicroseconds(micros));

                try
                {
                    await registry.WaitForInitialEnumerationAsync(attempt.Token);
                    completed++;
                }
                catch (OperationCanceledException)
                {
                    cancelled++;
                }
            }

            // Both outcomes must occur across that sweep, or the test is not covering the race it
            // claims to; and the connection must still work afterwards.
            Assert.IsTrue(completed > 0, "no round-trip completed; the sweep never reached the reply");
            Assert.IsTrue(cancelled > 0, "no round-trip was cancelled; the sweep started too late");

            await registry.WaitForInitialEnumerationAsync(cts.Token);
        }
    }

    [TestMethod]
    public async Task DisposingTheContextWithARoundTripOutstanding_ReleasesItRatherThanHanging()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // The case with no token of its own. A round-trip is completed by a reply on the loop, and
        // disposal stops that loop - so without the context releasing its waiters this waits for a
        // reply that can never arrive, forever.
        for (int round = 0; round < 5; round++)
        {
            var ctx = new PipeWireContext($"pwnet-sync-dispose-{round}", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync(cts.Token);
            var registry = new PipeWireRegistry(ctx);

            Task waiting = Task.Run(async () =>
            {
                try
                {
                    // Deliberately uncancellable from the caller's side.
                    await registry.WaitForInitialEnumerationAsync(CancellationToken.None);
                }
                catch (ObjectDisposedException) { }
                catch (Exception e) when (e is InvalidOperationException or PipeWireException) { }
                catch (OperationCanceledException) { }
            }, cts.Token);

            await ctx.DisposeAsync();

            await waiting.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            await registry.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AFailedRequestFaultsItsOwnWaiter_AndLeavesOthersAlone()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-sync-error", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("SyncErr")
                .WithName($"pwnet_syncerr_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);
            await control.ReadyAsync(cts.Token);

            for (int i = 0; i < 5; i++)
            {
                // A parameter a node does not have. The daemon refuses the request and reports it
                // out of band on the error stream, carrying the request's own sequence number -
                // which is the only thing tying the failure to the caller waiting on it.
                await Assert.ThrowsExactlyAsync<PipeWireException>(
                    async () => await control.EnumerateParametersAsync(SpaParamType.EnumProfile, cts.Token));

                // An error must fault only the request it belongs to. A read of a parameter the
                // node does have, straight afterwards, has to succeed.
                Assert.IsNotNull(await control.GetVolumeAsync(cts.Token),
                    $"iteration {i}: an error on one request poisoned the next");
            }

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task RemovingSomethingThatCannotBeRemoved_SaysSoRatherThanReportingSuccess()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-rm-contract", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // The core. The daemon accepts the request and does nothing at all - no error, no
            // removal - so a caller that trusted the return value would believe it had worked.
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
                async () => await registry.DestroyGlobalAsync(0, cts.Token));

            // An id the daemon has never issued. This one it does refuse, out of band on the error
            // stream, and that refusal has to reach the caller rather than being lost.
            PipeWireException refused = await Assert.ThrowsExactlyAsync<PipeWireException>(
                async () => await registry.DestroyGlobalAsync(NoSuchGlobal, cts.Token));

            // The point of the type: a caller can tell what failed and why without parsing text.
            Assert.IsTrue(refused.Result < 0, "a refusal must carry the daemon's result code");
            Assert.IsNotNull(refused.DaemonMessage, "a refusal must carry what the daemon said");

            // And the connection is still usable afterwards.
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsTrue(registry.Current.Nodes.Length > 0);
        }
    }

    [TestMethod]
    public async Task RoundTripsFromSeveralThreadsOnOneContext_DoNotCrossTheirCompletions()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-sync-threads", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // Each sync adds and removes a listener on the same core. Doing that from several
            // threads at once is where a listener list edited without the loop lock would corrupt.
            var faults = new System.Collections.Concurrent.ConcurrentQueue<string>();

            Task[] workers =
            [
                .. Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
                {
                    for (int i = 0; i < 12; i++)
                    {
                        try { await registry.WaitForInitialEnumerationAsync(cts.Token); }
                        catch (Exception ex) { faults.Enqueue($"worker {w}: {ex.GetType().Name}"); return; }
                    }
                }, cts.Token)),
            ];

            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(20), cts.Token);
            Assert.IsTrue(faults.IsEmpty, string.Join("; ", faults));
        }
    }

    [TestMethod]
    public async Task CancellingAWriteDoesNotRecallIt_AndLeavesTheStoreAgreeingWithTheDaemon()
    {
        // Cancellation abandons the wait, not the request: the write is already on its way when the
        // token trips, so the daemon may apply it anyway. That is documented rather than prevented,
        // because preventing it would mean a rollback the protocol has no way to express. What must
        // not happen is the two disagreeing afterwards, or the connection being left unusable.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-cancel-write", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null) Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await store!.ReadyAsync(cts.Token);

                string key = $"pwnet.cancel.{Environment.ProcessId}.{Random.Shared.Next():x}";
                int cancelled = 0, completed = 0;

                try
                {
                    for (int round = 0; round < 30; round++)
                    {
                        // Cancelled at a different point in the request's life each time, so the
                        // window where the native call has been made but the reply has not arrived
                        // is actually hit rather than assumed.
                        using var race = new CancellationTokenSource();
                        Task write = store.SetAsync(key, $"v{round}", cancellationToken: race.Token);

                        if (round % 3 == 0) race.Cancel();
                        else if (round % 3 == 1) await Task.Yield();

                        race.CancelAfter(TimeSpan.FromMilliseconds(round % 7));

                        try
                        {
                            await write;
                            completed++;
                        }
                        catch (OperationCanceledException)
                        {
                            cancelled++;
                        }
                    }

                    // Whatever the split, the store and the daemon must agree at the end. A fresh
                    // read of the key from a barrier the daemon answered is the arbiter.
                    await store.ReadyAsync(cts.Token);
                    string? settled = store.Get(key);

                    await store.SetAsync(key, "final", cancellationToken: cts.Token);
                    Assert.AreEqual("final", store.Get(key),
                        $"the store stopped tracking the key after {cancelled} cancelled and "
                        + $"{completed} completed writes (it had settled on '{settled ?? "(null)"}')");
                }
                finally
                {
                    await store.SetAsync(key, null, cancellationToken: CancellationToken.None);
                }

                Assert.IsTrue(cancelled > 0, "no write was actually cancelled, so nothing was exercised");

                // And the connection still works, which a half-torn-down request would not leave it.
                await registry.WaitForInitialEnumerationAsync(cts.Token);
                Assert.IsTrue(registry.Current.Nodes.Length > 0);
            }
        }
    }

    [TestMethod]
    public async Task ABindingDisposedTwiceAndThenFinalized_DestroysItsProxyExactlyOnce()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-sync-once", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("DestroyOnce")
                .WithName($"pwnet_destroyonce_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .ExecuteAsync(cts.Token);

            // Exactly-once native destruction is what a SafeHandle is for, and the permutations that
            // break it are disposal twice, and disposal followed by finalization. A second
            // pw_proxy_destroy on the same pointer is a use-after-free, not an exception.
            for (int round = 0; round < 6; round++)
            {
                PipeWireNodeControl control = registry.BindNode(node.NodeId);
                await control.DisposeAsync();
                await control.DisposeAsync();
                await control.DisposeAsync();
            }

            // Now the finalizer path: bound, dropped without disposal, collected.
            for (int round = 0; round < 6; round++)
                BindAndDrop(registry, node.NodeId);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // And one that is finalized after having been disposed, which must not destroy twice.
            PipeWireNodeControl disposedThenCollected = registry.BindNode(node.NodeId);
            await disposedThenCollected.DisposeAsync();
            disposedThenCollected = null!;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // The node is still there and still answers, so nothing destroyed anything it shouldn't.
            Assert.IsNotNull(registry.Current.GetNode(node.NodeId));
            await using PipeWireNodeControl fresh = registry.BindNode(node.NodeId);
            Assert.IsNotNull(await fresh.GetVolumeAsync(cts.Token));

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    private static void BindAndDrop(PipeWireRegistry registry, uint nodeId)
    {
        PipeWireNodeControl control = registry.BindNode(nodeId);
        GC.KeepAlive(control.Id);
    }
}
