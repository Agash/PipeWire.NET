using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Deliberate attempts to break the control plane: things vanishing mid-operation, disposal racing
/// callbacks, cancellation at every point, and inputs no daemon would send.
/// </summary>
/// <remarks>
/// Every test here asserts the same underlying property - the library fails cleanly rather than
/// hanging, corrupting, or aborting the process.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class HostileControlTests
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

    private static string UniqueName(string prefix) =>
        $"{prefix}_{Environment.ProcessId}_{Random.Shared.Next():x}";

    [TestMethod]
    public async Task DestroyingANodeWhileItsParametersAreBeingRead_FailsCleanly()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-vanish", cts.Token);
        await using (ctx)
        await using (registry)
        {
            for (int round = 0; round < 6; round++)
            {
                PipeWireNode node = await registry.CreateVirtualNode("Vanish")
                    .WithName(UniqueName("pwnet_vanish")).ExecuteAsync(cts.Token);

                await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

                // Read and destroy at the same time. Whichever wins, the read must end - with a
                // value, with an empty answer, or with an exception - and never hang.
                Task<ImmutableArray<SpaObject>> reading =
                    control.EnumerateParametersAsync(SpaParamType.Props, cts.Token);
                Task destroying = registry.DestroyGlobalAsync(node.NodeId, cts.Token);

                try
                {
                    await Task.WhenAll(reading, destroying).WaitAsync(TimeSpan.FromSeconds(8), cts.Token);
                }
                catch (ObjectDisposedException) { }
                catch (Exception e) when (e is InvalidOperationException or PipeWireException) { }
            }
        }
    }

    [TestMethod]
    public async Task DisposingAControlWhileItIsBeingUsedFromAnotherThread_DoesNotCrash()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-dispose", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("DisposeRace")
                .WithName(UniqueName("pwnet_disposerace")).ExecuteAsync(cts.Token);

            for (int round = 0; round < 10; round++)
            {
                PipeWireNodeControl control = registry.BindNode(node.NodeId);

                // Every reader must end one way or another.
                Task[] readers =
                [
                    .. Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
                    {
                        try { await control.GetVolumeAsync(cts.Token); }
                        catch (ObjectDisposedException) { }
                        catch (Exception e) when (e is InvalidOperationException or PipeWireException) { }
                        catch (OperationCanceledException) { }
                    }, cts.Token)),
                ];

                await control.DisposeAsync();
                await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

                // Disposing twice must be a no-op, not a second release of the same native object.
                await control.DisposeAsync();
            }

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task CancellingEveryParameterReadAtAnArbitraryPoint_LeavesNothingBehind()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-cancel", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("CancelRace")
                .WithName(UniqueName("pwnet_cancelrace")).ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

            // Cancel after a delay that sweeps across the whole exchange, so the token fires before
            // the request, during the wait, and after the answers have arrived.
            for (int micros = 0; micros < 2000; micros += 137)
            {
                using var attempt = new CancellationTokenSource();
                attempt.CancelAfter(TimeSpan.FromMicroseconds(micros));

                try { await control.EnumerateParametersAsync(SpaParamType.Props, attempt.Token); }
                catch (OperationCanceledException) { }
            }

            // Whatever happened above, the control still works: no waiter left in the map, no lock
            // left held, no state corrupted by an abandoned request.
            Assert.IsNotNull(await control.GetVolumeAsync(cts.Token),
                "the control must still answer after every read before it was cancelled");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ManyOverlappingReadsOfDifferentParameters_DoNotCrossTheirAnswers()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-overlap", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("Overlap")
                .WithName(UniqueName("pwnet_overlap")).ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);
            await control.ReadyAsync(cts.Token);

            // The correlation key is the daemon's own sequence number. If two requests in flight
            // shared a key, or one collected the other's answers, this is where it shows.
            for (int round = 0; round < 5; round++)
            {
                Task<ImmutableArray<SpaObject>> props =
                    control.EnumerateParametersAsync(SpaParamType.Props, cts.Token);
                Task<ImmutableArray<SpaObject>> propInfo =
                    control.EnumerateParametersAsync(SpaParamType.PropInfo, cts.Token);
                Task<ImmutableArray<SpaObject>> formats =
                    control.EnumerateParametersAsync(SpaParamType.EnumFormat, cts.Token);

                await Task.WhenAll(props, propInfo, formats);

                foreach (SpaObject o in await props)
                    Assert.AreEqual(SpaParamType.Props, o.ObjectId, "a Props read collected something else");
                foreach (SpaObject o in await propInfo)
                    Assert.AreEqual(SpaParamType.PropInfo, o.ObjectId, "a PropInfo read collected something else");
                foreach (SpaObject o in await formats)
                    Assert.AreEqual(SpaParamType.EnumFormat, o.ObjectId, "a format read collected something else");

                Assert.IsTrue((await props).Length > 0);
                Assert.IsTrue((await propInfo).Length > 0);
            }

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task WritingAParameterThatIsNonsenseForTheObject_IsIgnoredRatherThanFatal()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-write", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("Nonsense")
                .WithName(UniqueName("pwnet_nonsense")).ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

            // A Props object carrying a key from a completely different object type, a value of the
            // wrong type for the key it claims, and a key no enum defines. The daemon is entitled to
            // drop any of these; what it must not do is take the connection down with it.
            SpaObject[] nonsense =
            [
                new(SpaType.ObjectProps, SpaParamType.Props,
                    [new SpaProperty((uint)SpaParamRoute.Index, 0, new SpaInt(9999))]),
                new(SpaType.ObjectProps, SpaParamType.Props,
                    [new SpaProperty((uint)SpaProp.Volume, 0, new SpaString("not a float"))]),
                new(SpaType.ObjectProps, SpaParamType.Props,
                    [new SpaProperty(0xDEAD_BEEF, 0, new SpaBool(true))]),
                new(SpaType.ObjectProps, SpaParamType.Props, []),
            ];

            foreach (SpaObject value in nonsense)
            {
                try { await control.SetParameterAsync(SpaParamType.Props, value, cts.Token); }
                catch (PipeWireException) { }
            }

            Assert.IsNotNull(await control.GetVolumeAsync(cts.Token));
            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task AVolumeAtTheEdgesOfWhatAFloatCanHold_IsRefusedOrClampedButNeverCorrupting()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-volume", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("EdgeVolume")
                .WithName(UniqueName("pwnet_edgevol")).ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

            // The API refuses what it can prove is wrong before it reaches the wire.
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
                async () => await control.SetVolumeAsync(-1f, cts.Token));
            Assert.ThrowsExactly<ArgumentException>(
                () => control.SetChannelVolumesAsync([], cts.Token));
            Assert.ThrowsExactly<ArgumentException>(
                () => control.SetChannelVolumesAsync([0.5f, -0.5f], cts.Token));
            Assert.ThrowsExactly<ArgumentException>(
                () => control.SetChannelVolumesAsync([float.NaN], cts.Token));

            // And what it cannot prove is wrong, it sends: the daemon decides. Infinity is a real
            // float, so it goes, and whatever comes back must still be readable.
            foreach (float extreme in (float[])[0f, float.Epsilon, 1e30f, float.PositiveInfinity])
            {
                await control.SetVolumeAsync(extreme, cts.Token);
                float? read = await control.GetVolumeAsync(cts.Token);
                Assert.IsNotNull(read, $"the node stopped reporting a volume after being sent {extreme}");
            }

            // Read back on the same connection, so the daemon orders it against the write. What it
            // does not exclude is the session manager writing its own value in between: it manages
            // this node and does override volumes on nodes it manages, which is why this reads back
            // rather than asserting the daemon kept the value indefinitely.
            await control.SetVolumeAsync(0.5f, cts.Token);
            Assert.AreEqual(0.5f, (await control.GetVolumeAsync(cts.Token))!.Value, 0.001f);

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task AChannelVolumeCountThatDoesNotMatchTheNode_DoesNotCorruptTheOnesThatDo()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-chan", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("ChannelCount")
                .WithName(UniqueName("pwnet_chancount")).ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

            await control.SetChannelVolumesAsync([0.3f, 0.7f], cts.Token);
            ImmutableArray<float> before = await control.GetChannelVolumesAsync(cts.Token);
            Assert.AreEqual(2, before.Length);

            ImmutableArray<SpaAudioChannel> map = await control.GetChannelMapAsync(cts.Token);
            Assert.AreEqual(2, map.Length, "a stereo node has two channels");

            // Eight volumes for a two-channel node. PipeWire stores the array verbatim rather than
            // refusing or truncating it, so the node reports eight from now on - the trap this pins,
            // because nothing errors and a mixer would quietly be driving channels that do not exist.
            await control.SetChannelVolumesAsync([.. Enumerable.Repeat(0.1f, 8)], cts.Token);
            Assert.AreEqual(8, (await control.GetChannelVolumesAsync(cts.Token)).Length,
                "PipeWire stores a mismatched volume array as given");

            // The channel map does not follow it, which is what makes the map the authority.
            Assert.AreEqual(2, (await control.GetChannelMapAsync(cts.Token)).Length,
                "the channel map must still describe the node, not the last bad write");

            await control.SetChannelVolumesAsync([0.3f, 0.7f], cts.Token);
            ImmutableArray<float> after = await control.GetChannelVolumesAsync(cts.Token);
            Assert.AreEqual(2, after.Length);
            Assert.AreEqual(0.3f, after[0], 0.01f);

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task BindingEveryObjectInTheGraphAtOnce_AndDroppingThemAllOutOfOrder()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-bindall", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireGraphSnapshot graph = registry.Current;
            var bound = new List<IAsyncDisposable>();

            // Each is a native proxy and a listener, so this is the shape that would expose a leak
            // or a shared-state mistake between bindings.
            foreach (PipeWireNode node in graph.Nodes)
            {
                try { bound.Add(registry.BindNode(node.NodeId)); }
                catch (PipeWireException) { }
            }

            foreach (PipeWireDevice device in graph.Devices)
            {
                try { bound.Add(registry.BindDevice(device.Id)); }
                catch (PipeWireException) { }
            }

            foreach (PipeWireClient client in graph.Clients)
            {
                try { bound.Add(registry.BindClient(client.Id)); }
                catch (PipeWireException) { }
            }

            Assert.IsTrue(bound.Count > 0, "a live session must have something to bind");

            // Disposed in a shuffled order, because the ownership chain must not depend on the order
            // bindings were taken in.
            foreach (IAsyncDisposable binding in bound.OrderBy(_ => Random.Shared.Next()))
                await binding.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SubscribingThenDestroyingTheNodeUnderneath_DoesNotFireIntoFreedMemory()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-sub", cts.Token);
        await using (ctx)
        await using (registry)
        {
            for (int round = 0; round < 5; round++)
            {
                PipeWireNode node = await registry.CreateVirtualNode("SubDestroy")
                    .WithName(UniqueName("pwnet_subdestroy")).ExecuteAsync(cts.Token);

                PipeWireNodeControl control = registry.BindNode(node.NodeId);
                control.ParameterChanged += (_, _) => { };
                control.SubscribeParameters(SpaParamType.Props, SpaParamType.Format, SpaParamType.Latency);

                // Destroy the object the subscription points at, then dispose the subscriber. The
                // daemon may still be dispatching for it; the listener has to be detached before its
                // memory goes back, which is the whole point of the disposal order.
                await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
                await control.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task AHandlerThatThrowsOnEveryEvent_DoesNotStopTheOthersOrTheGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-throw", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("ThrowingSub")
                .WithName(UniqueName("pwnet_throwsub")).ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);

            int survivors = 0;
            control.ParameterChanged += (_, _) => throw new InvalidOperationException("first");
            control.ParameterChanged += (_, _) => Interlocked.Increment(ref survivors);
            control.ParameterChanged += (_, _) => throw new InvalidOperationException("third");

            control.SubscribeParameters(SpaParamType.Props);

            for (int i = 0; i < 4; i++)
                await control.SetVolumeAsync(0.1f * (i + 1), cts.Token);

            // The subscriber between two throwing ones still ran, which is the property that a bare
            // multicast Invoke would not have.
            Assert.IsTrue(Volatile.Read(ref survivors) > 0,
                "a handler after a throwing one was never reached");

            Assert.IsNotNull(await control.GetVolumeAsync(cts.Token));
            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task APropertyDictionaryBigEnoughToLeaveTheStack_IsSentIntact()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-props", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireClient? me = registry.Current.Clients
                .FirstOrDefault(c => c.ProcessId == Environment.ProcessId);

            if (me is null)
                Assert.Inconclusive("this connection is not visible as a client object.");

            await using PipeWireClientControl control = registry.BindClient(me!.Id);

            // Past both stackalloc thresholds - more than 32 items and more than 1024 bytes - so the
            // dictionary is built in pinned heap memory instead. If that pinning were wrong, the GC
            // could move the buffers the native call is reading from.
            var big = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < 60; i++)
                big[$"pwnet.test.key.{i}"] = new string('v', 64);

            await control.UpdatePropertiesAsync(big, cts.Token);

            // Force a collection while the daemon may still be reading, then prove the connection
            // survived by doing something else with it.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            await control.UpdatePropertiesAsync(
                new Dictionary<string, string> { ["application.name"] = "pwnet-after-big" }, cts.Token);
        }
    }

    [TestMethod]
    public async Task ConcurrentWritersToTheSameMetadataKey_AllCompleteAndTheStoreStaysConsistent()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-meta", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await store.ReadyAsync(cts.Token);

                string key = $"pwnet.test.concurrent.{Environment.ProcessId}";

                try { await store.SetAsync(key, "seed", cancellationToken: cts.Token); }
                catch (PipeWireException) { Assert.Inconclusive("cannot write metadata here."); }

                // Ten writers to one key. Each waits for an echo, and the echoes are not
                // distinguishable per writer - so the property being tested is that none of them
                // hangs or is left waiting on a completion source nobody will set.
                Task[] writers =
                [
                    .. Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
                    {
                        try { await store.SetAsync(key, $"value-{i}", cancellationToken: cts.Token); }
                        catch (InvalidOperationException) { }
                    }, cts.Token)),
                ];

                await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(15), cts.Token);

                Assert.IsNotNull(store.Get(key), "the key must hold one of the written values");
                await store.SetAsync(key, null, cancellationToken: cts.Token);
                Assert.IsNull(store.Get(key));
            }
        }
    }

    [TestMethod]
    public async Task DisposingTheStoreWhileAWriteIsWaitingForItsEcho_ReleasesTheWriter()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-metadisp", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null)
                Assert.Inconclusive("no session manager, so no default store.");

            string key = $"pwnet.test.dispose.{Environment.ProcessId}";

            // The write waits for the store to report the change back. Disposing means that report
            // will never come, so the waiter has to be released rather than left for the token.
            Task writing = Task.Run(async () =>
            {
                try { await store!.SetAsync(key, "value", cancellationToken: cts.Token); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                catch (OperationCanceledException) { }
            }, cts.Token);

            await store!.DisposeAsync();
            await writing.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        }
    }

    [TestMethod]
    public async Task ABurstOfWritesToOneKey_NeverReadsBackAnOlderValue()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-hostile-burst", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await store.ReadyAsync(cts.Token);
                string key = $"pwnet.test.burst.{Environment.ProcessId}";

                try { await store.SetAsync(key, "seed", cancellationToken: cts.Token); }
                catch (InvalidOperationException) { Assert.Inconclusive("cannot write metadata here."); }

                // The store echoes every change back, and those echoes lag the sync that reports a
                // write as processed. Under a burst, the echo of an older value lands after a newer
                // one has been written, and putting it back would make a read return the value
                // before last.
                var regressions = new List<string>();
                for (int i = 0; i < 400; i++)
                {
                    string expected = $"value-{i}";
                    await store.SetAsync(key, expected, cancellationToken: cts.Token);

                    string? actual = store.Get(key);
                    if (!string.Equals(expected, actual, StringComparison.Ordinal))
                        regressions.Add($"wrote {expected}, read {actual ?? "null"}");
                }

                Assert.IsTrue(regressions.Count == 0,
                    $"{regressions.Count} reads returned a superseded value, e.g. {regressions.FirstOrDefault()}");

                await store.SetAsync(key, null, cancellationToken: cts.Token);
                Assert.IsNull(store.Get(key));
            }
        }
    }

    [TestMethod]
    public async Task AnotherClientChangingAKeyWeJustWrote_IsStillReported()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // Two independent connections: suppressing our own superseded echoes must not suppress
        // somebody else's change to the same key, which is invisible with one connection.
        (PipeWireContext ctxA, PipeWireRegistry regA) = await ConnectAsync("pwnet-ext-a", cts.Token);
        (PipeWireContext ctxB, PipeWireRegistry regB) = await ConnectAsync("pwnet-ext-b", cts.Token);

        await using (ctxA)
        await using (regA)
        await using (ctxB)
        await using (regB)
        {
            PipeWireMetadataStore? mine = regA.BindMetadataStore("default");
            PipeWireMetadataStore? theirs = regB.BindMetadataStore("default");
            if (mine is null || theirs is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (mine)
            await using (theirs)
            {
                await mine!.ReadyAsync(cts.Token);
                await theirs!.ReadyAsync(cts.Token);

                string key = $"pwnet.test.external.{Environment.ProcessId}";

                var sawTheirs = new TaskCompletionSource<string?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                mine.EntryChanged += (_, entry) =>
                {
                    if (entry.Key == key && entry.Value == "from-b")
                        sawTheirs.TrySetResult(entry.Value);
                };

                try { await mine.SetAsync(key, "from-a", cancellationToken: cts.Token); }
                catch (InvalidOperationException) { Assert.Inconclusive("cannot write metadata here."); }

                Assert.AreEqual("from-a", mine.Get(key));

                // The other client overwrites it. A store that suppressed every echo not matching
                // its own outstanding write would never see this, and would report "from-a" forever.
                await theirs.SetAsync(key, "from-b", cancellationToken: cts.Token);

                string? reported = null;
                try
                {
                    reported = await sawTheirs.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
                }
                catch (TimeoutException)
                {
                    // Which half failed matters. If the cache holds the new value the echo arrived
                    // and the event was suppressed, which is a reconciler bug. If it does not, the
                    // session manager never relayed it, and nothing about this library follows.
                    if (mine.Get(key) == "from-b")
                    {
                        Assert.Fail(
                            "the cache took the other client's write but raised no event, "
                            + "so the echo was suppressed");
                    }

                    Assert.Inconclusive(
                        $"the session manager did not relay the other client's write within 10s. "
                        + $"cache holds '{mine.Get(key) ?? "(null)"}', "
                        + $"peer holds '{theirs.Get(key) ?? "(null)"}'");
                }

                Assert.AreEqual("from-b", reported, "the other client's change must be reported");
                Assert.AreEqual("from-b", mine.Get(key), "and must be reflected in the store");

                await mine.SetAsync(key, null, cancellationToken: cts.Token);
            }
        }
    }

    [TestMethod]
    public async Task StartingAContextFromSeveralThreadsAtOnce_StartsItExactlyOnce()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // Two threads both seeing an unstarted context would both call pw_thread_loop_start; the
        // second fails because the loop is already running, and its error path then stops the loop
        // the first one is connecting on - leaving a context that looks started and is not.
        for (int round = 0; round < 8; round++)
        {
            await using var ctx = new PipeWireContext($"pwnet-startrace-{round}", ConsoleTestLoggerFactory.Instance);

            Task[] starters =
            [
                .. Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
                {
                    try { await ctx.StartAsync(cts.Token); }
                    catch (ObjectDisposedException) { }
                }, cts.Token)),
            ];

            await Task.WhenAll(starters).WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

            // The proof it really is running: the registry can enumerate through it.
            await using var registry = new PipeWireRegistry(ctx);
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            Assert.IsTrue(registry.Current.Nodes.Length >= 0);
        }
    }
}
