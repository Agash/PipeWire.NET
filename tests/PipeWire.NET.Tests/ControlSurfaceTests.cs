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
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class ControlSurfaceTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

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
    public async Task ANodesSupportedFormats_CanBeEnumerated()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-formats", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("Formats")
                .WithName($"pwnet_formats_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);
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
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-defsource", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
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

                Assert.AreEqual(current, store.DefaultAudioSource?.NameValue,
                    "rewriting the default source must leave it where it was");
            }
        }
    }

    [TestMethod]
    public async Task AStoreAndAClientControl_RefuseWorkAfterDisposal()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-disposed", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await store!.DisposeAsync();

            // ClearAsync empties a store the whole session shares, so it is checked only to the
            // point of its guard - calling it for real would take the session's defaults with it.
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                async () => await store.ClearAsync(cts.Token));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                async () => await store.SetAsync("k", "v", cancellationToken: cts.Token));

            PipeWireClient? self = registry.Current.Clients.FirstOrDefault();
            if (self is null)
                Assert.Inconclusive("the registry reported no clients.");

            PipeWireClientControl client = registry.BindClient(self!.Id);
            await client.DisposeAsync();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                async () => await client.UpdatePropertiesAsync(
                    new Dictionary<string, string> { ["k"] = "v" }, cts.Token));
        }
    }

    [TestMethod]
    public async Task UpdatingPermissionsWithNothingToApply_IsRejectedBeforeItReachesTheDaemon()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-perms", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireClient? self = registry.Current.Clients.FirstOrDefault();
            if (self is null)
                Assert.Inconclusive("the registry reported no clients.");

            await using PipeWireClientControl client = registry.BindClient(self!.Id);

            // Reducing a real client's permissions can cut off the connection that would restore
            // them, so only the argument guard is driven here.
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await client.UpdatePermissionsAsync(ReadOnlyMemory<PipeWireObjectPermission>.Empty, cts.Token));
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
    public async Task ASubscribedVolumeChange_RaisesAnEventWhileConcurrentReadsAgree()
    {
        // Two things the ordinary read path never touches: the subscription set the daemon keeps
        // per binding, and several enumerations sharing one answers table keyed by sequence.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-subscribe", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("Subscribe")
                .WithName($"pwnet_sub_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);
            await control.ReadyAsync(cts.Token);

            control.SubscribeParameters(SpaParamType.Props);
            CollectionAssert.AreEqual(
                new[] { SpaParamType.Props }, control.SubscribedParameters.ToArray());

            var changed = new TaskCompletionSource<SpaObject>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            control.ParameterChanged += (_, value) =>
            {
                if (value.ObjectType == SpaType.ObjectProps)
                    changed.TrySetResult(value);
            };

            // No writer is active, so every concurrent read must file under its own key and all
            // must describe the same state.
            Task<ImmutableArray<SpaObject>>[] reads =
                Enumerable.Range(0, 8)
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
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-nodeguards", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("Guards")
                .WithName($"pwnet_guards_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);
            await control.ReadyAsync(cts.Token);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => control.SetVolumeAsync(-1f));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => control.SetVolumeAsync(float.NaN));

            await Assert.ThrowsExactlyAsync<ArgumentNullException>(
                async () => await control.SetPortConfigAsync(null!, cts.Token));
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(
                async () => await control.SetProcessLatencyAsync(null!, cts.Token));
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(
                async () => await control.SetTagAsync(null!, cts.Token));

            // Both channel-volume overloads reach the daemon: unchecked writes verbatim, checked
            // writes against the map and the current volumes.
            await control.SetChannelVolumesAsync(new float[] { 0.5f, 0.5f }, matchChannelMap: false, cts.Token);
            await control.SetChannelVolumesAsync(new float[] { 0.5f, 0.5f }, matchChannelMap: true, cts.Token);

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

        PipeWireMetadataStore? store = registry.BindMetadataStore("settings");
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
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-defguards", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await Assert.ThrowsExactlyAsync<ArgumentException>(
                    async () => await store!.SetDefaultAudioSinkAsync("", cts.Token));
                await Assert.ThrowsExactlyAsync<ArgumentException>(
                    async () => await store!.SetDefaultAudioSourceAsync("", cts.Token));
            }
        }
    }
}
