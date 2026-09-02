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
            PipeWireNode node = await registry.CreateVirtualStereoNode("Formats")
                .WithName($"pwnet_formats_{Environment.ProcessId}_{Random.Shared.Next():x}")
                .ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);
            await control.ReadyAsync(cts.Token);

            // An adapter reports the formats it can be configured for. An empty result is a valid
            // answer for a node that has none, so the contract is "does not fail", not "is not empty".
            ImmutableArray<SpaObject> formats = await control.EnumerateFormatsAsync(cts.Token);

            foreach (SpaObject format in formats)
                Assert.AreEqual(SpaType.ObjectFormat, format.ObjectType);

            await registry.RemoveObjectAsync(node.NodeId, cts.Token);
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
}
