using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Tests;

/// <summary>
/// What a core round trip orders, and where it stops ordering anything.
/// </summary>
/// <remarks>
/// A barrier orders one connection against the daemon. That reads like "the write has landed", and
/// for a write the daemon itself applies, it has. For the stores that matter most it has not: the
/// session manager serves <c>default</c> and friends from its own process, so a write travels
/// client, daemon, session manager, daemon, client, and no barrier held by either end waits for the
/// middle. The third test isolates the cause: the same sequence against a store this process
/// serves has no third party in it and is ordered.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class CrossProcessOrderingTests
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
    public async Task AWriterSeesItsOwnWriteWithoutWaiting_EvenThroughTheSessionManager()
    {
        // The optimistic local apply. The value is in the writer's own cache when SetAsync returns,
        // whatever the session manager is doing, because the store applied it on the way out.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-xproc-self", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null) Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await store!.ReadyAsync(cts.Token);
                string key = Unique("pwnet.xproc.self");

                try { await store.SetAsync(key, "v", cancellationToken: cts.Token); }
                catch (PipeWireException) { Assert.Inconclusive("cannot write metadata here."); }

                Assert.AreEqual("v", store.Get(key), "a client cannot read back its own write");

                await store.SetAsync(key, null, cancellationToken: CancellationToken.None);
                Assert.IsNull(store.Get(key), "the removal is not reflected locally either");
            }
        }
    }

    [TestMethod]
    public async Task ASecondClientLearnsOfTheWrite_ByEventAndNotByBarrier()
    {
        // The half that is not ordered. A barrier on the reader is issued and allowed to complete
        // before the event is waited for, so if the barrier did order the session manager's hop the
        // value would already be there. It is recorded rather than asserted either way: the point
        // is that the event is what can be relied on, and it must arrive.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext a, PipeWireRegistry ra) = await ConnectAsync("pwnet-xproc-a", cts.Token);
        (PipeWireContext b, PipeWireRegistry rb) = await ConnectAsync("pwnet-xproc-b", cts.Token);
        await using (a)
        await using (ra)
        await using (b)
        await using (rb)
        {
            PipeWireMetadataStore? writer = ra.BindMetadataStore("default");
            PipeWireMetadataStore? reader = rb.BindMetadataStore("default");
            if (writer is null || reader is null)
                Assert.Inconclusive("no session manager, so no default store.");

            await using (writer)
            await using (reader)
            {
                await Task.WhenAll(writer.ReadyAsync(cts.Token), reader.ReadyAsync(cts.Token));

                string key = Unique("pwnet.xproc.peer");
                try
                {
                    string? seen = await MetadataRelay.AwaitRelayAsync(
                        reader,
                        key,
                        async () =>
                        {
                            try { await writer.SetAsync(key, "v", cancellationToken: cts.Token); }
                            catch (PipeWireException) { Assert.Inconclusive("cannot write metadata here."); }

                            await reader.ReadyAsync(cts.Token);

                            // Whether this is already "v" is a race, so it is not the assertion. The
                            // assertion is that waiting works, which is the contract callers get.
                            Console.WriteLine(
                                $"reader held '{reader.Get(key) ?? "(null)"}' the moment its barrier completed");
                        },
                        cts.Token);

                    Assert.AreEqual("v", seen, "the second client was told, with the wrong value");
                    Assert.AreEqual("v", reader.Get(key), "the event fired but the store does not hold it");
                }
                finally
                {
                    await writer.SetAsync(key, null, cancellationToken: CancellationToken.None);
                }
            }
        }
    }

    [TestMethod]
    public async Task AStoreThisProcessServes_IsOrderedByTheBarrier()
    {
        // The control. Same client, same barrier, same read, with the session manager taken out of
        // the path: this process serves the store, so the write is applied before the round trip
        // that follows it can complete. If this raced too, the diagnosis above would be wrong.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-xproc-own", cts.Token);
        await using (ctx)
        await using (registry)
        {
            await using PipeWireMetadataProvider provider =
                PipeWireMetadataProvider.Create(ctx, Unique("pwnet-xproc-store"));

            string key = Unique("pwnet.xproc.own");

            for (int round = 0; round < 20; round++)
            {
                string value = $"v{round}";
                provider.Set(key, value);

                await CoreSync.RoundTripAsync(ctx, cts.Token);

                Assert.AreEqual(value, provider.Get(key),
                    "a store served in this process lost a write across its own barrier");
            }

            provider.Clear();
            Assert.IsNull(provider.Get(key));
        }
    }
}
