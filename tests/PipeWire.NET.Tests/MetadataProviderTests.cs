using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Serving a metadata store, rather than consuming one.
/// </summary>
/// <remarks>
/// The daemon returns an object the creating client is expected to serve. Nothing serving it
/// blocks the daemon and stops it answering every client on the machine. These check the store
/// works and that the session survives it.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class MetadataProviderTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static string Unique() => $"pwnet-own-{Environment.ProcessId}-{Random.Shared.Next():x}";

    [TestMethod]
    public async Task AKeyWithAnEmbeddedNul_IsRefusedBeforeItCanDesyncTheStore()
    {
        // Native strings end at the first NUL while the managed cache keys on the whole string.
        // Writing one would file an entry here the daemon records under a truncated key, and the
        // two would never reconcile - so the write is refused rather than half-applied.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-provider-nul", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        using PipeWireMetadataProvider provider = PipeWireMetadataProvider.Create(ctx, Unique());

        Assert.ThrowsExactly<ArgumentException>(() => provider.Set("a\0b", "v"));
        Assert.ThrowsExactly<ArgumentException>(() => provider.Set("k", "a\0b"));
        Assert.ThrowsExactly<ArgumentException>(() => provider.Set("k", "v", "a\0b"));
    }

    [TestMethod]
    public async Task AStoreWeServe_LeavesTheSessionResponsive()
    {
        RequireLinux();
        CliTool cli = CliTool.Require("pw-cli");
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-provider", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        using (PipeWireMetadataProvider provider = PipeWireMetadataProvider.Create(ctx, Unique()))
        {
            provider.Set("k", "v");

            // The failure this exists for: the daemon stops answering every client, not just us.
            (int exit, _, _) = await cli.RunAsync(["info", "0"], cts.Token, TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, exit, "the daemon stopped answering while we served a store");

            await registry.WaitForInitialEnumerationAsync(cts.Token);
        }

        (int after, _, _) = await cli.RunAsync(["info", "0"], cts.Token, TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, after, "the daemon stopped answering after the store went away");
    }

    [TestMethod]
    public async Task AStoreWeServe_ReportsEveryChangeItAccepts()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-provider-events", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        using PipeWireMetadataProvider provider = PipeWireMetadataProvider.Create(ctx, Unique());

        var seen = new List<PipeWireMetadataEntry>();
        provider.EntryChanged += (_, e) => { lock (seen) seen.Add(e); };

        provider.Set("a", "1");
        provider.Set("a", "2");
        provider.Set("a", null);

        lock (seen)
        {
            Assert.AreEqual(3, seen.Count, "each accepted change must be reported once");
            Assert.AreEqual("1", seen[0].Value);
            Assert.AreEqual("2", seen[1].Value);
            Assert.IsNull(seen[2].Value, "a removal is reported as a null value");
        }

        Assert.IsNull(provider.Get("a"));
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task ClearingAStoreWeServe_EmptiesEverySubjectInOneCallEach()
    {
        // One set per entry drops the loop lock between each, so a reader can catch the store
        // half-cleared. A null key is the implementation's own clear form: it empties the subject
        // and emits one notification without letting go of the lock.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-provider-clear", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        using PipeWireMetadataProvider provider = PipeWireMetadataProvider.Create(ctx, Unique());

        provider.Set("a", "1");
        provider.Set("b", "2");
        provider.Set("c", "3", subject: 7);
        provider.Set("d", "4", subject: 7);
        Assert.AreEqual(4, provider.Entries.Count);

        int notifications = 0;
        provider.EntryChanged += (_, _) => Interlocked.Increment(ref notifications);

        provider.Clear();

        Assert.AreEqual(0, provider.Entries.Count, "the store still holds entries after a clear");
        Assert.IsNull(provider.Get("a"));
        Assert.IsNull(provider.Get("c", subject: 7));

        // One notification per entry removed, from the callback that walks the cache. What the
        // clear does not do is issue one native write per entry.
        Assert.AreEqual(4, notifications, "every removal must be reported");
    }

    [TestMethod]
    public async Task ClearingAnEmptyStore_IsNotAnError()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-provider-clear-empty", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        using PipeWireMetadataProvider provider = PipeWireMetadataProvider.Create(ctx, Unique());

        provider.Clear();
        Assert.AreEqual(0, provider.Entries.Count);
    }

    [TestMethod]
    public async Task AUnexportedStore_StaysInsideThisProcess()
    {
        // Exporting is what publishes the global; without it the store works locally and
        // nothing else ever sees it. Disposing then using it is refused like any other use
        // after disposal.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-provider-local", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();
        using PipeWireMetadataProvider provider =
            PipeWireMetadataProvider.Create(ctx, name, export: false);

        provider.Set("k", "v");
        Assert.AreEqual("v", provider.Get("k"));

        await registry.WaitForInitialEnumerationAsync(cts.Token);
        Assert.IsNull(
            registry.Current.Objects.FirstOrDefault(o =>
                o is PipeWireMetadataObject metadata && metadata.MetadataName == name),
            "an unexported store is visible in the graph");

        provider.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => provider.Set("k", "v"));
    }

    [TestMethod]
    public async Task ClearingAStoreWeServeThroughItsBinding_EmptiesIt()
    {
        // The consumer is a second connection, which is what a store is for: serving and
        // consuming over one connection wedges the session, so no test does that here.
        // Clearing the session's shared store would take every client's defaults with it, so
        // the store cleared here is one this same test serves instead.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-provider-clearbind", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string name = Unique();
        using PipeWireMetadataProvider provider = PipeWireMetadataProvider.Create(ctx, name);

        await using var reader = new PipeWireContext("pwnet-provider-clearread", ConsoleTestLoggerFactory.Instance);
        await reader.StartAsync(cts.Token);
        await using var readerRegistry = new PipeWireRegistry(reader);
        await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireMetadataStore? store = null;
        long appearUntil = Environment.TickCount64 + 20_000;
        while (store is null && Environment.TickCount64 < appearUntil)
        {
            await readerRegistry.WaitForInitialEnumerationAsync(cts.Token);
            store = readerRegistry.BindMetadataStore(name);
            if (store is null)
                await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);
        }

        if (store is null)
            Assert.Inconclusive("the served store never appeared in the graph.");

        await using (store)
        {
            await store!.ReadyAsync(cts.Token);
            await store.SetAsync("k", "v", cancellationToken: cts.Token);
            Assert.AreEqual("v", store.Get("k"));
            await WaitForProviderValueAsync(provider, "k", "v", cts.Token);

            // Per-key removals travel as property events and land; the bulk clear below does
            // not reach the implementation at all (the daemon answers it from its own state),
            // so the two are asserted separately on purpose.
            await store.SetAsync("j", "w", cancellationToken: cts.Token);
            await WaitForProviderValueAsync(provider, "j", "w", cts.Token);
            await store.SetAsync("j", null, cancellationToken: cts.Token);
            await WaitForProviderValueAsync(provider, "j", null, cts.Token);

            await store.ClearAsync(cts.Token);

            Assert.IsNull(store.Get("k"), "the store still holds entries after a clear");
        }
    }

    /// <summary>
    /// Waits until the serving process holds an expected value for a key.
    /// </summary>
    /// <remarks>
    /// The writer's round-trip proves the daemon processed the write, not that the forward to
    /// this implementation has been dispatched yet, so reading immediately is a race the test
    /// would sometimes lose. A value that never arrives is still a failure, just a slower one.
    /// </remarks>
    private static async Task WaitForProviderValueAsync(
        PipeWireMetadataProvider provider, string key, string? expected, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            if (provider.Get(key) == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        Assert.AreEqual(expected, provider.Get(key),
            $"the serving process never held '{expected ?? "<null>"}` for '{key}'");
    }
}
