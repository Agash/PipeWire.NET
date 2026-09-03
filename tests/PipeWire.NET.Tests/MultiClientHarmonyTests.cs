using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Several clients on one session at once: more than one instance of this library, external
/// producers, and the reference command-line tools, all acting on the same graph.
/// </summary>
/// <remarks>
/// Every other suite drives the daemon from a single context, which hides a whole class of defect:
/// a cache that only converges because nothing else is writing, an event that is only correct
/// because it was the local client that caused it, a view that agrees with itself rather than with
/// the session. These deliberately introduce a second opinion - another context in this process, a
/// real producer, a tool the library did not write - and require the views to agree afterwards.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class MultiClientHarmonyTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private sealed class Client : IAsyncDisposable
    {
        public required PipeWireContext Context { get; init; }
        public required PipeWireRegistry Registry { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await Context.DisposeAsync();
        }
    }

    private static async Task<Client> ConnectAsync(string name, CancellationToken cancellationToken)
    {
        var context = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cancellationToken);
        var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cancellationToken);
        return new Client { Context = context, Registry = registry };
    }

    private static string Unique(string prefix) =>
        $"{prefix}_{Environment.ProcessId}_{Random.Shared.Next():x}";

    /// <summary>Polls a condition that depends on another client's change reaching this one.</summary>
    private static async Task<bool> EventuallyAsync(
        Func<Task<bool>> condition, TimeSpan within, CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + (long)within.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(50, cancellationToken);
        }

        return await condition();
    }

    [TestMethod]
    public async Task EightContextsInOneProcess_AllConvergeOnTheSameGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // One process holding eight connections is not exotic - a host application, its plugins and
        // its UI can each end up with their own. They share a loader and a process heap but nothing
        // else, and each is meant to be an independent client of the daemon.
        Client[] clients =
        [
            .. await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(i => ConnectAsync($"pwnet-harmony-{i}", cts.Token))),
        ];

        try
        {
            // A node created through one of them has to become visible to all of them, by id.
            PipeWireNode node = await clients[0].Registry.CreateVirtualNode("Harmony")
                .WithName(Unique("pwnet_harmony"))
                .ExecuteAsync(cts.Token);

            foreach (Client c in clients)
            {
                bool saw = await EventuallyAsync(async () =>
                {
                    await c.Registry.WaitForInitialEnumerationAsync(cts.Token);
                    return c.Registry.Current.GetNode(node.NodeId) is not null;
                }, TimeSpan.FromSeconds(10), cts.Token);

                Assert.IsTrue(saw, "a node created by one client was never seen by another");
            }

            // And every one of them must be able to bind and drive it at the same time: eight
            // proxies to one global, eight independent parameter caches.
            PipeWireNodeControl[] controls = [.. clients.Select(c => c.Registry.BindNode(node.NodeId))];
            try
            {
                await Task.WhenAll(controls.Select(c => c.ReadyAsync(cts.Token)));

                for (int i = 0; i < controls.Length; i++)
                    await controls[i].SetVolumeAsync(0.1f + (i * 0.1f), cts.Token);

                // The last write wins and everyone must agree on which that was, rather than each
                // believing whatever it wrote itself.
                bool agreed = await EventuallyAsync(async () =>
                {
                    float?[] seen = await Task.WhenAll(controls.Select(c => c.GetVolumeAsync(cts.Token)));
                    return Array.TrueForAll(seen, v => v is not null) && seen.Distinct().Count() == 1;
                }, TimeSpan.FromSeconds(15), cts.Token);

                Assert.IsTrue(agreed, "eight controls on one node did not converge on one volume");
            }
            finally
            {
                foreach (PipeWireNodeControl c in controls)
                    await c.DisposeAsync();
            }

            await clients[0].Registry.DestroyGlobalAsync(node.NodeId, cts.Token);

            // Removal has to propagate as reliably as creation; a client that keeps a destroyed node
            // in its snapshot hands out ids that fail on the next bind.
            foreach (Client c in clients)
            {
                bool gone = await EventuallyAsync(async () =>
                {
                    await c.Registry.WaitForInitialEnumerationAsync(cts.Token);
                    return c.Registry.Current.GetNode(node.NodeId) is null;
                }, TimeSpan.FromSeconds(10), cts.Token);

                Assert.IsTrue(gone, "a removed node stayed in another client's snapshot");
            }
        }
        finally
        {
            foreach (Client c in clients)
                await c.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TwoOfOurClientsAndPwCliFightingOverOneVolume_EndUpAgreeing()
    {
        RequireLinux();
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);

        await using Client a = await ConnectAsync("pwnet-fight-a", cts.Token);
        await using Client b = await ConnectAsync("pwnet-fight-b", cts.Token);

        PipeWireNode node = await a.Registry.CreateVirtualNode("Contended")
            .WithName(Unique("pwnet_contended"))
            .ExecuteAsync(cts.Token);

        await using PipeWireNodeControl ca = a.Registry.BindNode(node.NodeId);
        await using PipeWireNodeControl cb = b.Registry.BindNode(node.NodeId);
        await Task.WhenAll(ca.ReadyAsync(cts.Token), cb.ReadyAsync(cts.Token));

        // Three writers with no coordination between them. The library makes no ordering promise
        // here and cannot - what it must not do is end in a state where its two caches disagree
        // with each other or with the daemon.
        var faults = new ConcurrentQueue<string>();

        Task writerA = Task.Run(async () =>
        {
            for (int i = 0; i < 40; i++)
            {
                try { await ca.SetVolumeAsync(0.20f, cts.Token); }
                catch (Exception ex) { faults.Enqueue($"a: {ex.GetType().Name}: {ex.Message}"); return; }
            }
        }, cts.Token);

        Task writerB = Task.Run(async () =>
        {
            for (int i = 0; i < 40; i++)
            {
                try { await cb.SetVolumeAsync(0.60f, cts.Token); }
                catch (Exception ex) { faults.Enqueue($"b: {ex.GetType().Name}: {ex.Message}"); return; }
            }
        }, cts.Token);

        Task writerCli = Task.Run(async () =>
        {
            for (int i = 0; i < 12; i++)
                await PwTools.SetNodeVolumeAsync(node.NodeId, 0.90f, cts.Token);
        }, cts.Token);

        await Task.WhenAll(writerA, writerB, writerCli).WaitAsync(TimeSpan.FromSeconds(60), cts.Token);
        Assert.IsTrue(faults.IsEmpty, string.Join("; ", faults));

        // Now nobody is writing. One more write settles the argument, and both caches have to report
        // it - a cache that suppressed the other client's echoes would be stuck on its own last
        // write instead.
        await ca.SetVolumeAsync(0.33f, cts.Token);

        bool converged = await EventuallyAsync(async () =>
        {
            float? va = await ca.GetVolumeAsync(cts.Token);
            float? vb = await cb.GetVolumeAsync(cts.Token);
            return va is not null && vb is not null && Math.Abs(va.Value - vb.Value) < 0.001f;
        }, TimeSpan.FromSeconds(20), cts.Token);

        Assert.IsTrue(converged, "two clients did not agree on the volume after the writes stopped");

        await a.Registry.DestroyGlobalAsync(node.NodeId, cts.Token);
    }

    [TestMethod]
    public async Task MetadataWrittenByEveryKindOfClient_ReachesEveryOtherOne()
    {
        RequireLinux();
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);

        await using Client a = await ConnectAsync("pwnet-meta-a", cts.Token);
        await using Client b = await ConnectAsync("pwnet-meta-b", cts.Token);

        PipeWireMetadataStore? sa = a.Registry.BindMetadataStore("default");
        PipeWireMetadataStore? sb = b.Registry.BindMetadataStore("default");
        if (sa is null || sb is null)
            Assert.Inconclusive("no default metadata store on this session.");

        await using (sa)
        await using (sb)
        {
            await Task.WhenAll(sa.ReadyAsync(cts.Token), sb.ReadyAsync(cts.Token));

            string key = Unique("pwnet.harmony");
            var seenByB = new ConcurrentQueue<string?>();
            sb.EntryChanged += (_, e) =>
            {
                if (string.Equals(e.Key, key, StringComparison.Ordinal)) seenByB.Enqueue(e.Value);
            };

            // Our client writes; the other one must see it.
            await sa.SetAsync(key, "from-a", "Spa:String", PipeWireMetadataStore.SubjectCore, cts.Token);
            if (!await EventuallyAsync(() => Task.FromResult(sb.Get(key) == "from-a"),
                    TimeSpan.FromSeconds(10), cts.Token))
            {
                // The hop between the two clients is the session manager's, not this library's. A
                // write that never arrives says the relay stalled, and every assertion below it
                // depends on that relay working.
                Assert.Inconclusive(
                    "the session manager did not relay a write between two clients within 10s.");
            }

            // And the direction it breaks in the quiet way: a write from a client that is not this
            // library at all, to a key this library has just written. Suppressing our own echoes
            // must not suppress pw-metadata's.
            await PwTools.SetMetadataAsync(key, "from-pw-metadata", cts.Token);

            Assert.IsTrue(
                await EventuallyAsync(() => Task.FromResult(sa.Get(key) == "from-pw-metadata"),
                    TimeSpan.FromSeconds(10), cts.Token),
                "an external write was not applied by the client that had just written the key");
            Assert.IsTrue(
                await EventuallyAsync(() => Task.FromResult(sb.Get(key) == "from-pw-metadata"),
                    TimeSpan.FromSeconds(10), cts.Token),
                "an external write was not applied by the observing client");

            // The event has to have fired too, not just the cache updated: an application that only
            // listens would otherwise never learn of the change.
            Assert.IsTrue(seenByB.Contains("from-pw-metadata"),
                "the external write updated the cache but raised no event");

            // A burst from both of ours at once, then a single external write on top. Whatever the
            // interleaving, the external value is the last one written and must be where everyone
            // ends up - this is the case the in-flight window is bounded by age for. Bounding it by
            // count instead drops values whose echoes are still in flight, and those echoes then
            // read as somebody else's change and put an old value back.
            await Task.WhenAll(
                Task.Run(async () =>
                {
                    for (int i = 0; i < 30; i++)
                        await sa.SetAsync(key, $"a-{i}", "Spa:String", PipeWireMetadataStore.SubjectCore, cts.Token);
                }, cts.Token),
                Task.Run(async () =>
                {
                    for (int i = 0; i < 30; i++)
                        await sb.SetAsync(key, $"b-{i}", "Spa:String", PipeWireMetadataStore.SubjectCore, cts.Token);
                }, cts.Token));

            await PwTools.SetMetadataAsync(key, "final", cts.Token);

            Assert.IsTrue(
                await EventuallyAsync(
                    () => Task.FromResult(sa.Get(key) == "final" && sb.Get(key) == "final"),
                    TimeSpan.FromSeconds(25), cts.Token),
                $"after a burst the clients settled on a='{sa.Get(key)}' b='{sb.Get(key)}', not the last write");

            await sa.SetAsync(key, null, "Spa:String", PipeWireMetadataStore.SubjectCore, cts.Token);
        }
    }

    [TestMethod]
    [TestCategory("RequiresGStreamer")]
    public async Task ARealProducerAndOurFilterAndAnObserver_AllRunAtOnce()
    {
        RequireLinux();
        GstTestSource.RequireGStreamer();
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);

        // Every part is a separate client of the daemon.
        await using Client dsp = await ConnectAsync("pwnet-chain-dsp", cts.Token);
        await using Client observer = await ConnectAsync("pwnet-chain-observer", cts.Token);

        string sourceName = Unique("pwnet_chain_src");
        await using GstTestSource producer = await GstTestSource.StartAsync(
            dsp.Context, sourceName,
            "audiotestsrc is-live=true wave=sine ! audio/x-raw,format=F32LE,channels=2,rate=48000",
            "Audio/Source");

        long cycles = 0;
        string dspName = Unique("pwnet_chain_dsp");
        await using PipeWireFilter filter = PipeWireFilter.Create(dsp.Context, dspName);
        filter.ProcessCallback = (_, _) => Interlocked.Increment(ref cycles);

        filter.AddAudioPort(PipeWirePortDirection.In, "in-l");
        filter.AddAudioPort(PipeWirePortDirection.In, "in-r");
        filter.AddAudioPort(PipeWirePortDirection.Out, "out-l");
        filter.AddAudioPort(PipeWirePortDirection.Out, "out-r");

        await filter.ConnectAsync(cancellationToken: cts.Token);
        uint filterNodeId = await filter.WaitForNodeIdAsync(cts.Token);

        // The observer - a completely separate connection - must see both of them, without either
        // having told it anything.
        bool sawBoth = await EventuallyAsync(async () =>
        {
            await observer.Registry.WaitForInitialEnumerationAsync(cts.Token);
            PipeWireGraphSnapshot g = observer.Registry.Current;
            return g.GetNode(producer.NodeId) is not null && g.GetNode(filterNodeId) is not null;
        }, TimeSpan.FromSeconds(20), cts.Token);

        if (!sawBoth)
        {
            PipeWireGraphSnapshot g = observer.Registry.Current;
            Assert.Fail(
                $"an independent observer did not see both: producer id={producer.NodeId} "
                + $"seen={g.GetNode(producer.NodeId) is not null}, filter id={filterNodeId} "
                + $"seen={g.GetNode(filterNodeId) is not null}, observer knows {g.Nodes.Length} nodes");
        }

        // Wired with pw-link, so the links are ones this library did not create and must still
        // account for correctly.
        // Resolved from the observer's own snapshot rather than by parsing names out of pw-link:
        // a filter's ports are not listed under its node name there, and matching on names finds
        // the producer's two and none of the filter's. Doing it by node id also makes the wiring
        // step check that an uninvolved client's view of the graph is good enough to act on.
        ImmutableArray<PipeWirePort> srcPorts = [];
        ImmutableArray<PipeWirePort> dspPorts = [];

        bool wired = await EventuallyAsync(async () =>
        {
            await observer.Registry.WaitForInitialEnumerationAsync(cts.Token);
            PipeWireGraphSnapshot g = observer.Registry.Current;

            srcPorts =
            [
                .. g.GetPortsForNode(producer.NodeId)
                    .Where(p => p.PortDirection == PipeWirePortDirection.Out && !p.Monitor)
                    .OrderBy(p => p.PortName, StringComparer.Ordinal),
            ];
            dspPorts =
            [
                .. g.GetPortsForNode(filterNodeId)
                    .Where(p => p.PortDirection == PipeWirePortDirection.In)
                    .OrderBy(p => p.PortName, StringComparer.Ordinal),
            ];

            return srcPorts.Length >= 2 && dspPorts.Length >= 2;
        }, TimeSpan.FromSeconds(20), cts.Token);

        Assert.IsTrue(wired,
            $"producer id={producer.NodeId} published {srcPorts.Length} output port(s) and filter "
            + $"id={filterNodeId} {dspPorts.Length} input port(s); both need two");

        for (int i = 0; i < 2; i++)
        {
            await PwTools.LinkAsync(
                srcPorts[i].PortId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                dspPorts[i].PortId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cts.Token);
        }

        // Samples have to actually flow through the managed callback.
        Assert.IsTrue(
            await EventuallyAsync(() => Task.FromResult(Interlocked.Read(ref cycles) > 20),
                TimeSpan.FromSeconds(25), cts.Token),
            $"the filter ran {Interlocked.Read(ref cycles)} cycles with a real producer linked into it");

        // And the observer's view of the links has to match pw-link's exactly - a graph it played no
        // part in building.
        Assert.IsTrue(
            await EventuallyAsync(async () =>
            {
                await observer.Registry.WaitForInitialEnumerationAsync(cts.Token);
                List<(uint Link, uint Output, uint Input)> theirs = await PwTools.ListLinksAsync(cts.Token);
                ImmutableArray<PipeWireLink> ours = observer.Registry.Current.Links;

                return theirs.Select(l => l.Link).Order().SequenceEqual(ours.Select(l => l.LinkId).Order());
            }, TimeSpan.FromSeconds(20), cts.Token),
            "an observing client's link list did not match pw-link's");
    }

    [TestMethod]
    public async Task ClientsJoiningAndLeavingWhileTheGraphChurns_NeverMissTheFinalState()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // Connections opening and closing while the graph is being rebuilt underneath them. A client
        // that enumerates during a churn can legitimately see a torn-looking graph; what it must not
        // do is settle on one, so each is checked after the churn has stopped.
        await using Client churner = await ConnectAsync("pwnet-churn", cts.Token);

        var created = new ConcurrentBag<uint>();
        var faults = new ConcurrentQueue<string>();
        using var churning = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(churning.Token, cts.Token);

        Task churn = Task.Run(async () =>
        {
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    PipeWireNode n = await churner.Registry.CreateVirtualNode("Churn")
                        .WithName(Unique("pwnet_churn"))
                        .ExecuteAsync(linked.Token);

                    created.Add(n.NodeId);
                    try
                    {
                        await Task.Delay(30, linked.Token);
                    }
                    finally
                    {
                        // Uncancellable, and in a finally: cancellation lands between the create and
                        // the remove often enough that a cancellable removal strands a node, which
                        // the assertion after the churn then reads as the graph having gone stale.
                        await churner.Registry.DestroyGlobalAsync(n.NodeId, CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException) { /* the churn is stopped by cancellation. */ }
            catch (Exception ex) { faults.Enqueue($"churn: {ex.GetType().Name}: {ex.Message}"); }
        }, cts.Token);

        // Twenty short-lived clients arriving into the middle of that.
        for (int round = 0; round < 20; round++)
        {
            try
            {
                await using Client transient = await ConnectAsync($"pwnet-transient-{round}", cts.Token);
                Assert.IsTrue(transient.Registry.Current.Version > 0);
            }
            catch (Exception ex)
            {
                faults.Enqueue($"transient {round}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        await churning.CancelAsync();
        await churn;
        Assert.IsTrue(faults.IsEmpty, string.Join("; ", faults));

        // One last client, connecting after everything has settled. It must see none of the churned
        // nodes - each was removed, and a stale global would show up here as a node that is gone.
        await using Client after = await ConnectAsync("pwnet-after-churn", cts.Token);

        bool clean = await EventuallyAsync(async () =>
        {
            await after.Registry.WaitForInitialEnumerationAsync(cts.Token);
            return !created.Any(id => after.Registry.Current.GetNode(id) is not null);
        }, TimeSpan.FromSeconds(20), cts.Token);

        Assert.IsTrue(clean, "a client connecting after the churn still saw nodes that had been removed");
    }

    [TestMethod]
    public async Task OneClientDyingWithWorkInFlight_LeavesTheOthersHealthy()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using Client survivor = await ConnectAsync("pwnet-survivor", cts.Token);

        // A client disposed while it still owns objects and has a read outstanding, repeatedly. The
        // daemon cleans up after it; the surviving connection must not be disturbed by that, and
        // must see the cleanup rather than keeping the dead client's nodes forever.
        var abandoned = new List<uint>();

        for (int round = 0; round < 6; round++)
        {
            Client doomed = await ConnectAsync($"pwnet-doomed-{round}", cts.Token);

            PipeWireNode n = await doomed.Registry.CreateVirtualNode("Doomed")
                .WithName(Unique("pwnet_doomed"))
                .ExecuteAsync(cts.Token);

            abandoned.Add(n.NodeId);

            PipeWireNodeControl control = doomed.Registry.BindNode(n.NodeId);
            await control.ReadyAsync(cts.Token);

            // Disposed out from under an outstanding read, with the node never removed.
            Task<float?> reading = control.GetVolumeAsync(cts.Token);
            await doomed.DisposeAsync();

            try { await reading.WaitAsync(TimeSpan.FromSeconds(15), cts.Token); }
            catch (ObjectDisposedException) { /* the expected outcome. */ }
            catch (Exception e) when (e is InvalidOperationException or PipeWireException) { /* the connection went while the request was open. */ }
            catch (OperationCanceledException) { /* likewise. */ }

            await control.DisposeAsync();
        }

        // The survivor is still usable, and the daemon's cleanup of the dead clients' nodes has
        // reached it.
        Assert.IsTrue(
            await EventuallyAsync(async () =>
            {
                await survivor.Registry.WaitForInitialEnumerationAsync(cts.Token);
                return !abandoned.Any(id => survivor.Registry.Current.GetNode(id) is not null);
            }, TimeSpan.FromSeconds(25), cts.Token),
            "nodes owned by clients that went away stayed in the surviving client's graph");

        PipeWireNode fresh = await survivor.Registry.CreateVirtualNode("Survivor")
            .WithName(Unique("pwnet_survivor"))
            .ExecuteAsync(cts.Token);

        await using PipeWireNodeControl c = survivor.Registry.BindNode(fresh.NodeId);
        await c.ReadyAsync(cts.Token);
        Assert.IsNotNull(await c.GetVolumeAsync(cts.Token));

        await survivor.Registry.DestroyGlobalAsync(fresh.NodeId, cts.Token);
    }
}
