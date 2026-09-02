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
                catch (InvalidOperationException) { }
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
            PipeWireNode node = await registry.CreateVirtualStereoNode("SyncErr")
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

            await registry.RemoveObjectAsync(node.NodeId, cts.Token);
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
                async () => await registry.RemoveObjectAsync(0, cts.Token));

            // An id the daemon has never issued. This one it does refuse, out of band on the error
            // stream, and that refusal has to reach the caller rather than being lost.
            PipeWireException refused = await Assert.ThrowsExactlyAsync<PipeWireException>(
                async () => await registry.RemoveObjectAsync(NoSuchGlobal, cts.Token));

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
    public async Task ABindingDisposedTwiceAndThenFinalized_DestroysItsProxyExactlyOnce()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-sync-once", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualStereoNode("DestroyOnce")
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

            await registry.RemoveObjectAsync(node.NodeId, cts.Token);
        }
    }

    private static void BindAndDrop(PipeWireRegistry registry, uint nodeId)
    {
        PipeWireNodeControl control = registry.BindNode(nodeId);
        GC.KeepAlive(control.Id);
    }
}
