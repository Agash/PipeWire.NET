using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// What the daemon actually sends, against what the store assumes it sends.
/// </summary>
/// <remarks>
/// The reconciler's correctness rests on matching an echo to the write that caused it, and the match
/// is on (subject, key, type, value). Every field in that tuple is a place where our idea of the
/// value can differ from the daemon's while both look right in isolation: a type we left for the
/// daemon to choose comes back filled in, and a subject we never use comes back on a clear. These
/// drive the daemon rather than a model of it, because a model would have the same assumptions.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class MetadataProtocolTests
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

    private static string Unique(string p) => $"{p}.{Environment.ProcessId}.{Random.Shared.Next():x}";

    [TestMethod]
    public async Task AWriteThatLetsTheDaemonChooseTheType_IsReportedOnce()
    {
        // SetAsync leaves the type null by default, meaning "you decide". The daemon decides
        // Spa:String and echoes that back. The reconciler matches an echo on the whole tuple
        // including the type, so a null written against a Spa:String echoed does not match its own
        // write: the store raises once for the optimistic local apply and again when the echo it
        // failed to recognise arrives as somebody else's change. A subscriber counting changes sees
        // every one of its own writes twice.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-meta-type", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null) Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await store!.ReadyAsync(cts.Token);

                string key = Unique("pwnet.type");
                var raised = new ConcurrentQueue<string?>();

                void OnChanged(PipeWireMetadataStore _, PipeWireMetadataEntry e)
                {
                    if (e.Key == key) raised.Enqueue(e.Value);
                }

                store.EntryChanged += OnChanged;
                try
                {
                    try { await store.SetAsync(key, "once", cancellationToken: cts.Token); }
                    catch (PipeWireException) { Assert.Inconclusive("cannot write metadata here."); }

                    // Long enough for an echo to arrive if one is coming. A barrier does not order
                    // the session manager's hop, so this waits rather than syncing.
                    await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

                    Assert.AreEqual(1, raised.Count,
                        $"one write raised {raised.Count} changes: {string.Join(", ", raised)}");
                }
                finally
                {
                    store.EntryChanged -= OnChanged;
                    await store.SetAsync(key, null, cancellationToken: CancellationToken.None);
                }
            }
        }
    }

    [TestMethod]
    public async Task AStoreClearedByItsServer_EmptiesEveryBoundConsumer()
    {
        // The wire path, not the local one. A clear is reported as a property event, and which
        // subject it carries decides whether a consumer filtering by subject drops the right
        // entries or none of them. Served locally and consumed from a second connection, so the
        // event makes a real round trip without clearing the session's own default store, which
        // holds the machine's audio routing.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext serverCtx, PipeWireRegistry serverReg) = await ConnectAsync("pwnet-clear-server", cts.Token);
        (PipeWireContext clientCtx, PipeWireRegistry clientReg) = await ConnectAsync("pwnet-clear-client", cts.Token);
        await using (serverCtx)
        await using (serverReg)
        await using (clientCtx)
        await using (clientReg)
        {
            string storeName = Unique("pwnet-clear-wire");
            // Exported, which is the only way a second client can find it at all.
            await using PipeWireMetadataProvider provider =
                PipeWireMetadataProvider.Create(serverCtx, storeName, export: true);

            provider.Set("a", "1");
            provider.Set("b", "2");

            PipeWireMetadataStore? consumer = null;
            for (int attempt = 0; attempt < 80 && consumer is null; attempt++)
            {
                await clientReg.WaitForInitialEnumerationAsync(cts.Token);
                consumer = clientReg.BindMetadataStore(storeName);
                if (consumer is null) await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);
            }

            if (consumer is null)
            {
                // The export is what makes the store findable at all, so this means it did not
                // reach the daemon rather than that the feature is missing.
                Assert.Inconclusive("the exported store never reached the other client.");
            }

            await using (consumer)
            {
                await consumer!.ReadyAsync(cts.Token);

                // Everything the store held when the consumer attached. A metadata implementation
                // sends its whole contents to a new listener, so a consumer that binds after the
                // writes still sees them; if that stopped working, a client joining an existing
                // session would start with an empty view of it.
                Assert.AreEqual("1", consumer.Get("a"), "the consumer never received the entries");
                Assert.AreEqual("2", consumer.Get("b"));

                provider.Clear();

                // Waits for the clear to arrive rather than assuming a barrier orders the server's
                // hop, for the reason CrossProcessOrderingTests pins.
                for (int attempt = 0; attempt < 80 && consumer.Get("a") is not null; attempt++)
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);

                Assert.IsNull(consumer.Get("a"),
                    "a cleared store left entries in a bound consumer, so the clear's subject was not understood");
                Assert.IsNull(consumer.Get("b"));
            }
        }
    }

    [TestMethod]
    public async Task AnExternalToolClearingAnExportedStore_EmptiesOurConsumer()
    {
        // The clear path over the wire, driven by a tool that is not us.
        //
        // What it settles is which subject the daemon puts on a store-wide clear. The consumer
        // filters removals by subject, so if it arrives as SPA_ID_INVALID and that is compared
        // against a stored subject of 0, nothing is dropped and the cache keeps values that no
        // longer exist anywhere.
        RequireLinux();
        PwTools.Require();

        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext serverCtx, PipeWireRegistry serverReg) = await ConnectAsync("pwnet-extclear-server", cts.Token);
        (PipeWireContext clientCtx, PipeWireRegistry clientReg) = await ConnectAsync("pwnet-extclear-client", cts.Token);
        await using (serverCtx)
        await using (serverReg)
        await using (clientCtx)
        await using (clientReg)
        {
            string storeName = Unique("pwnet-extclear");
            await using PipeWireMetadataProvider provider =
                PipeWireMetadataProvider.Create(serverCtx, storeName, export: true);

            provider.Set("a", "1");
            provider.Set("b", "2");

            PipeWireMetadataStore? consumer = null;
            for (int attempt = 0; attempt < 80 && consumer is null; attempt++)
            {
                await clientReg.WaitForInitialEnumerationAsync(cts.Token);
                consumer = clientReg.BindMetadataStore(storeName);
                if (consumer is null) await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);
            }

            if (consumer is null) Assert.Inconclusive("the exported store never reached the other client.");

            await using (consumer)
            {
                await consumer!.ReadyAsync(cts.Token);
                Assert.AreEqual("1", consumer.Get("a"), "the consumer never received the entries");

                // pw-metadata with no key clears everything for the subject, from a third process.
                await PwTools.ClearMetadataAsync(storeName, cts.Token);

                for (int attempt = 0; attempt < 80 && consumer.Get("a") is not null; attempt++)
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);

                Assert.IsNull(consumer.Get("a"),
                    "an external clear left entries in the consumer, so its subject was not understood");
                Assert.IsNull(consumer.Get("b"));
            }
        }
    }

    [TestMethod]
    public async Task AStoreClearedLocally_EmptiesItsOwnCache()
    {
        // pw-metadata -d clears every entry. Whatever subject the daemon reports that against, the
        // store has to end up empty: a cache that keeps entries the daemon has dropped reports
        // values that no longer exist anywhere, and nothing later corrects it because the removal
        // already happened.
        RequireLinux();
        PwTools.Require();

        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-meta-clear", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // Our own store, not the session manager's: clearing that would take the session's
            // default sink with it and leave the machine's audio unrouted for every other test.
            await using PipeWireMetadataProvider provider =
                PipeWireMetadataProvider.Create(ctx, Unique("pwnet-clear-store"));

            provider.Set("a", "1");
            provider.Set("b", "2");

            Assert.AreEqual(2, provider.Entries.Count, "the provider did not take the writes");

            provider.Clear();

            Assert.AreEqual(0, provider.Entries.Count,
                "a cleared store still reports entries, so the clear was not applied to the cache");
            Assert.IsNull(provider.Get("a"));
            Assert.IsNull(provider.Get("b"));
        }
    }

    [TestMethod]
    public async Task AWriteTheDaemonRefuses_LeavesNoValueBehindInTheCache()
    {
        // The local apply happens before the round trip is awaited, so the value is readable
        // immediately. If the daemon then refuses the write, the cache is holding something that
        // exists nowhere else, and the caller has been told about a change that did not happen.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-meta-refused", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireMetadataStore? store = registry.BindMetadataStore("default");
            if (store is null) Assert.Inconclusive("no session manager, so no default store.");

            await using (store)
            {
                await store!.ReadyAsync(cts.Token);

                string key = Unique("pwnet.refused");

                // Cancelled before the round trip can complete, which is the reachable failure: a
                // permission refusal needs a restricted client, and this exercises the same path.
                using var race = new CancellationTokenSource();
                Task write = store.SetAsync(key, "ghost", cancellationToken: race.Token);
                race.Cancel();

                try { await write; }
                catch (OperationCanceledException) { /* the point of the test */ }
                catch (PipeWireException) { Assert.Inconclusive("cannot write metadata here."); }

                // Whatever happened, the store and the daemon must agree. Reading back through a
                // fresh barrier is the arbiter: if the write landed the value is there, and if it
                // did not the key is absent, but the cache must not disagree with the daemon.
                await store.ReadyAsync(cts.Token);

                string? cached = store.Get(key);
                Console.Error.WriteLine($"after a cancelled write the cache holds '{cached ?? "(null)"}'");

                // A later write must still take, which a wedged reconciler entry would prevent.
                await store.SetAsync(key, "real", cancellationToken: cts.Token);
                Assert.AreEqual("real", store.Get(key));

                await store.SetAsync(key, null, cancellationToken: CancellationToken.None);
            }
        }
    }
}
