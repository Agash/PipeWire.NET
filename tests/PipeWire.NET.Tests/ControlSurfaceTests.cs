using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Spa;

namespace PipeWire.NET.Tests;

/// <summary>
/// Public entry points that nothing else exercises.
/// </summary>
/// <remarks>
/// Two of these are deliberately not driven all the way: <c>ClearAsync</c> empties a store the whole
/// session shares, and reducing a client's permissions can cut off the connection that would undo
/// it. Both are checked up to the point where the next step would change the machine.
/// </remarks>
// Serialised against the rest of the suite deliberately. Updating client permissions while other
// classes tear objects down races an upstream daemon bug: pw_impl_client_update_permissions ->
// pw_global_update_permissions -> pw_resource_destroy asserts `!resource->destroyed` and aborts the
// daemon, taking every other test with it. A client should not be able to do that, so this is a
// workaround for the daemon, not a fix for anything here.
[DoNotParallelize]
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class ControlSurfaceTests : PipeWireTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<(PipeWireContext Context, PipeWireRegistry Registry)> ConnectAsync(
        string name,
        CancellationToken cancellationToken
    )
    {
        var context = new PipeWireContext(name, ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cancellationToken);
        var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cancellationToken);
        return (context, registry);
    }

    [TestMethod]
    public async Task ANodesSupportedFormats_CanBeEnumerated()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync(
            "pwnet-formats",
            cts.Token
        );
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry
                .CreateVirtualSink("Formats")
                .WithName($"pwnet_formats_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .ExecuteAsync(cts.Token);

            await using PipeWireNodeProxy control = registry.BindNode(node.NodeId);
            await control.ReadyAsync(cts.Token);

            // An adapter reports the formats it can be configured for. An empty result is a valid
            // answer for a node that has none, so the contract is "does not fail", not "is not empty".
            ImmutableArray<SpaObject> formats = await control.EnumerateFormatsAsync(cts.Token);

            foreach (SpaObject format in formats)
                Assert.AreEqual(SpaType.ObjectFormat, format.ObjectType);

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task TheDefaultAudioSource_CanBeSetToWhatItAlreadyIs()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync(
            "pwnet-defsource",
            cts.Token
        );
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataProxy? store = registry.BindMetadata("default");
            if (store is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await store.ReadyAsync(cts.Token);

                string? current = store.DefaultAudioSource?.NameValue;
                if (current is null)
                    Assert.Inconclusive("this session has no default audio source.");

                // Writing back the value it already holds: the write path runs, the session does not
                // move. The daemon sends no echo for a no-op change, which is why this asserts the
                // cache rather than waiting for an event.
                await store.SetDefaultAudioSourceAsync(current!, cts.Token);

                Assert.AreEqual(
                    current,
                    store.DefaultAudioSource?.NameValue,
                    "rewriting the default source must leave it where it was"
                );
            }
        }
    }

    [TestMethod]
    public async Task AStoreAndAClientControl_RefuseWorkAfterDisposal()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync(
            "pwnet-disposed",
            cts.Token
        );
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataProxy? store = registry.BindMetadata("default");
            if (store is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await store!.DisposeAsync();

            // ClearAsync empties a store the whole session shares, so it is checked only to the
            // point of its guard - calling it for real would take the session's defaults with it.
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
                await store.ClearAsync(cts.Token)
            );
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
                await store.SetAsync("k", "v", cancellationToken: cts.Token)
            );

            PipeWireClient? self = registry.Current.Clients.FirstOrDefault();
            if (self is null)
                Assert.Inconclusive("the registry reported no clients.");

            PipeWireClientProxy client = registry.BindClient(self!.Id);
            await client.DisposeAsync();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
                await client.UpdatePropertiesAsync(
                    new Dictionary<string, string> { ["k"] = "v" },
                    cts.Token
                )
            );
        }
    }

    [TestMethod]
    public async Task UpdatingPermissionsWithNothingToApply_IsRejectedBeforeItReachesTheDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync(
            "pwnet-perms",
            cts.Token
        );
        await using (ctx)
        await using (registry)
        {
            PipeWireClient? self = registry.Current.Clients.FirstOrDefault();
            if (self is null)
                Assert.Inconclusive("the registry reported no clients.");

            await using PipeWireClientProxy client = registry.BindClient(self!.Id);

            // Reducing a real client's permissions can cut off the connection that would restore
            // them, so only the argument guard is driven here.
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await client.UpdatePermissionsAsync(
                    ReadOnlyMemory<PipeWireObjectPermission>.Empty,
                    cts.Token
                )
            );
        }
    }

    [TestMethod]
    public async Task TheContextLock_IsHandedOutWhileOpenAndRefusedOnceDisposed()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        var ctx = new PipeWireContext("pwnet-lock", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        Assert.IsTrue(ctx.TryLock(out PipeWireContext.LoopLock granted));
        granted.Dispose();

        using (ctx.Lock())
        {
            // Recursive: the loop's mutex is, and the library relies on it - a write issued inside a
            // round-trip takes the lock the round-trip is already holding.
            Assert.IsTrue(ctx.TryLock(out PipeWireContext.LoopLock nested));
            nested.Dispose();
        }

        await ctx.DisposeAsync();

        Assert.IsFalse(ctx.TryLock(out _), "a disposed context must not hand out its loop lock");
        Assert.ThrowsExactly<ObjectDisposedException>(() => ctx.Lock().Dispose());
    }

    [TestMethod]
    [TestCategory("RequiresPipeWire168")]
    public async Task ASubscribedVolumeChange_RaisesAnEventWhileConcurrentReadsAgree()
    {
        // Two things the ordinary read path never touches: the subscription set the daemon keeps
        // per binding, and several enumerations sharing one answers table keyed by sequence.
        // 1.0.5 emits the current volume as the first subscribed event where 1.6.8 emits only
        // the change, so this expectation only holds where the daemon behaves the newer way.
        RequireLinux();
        SessionGates.RequireDaemonAtLeast(1, 6, 8);
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync(
            "pwnet-subscribe",
            cts.Token
        );
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry
                .CreateVirtualSink("Subscribe")
                .WithName($"pwnet_sub_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .ExecuteAsync(cts.Token);

            await using PipeWireNodeProxy control = registry.BindNode(node.NodeId);
            await control.ReadyAsync(cts.Token);

            control.SubscribeParameters(SpaParamType.Props);
            CollectionAssert.AreEqual(
                new[] { SpaParamType.Props },
                control.SubscribedParameters.ToArray()
            );

            // Completed by the event that carries the value written below, not by whichever Props
            // object arrives first. The subscription and the eight enumerations below share one
            // event, so latching the first one can capture a read's answer - the volume before the
            // write - and report it as the change. Waiting for the written value still fails if no
            // event ever carries it, which is the property under test.
            var changed = new TaskCompletionSource<SpaObject>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            control.ParameterChanged += (_, value) =>
            {
                if (
                    value.ObjectType == SpaType.ObjectProps
                    && value[SpaProp.Volume] is SpaFloat v
                    && Math.Abs(v.Value - 0.5f) < 0.0001f
                )
                {
                    changed.TrySetResult(value);
                }
            };

            // No writer is active, so every concurrent read must file under its own key and all
            // must describe the same state.
            Task<ImmutableArray<SpaObject>>[] reads = Enumerable
                .Range(0, 8)
                .Select(_ => control.EnumerateParametersAsync(SpaParamType.Props, cts.Token))
                .ToArray();
            ImmutableArray<SpaObject>[] results = await Task.WhenAll(reads);
            foreach (ImmutableArray<SpaObject> result in results)
                CollectionAssert.AreEqual(results[0].ToArray(), result.ToArray());

            await control.SetVolumeAsync(0.5f, cts.Token);

            SpaObject update = await changed.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);
            Assert.AreEqual(0.5f, (update[SpaProp.Volume] as SpaFloat)?.Value);

            control.UnsubscribeParameters();
            Assert.AreEqual(0, control.SubscribedParameters.Length);

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task NodeParameterGuards_RefuseBadInputBeforeTheDaemon()
    {
        // Every argument guard on the node surface: none of these may reach the daemon, so all
        // of them are checked against a node that is otherwise fully usable.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync(
            "pwnet-nodeguards",
            cts.Token
        );
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry
                .CreateVirtualSink("Guards")
                .WithName($"pwnet_guards_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .ExecuteAsync(cts.Token);

            await using PipeWireNodeProxy control = registry.BindNode(node.NodeId);
            await control.ReadyAsync(cts.Token);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => control.SetVolumeAsync(-1f));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                control.SetVolumeAsync(float.NaN)
            );

            await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
                await control.SetPortConfigAsync(null!, cts.Token)
            );
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
                await control.SetProcessLatencyAsync(null!, cts.Token)
            );
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
                await control.SetTagAsync(null!, cts.Token)
            );

            // Both channel-volume overloads reach the daemon: unchecked writes verbatim, checked
            // writes against the map and the current volumes.
            await control.SetChannelVolumesAsync(
                new float[] { 0.5f, 0.5f },
                matchChannelMap: false,
                cts.Token
            );
            await control.SetChannelVolumesAsync(
                new float[] { 0.5f, 0.5f },
                matchChannelMap: true,
                cts.Token
            );

            ImmutableArray<float> volumes = await control.GetChannelVolumesAsync(cts.Token);
            CollectionAssert.AreEqual(new[] { 0.5f, 0.5f }, volumes.ToArray());

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task SyncDisposal_TearsDownWithoutAsync()
    {
        // Disposal here does no I/O, so the synchronous form must tear down exactly what the
        // asynchronous one does.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        var ctx = new PipeWireContext("pwnet-syncdispose", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireMetadataProxy? store = registry.BindMetadata("settings");
        store?.Dispose();

        registry.Dispose();
        await ctx.DisposeAsync();

        Assert.IsTrue(ctx.IsDisposed, "the context did not report itself disposed");
    }

    [TestMethod]
    public async Task DefaultEndpointGuards_RefuseAnEmptyNameBeforeTheDaemon()
    {
        // A default stored by an empty name would drift onto whatever the daemon picks, so the
        // empty case is refused here rather than written.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync(
            "pwnet-defguards",
            cts.Token
        );
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataProxy? store = registry.BindMetadata("default");
            if (store is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                    await store!.SetDefaultAudioSinkAsync("", cts.Token)
                );
                await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                    await store!.SetDefaultAudioSourceAsync("", cts.Token)
                );
            }
        }
    }

    /// <summary>
    /// Retagging the connection reaches the daemon and comes back on the client's own info event.
    /// </summary>
    /// <remarks>
    /// End to end on purpose. `pw_core_update_properties` forwards to
    /// `pw_impl_client_update_properties`, which updates the client global and sends a fresh
    /// `info` to every bound client resource (`impl-client.c`), and `application.name` is one of
    /// the keys the daemon promotes to the global. So binding this process's own client and
    /// watching its properties proves the whole path rather than just that the value was stored
    /// locally - which is all an idempotence check would prove.
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task RetaggingAConnection_ReachesTheDaemonAndComesBack()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        const string Before = "pwnet-retag";
        const string After = "pwnet retagged client";

        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync(Before, cts.Token);

        await using (ctx)
        await using (reg)
        {
            // This process's own client, found by the name the context connected under.
            PipeWireClient? own = null;
            for (var i = 0; i < 100 && own is null; i++)
            {
                own = reg.Current.Clients.FirstOrDefault(c =>
                    c.Properties.TryGetValue(PipeWireKeys.PW_KEY_APP_NAME, out string? v)
                    && v == Before
                );

                if (own is null)
                    await Task.Delay(50, cts.Token);
            }

            Assert.IsNotNull(
                own,
                $"no client in the graph is this process (application.name '{Before}')"
            );

            await using PipeWireClientProxy client = reg.BindClient(own!.Id);
            await client.ReadyAsync(cts.Token);

            Assert.AreEqual(
                Before,
                client.Properties.TryGetValue(PipeWireKeys.PW_KEY_APP_NAME, out string? initial)
                    ? initial
                    : null,
                "the bound client did not report the name it connected under"
            );

            Assert.IsTrue(
                ctx.UpdateProperties(
                    new Dictionary<string, string> { [PipeWireKeys.PW_KEY_APP_NAME] = After }
                ) > 0,
                "the new application.name changed nothing, so it was never stored"
            );

            for (var i = 0; i < 100; i++)
            {
                if (
                    client.Properties.TryGetValue(PipeWireKeys.PW_KEY_APP_NAME, out string? now)
                    && now == After
                )
                    return;

                await Task.Delay(50, cts.Token);
            }

            Assert.Fail(
                "the daemon never sent the retagged application.name back on the client's info event"
            );
        }
    }

    /// <summary>A client's permissions can be read back, not only written.</summary>
    /// <remarks>
    /// The daemon answers `get_permissions` on the client's `permissions` event, which this proxy
    /// did not subscribe to at all until this session - so permissions could be set and never
    /// verified. Every client holds at least the default entry (`AnyObject`), so a healthy read
    /// returns something.
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task AClientsPermissions_CanBeReadBack()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync(
            "pwnet-perm-read",
            cts.Token
        );

        await using (ctx)
        await using (reg)
        {
            PipeWireClient? own = null;
            for (var i = 0; i < 100 && own is null; i++)
            {
                own = reg.Current.Clients.FirstOrDefault(c =>
                    c.Properties.TryGetValue(PipeWireKeys.PW_KEY_APP_NAME, out string? v)
                    && v == "pwnet-perm-read"
                );

                if (own is null)
                    await Task.Delay(50, cts.Token);
            }

            Assert.IsNotNull(own, "no client in the graph is this process");

            await using PipeWireClientProxy client = reg.BindClient(own!.Id);
            await client.ReadyAsync(cts.Token);

            ImmutableArray<PipeWireObjectPermission> permissions = await client.GetPermissionsAsync(
                cancellationToken: cts.Token
            );

            Assert.IsFalse(
                permissions.IsDefaultOrEmpty,
                "the daemon answered with no permission entries at all"
            );

            // Reading twice in a row has to work: the waiter is per-call and must be cleared.
            ImmutableArray<PipeWireObjectPermission> again = await client.GetPermissionsAsync(
                cancellationToken: cts.Token
            );

            Assert.AreEqual(permissions.Length, again.Length, "a second read answered differently");
        }
    }

    /// <summary>A proxy is told when the daemon destroys the object behind it.</summary>
    /// <remarks>
    /// `pw_proxy_events.removed` was wired nowhere until this session, so a caller holding a proxy
    /// had no way to learn its object had gone: the proxy stayed alive as a zombie and every call
    /// through it failed. Watching the graph is the other route to the same news, and it is the one
    /// this test does not use, precisely because the point is the proxy's own signal.
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task AProxyWhoseObjectIsDestroyed_IsToldAboutIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync(
            "pwnet-removed",
            cts.Token
        );

        await using (ctx)
        await using (reg)
        {
            PipeWireNode node = await reg.CreateVirtualSinkAsync(
                "pwnet removed probe",
                cancellationToken: cts.Token
            );

            await using PipeWireNodeProxy proxy = reg.BindNode(node.NodeId);
            await proxy.ReadyAsync(cts.Token);

            var removed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            proxy.Removed += () => removed.TrySetResult();

            Assert.IsFalse(proxy.IsRemoved, "a live proxy reported its object as removed");

            await reg.DestroyGlobalAsync(node.NodeId, cts.Token);

            await removed.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

            Assert.IsTrue(proxy.IsRemoved, "the proxy raised Removed but does not report it");
        }
    }

    /// <summary>The disconnect signal is readable and starts clear.</summary>
    /// <remarks>
    /// Losing the daemon on purpose would take the rest of the suite's session with it, so what is
    /// pinned here is the part a consumer depends on being true on a healthy connection: the fault
    /// is null, the event is subscribable, and neither is internal-only - which is what it was
    /// until this session.
    /// </remarks>
    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("RequiresDaemon")]
    public async Task AHealthyConnection_ReportsNoFaultAndAcceptsALostHandler()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry reg) = await ConnectAsync("pwnet-fault", cts.Token);

        await using (ctx)
        await using (reg)
        {
            var seen = 0;
            void OnLost(PipeWireConnectionClosedException _) => Interlocked.Increment(ref seen);

            ctx.ConnectionLost += OnLost;

            Assert.IsNull(ctx.ConnectionFault, "a healthy connection reported a fault");
            Assert.AreEqual(
                0,
                Volatile.Read(ref seen),
                "ConnectionLost fired on a healthy connection"
            );

            ctx.ConnectionLost -= OnLost;
        }
    }
}
