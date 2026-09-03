using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// The native ownership graph: proxy depends on core, core depends on loop.
/// </summary>
/// <remarks>
/// <para>
/// <c>pw_proxy_destroy</c> reads <c>proxy-&gt;core</c> while the context disconnects the core
/// independently, so the question is whether a proxy outliving the context dereferences freed memory.
/// </para>
/// <para>
/// During core teardown <c>destroy_proxy</c> sets <c>p-&gt;core = NULL</c> on every surviving proxy,
/// and both <c>pw_proxy_destroy</c> and <c>remove_from_map</c> guard on <c>proxy-&gt;core</c> being
/// non-null. The loop is separately protected by the handle ref-count.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class NativeLifetimeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static async Task<PipeWireGraphSnapshot> WaitForAsync(
        PipeWireRegistry registry, Func<PipeWireGraphSnapshot, bool> until, CancellationToken ct)
    {
        await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(ct))
            if (until(graph))
                return graph;

        throw new InvalidOperationException("the snapshot stream ended before the condition held");
    }

    [TestMethod]
    public async Task DestroyingTheContextWhileOwningProxies_DoesNotAbort()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // Own several proxies, tear the context down first, then let the registry unwind afterwards.
        var ctx = new PipeWireContext("pwnet-nl-owned", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        for (int i = 0; i < 4; i++)
            await reg.CreateVirtualNode($"Owned {i}").WithName($"pwnet_nl_owned_{i}")
                     .ExecuteAsync(cts.Token);

        await ctx.DisposeAsync();     // core disconnected, proxies still owned
        await reg.DisposeAsync();     // unwinds afterwards

        // Surviving is necessary but not sufficient: prove the process can still reach the daemon,
        // which a corrupted connection or a damaged loop would prevent.
        Assert.IsTrue(await CanStillConnectAsync(cts.Token),
            "the process survived but can no longer talk to the daemon");
    }

    /// <summary>Opens a fresh connection and reads the graph, as evidence the process is healthy.</summary>
    private static async Task<bool> CanStillConnectAsync(CancellationToken ct)
    {
        await using var probe = new PipeWireContext("pwnet-nl-probe", ConsoleTestLoggerFactory.Instance);
        await probe.StartAsync(ct);
        await using var reg = new PipeWireRegistry(probe);
        await reg.WaitForInitialEnumerationAsync(ct);
        return reg.Current.Nodes.Length > 0;
    }

    [TestMethod]
    public async Task ProxiesReleasedByFinalizationAfterTheContextIsGone_DoNotAbort()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // The harder version: nothing disposes the registry at all, so the proxy handles are
        // released by finalization, long after the context and its core went away. This removes
        // every ordering guarantee except the handle ref-count itself.
        await CreateAndAbandonAsync(cts.Token);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        await Task.Delay(500, cts.Token);

        Assert.IsTrue(await CanStillConnectAsync(cts.Token),
            "finalizing orphaned proxies left the process unable to reach the daemon");
    }

    [TestMethod]
    public async Task ALingeringNodeSurvivesItsCreatorBeingFinalized_NotJustDisposed()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // Disposal is the orderly path and it is already covered. This is the one that says whether
        // linger is a property of the object on the daemon or an artefact of how we shut down: the
        // creator is abandoned to the finalizer, so the proxies are released with no ordering and
        // no chance for the library to say anything to the daemon first. If a finalizer took the
        // node with it, linger would be a promise the library breaks whenever a caller forgets a
        // using, which is exactly when they are relying on it.
        uint lingering = await CreateLingeringAndAbandonAsync(cts.Token);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        await Task.Delay(500, cts.Token);

        await using var observer = new PipeWireContext("pwnet-linger-finalize-obs", ConsoleTestLoggerFactory.Instance);
        await observer.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(observer);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        Assert.IsNotNull(registry.Current.GetNode(lingering),
            "a lingering node did not survive its creator being finalized rather than disposed");

        // And it is a real object, not a stale registry entry: another client can still take it down.
        await registry.DestroyGlobalAsync(lingering, cts.Token);
        await WaitForRemovedAsync(registry, lingering, cts.Token);
    }

    [TestMethod]
    public async Task AControlDroppedWithoutDisposing_IsStillCollectable()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // A listener needs the daemon to be able to find the managed object, and the obvious way to
        // arrange that is a strong GCHandle. The trap is that the handle is freed by the proxy
        // handle's release path, the proxy handle is reached through the object the GCHandle roots,
        // and a rooted object is never finalized: the release that frees the handle is waiting on
        // the handle being freed. A caller who forgets a using then leaks the control, its proxy and
        // its listener for the life of the process, and no descriptor or daemon-side count shows it.
        await using var ctx = new PipeWireContext("pwnet-control-collect", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode node = await registry.CreateVirtualNode("Collect")
            .WithName($"pwnet_collect_{Environment.ProcessId}_{Random.Shared.Next():x}")
            .ExecuteAsync(cts.Token);

        WeakReference weak = BindAndAbandon(registry, node.NodeId);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.IsFalse(weak.IsAlive,
            "a control dropped without disposing is still rooted, so it and its proxy never go away");

        await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
    }

    /// <summary>Binds a control and returns only a weak reference to it.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference BindAndAbandon(PipeWireRegistry registry, uint nodeId)
    {
        PipeWireNodeControl control = registry.BindNode(nodeId);
        return new WeakReference(control);
    }

    [TestMethod]
    public async Task ManyContextsCreatedAndDisposed_DoNotAccumulateProcessResources()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // pw_init is called per context and pw_deinit is never called, which reads like an
        // unbalanced refcount. The balancing call is not the fix it looks like: pw_deinit tears down
        // process-global PipeWire state, and a library that does that when its last context closes
        // breaks a host application, or another library in the same process, that is still using
        // PipeWire. So the question is not whether the calls are balanced but whether repeating
        // pw_init actually costs anything, which is measurable.
        for (int i = 0; i < 10; i++)
        {
            await using var warm = new PipeWireContext($"pwnet-init-warm-{i}", ConsoleTestLoggerFactory.Instance);
            await warm.StartAsync(cts.Token);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int fdsBefore = OpenFileDescriptors();
        int threadsBefore = ProcessThreads();
        long rssBefore = ResidentBytes();

        const int Rounds = 100;
        for (int i = 0; i < Rounds; i++)
        {
            await using var ctx = new PipeWireContext($"pwnet-init-{i}", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync(cts.Token);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int fdsAfter = OpenFileDescriptors();
        int threadsAfter = ProcessThreads();
        long rssAfter = ResidentBytes();

        Console.Error.WriteLine(
            $"{Rounds} contexts: fds {fdsBefore} -> {fdsAfter}, threads {threadsBefore} -> {threadsAfter}, "
            + $"rss {rssBefore / 1024}KB -> {rssAfter / 1024}KB");

        Assert.IsTrue(fdsAfter <= fdsBefore + 2,
            $"file descriptors grew {fdsBefore} -> {fdsAfter} over {Rounds} contexts");
        Assert.IsTrue(threadsAfter <= threadsBefore + 1,
            $"threads grew {threadsBefore} -> {threadsAfter}, so loop threads are being stranded");

        // Resident memory is the one that would show a per-pw_init allocation never released.
        // Generous, because the allocator keeps what it takes: 100 rounds leaking even 8KB apiece
        // would be 800KB and fail this.
        Assert.IsTrue(rssAfter - rssBefore < 512 * 1024,
            $"resident memory grew {(rssAfter - rssBefore) / 1024}KB over {Rounds} contexts");
    }

    private static int OpenFileDescriptors() => Directory.GetFiles("/proc/self/fd").Length;

    private static int ProcessThreads() => ReadStatus("Threads:");

    private static long ResidentBytes() => ReadStatus("VmRSS:") * 1024L;

    private static int ReadStatus(string key)
    {
        foreach (string line in File.ReadLines("/proc/self/status"))
        {
            if (!line.StartsWith(key, StringComparison.Ordinal)) continue;

            ReadOnlySpan<char> rest = line.AsSpan(key.Length).Trim();
            int space = rest.IndexOf(' ');
            if (space > 0) rest = rest[..space];

            return int.Parse(rest, System.Globalization.CultureInfo.InvariantCulture);
        }

        return -1;
    }

    /// <summary>Creates a lingering node and drops every managed reference without disposing.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<uint> CreateLingeringAndAbandonAsync(CancellationToken ct)
    {
        var context = new PipeWireContext("pwnet-linger-finalize", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(ct);

        var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(ct);

        PipeWireNode node = await registry.CreateVirtualNode("LingerFinalize")
            .WithName($"pwnet_linger_fin_{Environment.ProcessId}_{Random.Shared.Next():x}")
            .WithLinger()
            .ExecuteAsync(ct);

        // Nothing is disposed and nothing is returned but the id, so both objects are unreachable
        // the moment this returns.
        return node.NodeId;
    }

    private static async Task WaitForRemovedAsync(
        PipeWireRegistry registry, uint id, CancellationToken ct)
    {
        while (registry.Current.GetNode(id) is not null)
        {
            ct.ThrowIfCancellationRequested();
            await registry.WaitForInitialEnumerationAsync(ct);
        }
    }

    /// <summary>Creates owned proxies and drops every managed reference without disposing.</summary>
    private static async Task CreateAndAbandonAsync(CancellationToken ct)
    {
        var ctx = new PipeWireContext("pwnet-nl-abandoned", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(ct);
        var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(ct);

        for (int i = 0; i < 3; i++)
            await reg.CreateVirtualNode($"Abandoned {i}").WithName($"pwnet_nl_ab_{i}")
                     .ExecuteAsync(ct);

        // Context goes; registry and its proxy handles are simply dropped.
        await ctx.DisposeAsync();
    }

    [TestMethod]
    public async Task TheLoopOutlivesEveryProxyThatReferencesIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // The ref-count exists so the loop cannot be destroyed while a proxy still needs it to
        // take the loop lock during its own destruction. Disposing the context asks for the loop
        // to go; the proxies must keep it alive until they are done with it.
        var ctx = new PipeWireContext("pwnet-nl-looprefs", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode node = await reg.CreateVirtualNode("LoopRef")
                                     .WithName("pwnet_nl_loopref").ExecuteAsync(cts.Token);
        Assert.IsNotNull(reg.Current.GetNode(node.NodeId));

        await ctx.DisposeAsync();

        // Destroying a proxy takes the loop lock. If the loop had really gone, this is where it
        // would fault rather than throw.
        await reg.DisposeAsync();

        Assert.IsTrue(await CanStillConnectAsync(cts.Token),
            "tearing down proxies after the context left the process unusable");
    }

    [TestMethod]
    public async Task AForeignObjectIsNeverDestroyedThroughAProxyWeDoNotOwn()
    {
        RequireLinux();
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);

        await using var ctx = new PipeWireContext("pwnet-nl-foreign", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // A node published by another process. We hold no proxy for it, so our disposal must not
        // attempt pw_proxy_destroy against something we never created - the double-destruction
        // that an ownership abstraction hiding the distinction would cause.
        await using PwTools.Loopback loop = await PwTools.StartLoopbackAsync("pwnet_nl_foreign", cts.Token);

        PipeWireGraphSnapshot graph = await WaitForAsync(
            reg, g => g.Nodes.Any(n => n.NodeName == "input.pwnet_nl_foreign"), cts.Token);

        uint foreignId = graph.Nodes.First(n => n.NodeName == "input.pwnet_nl_foreign").NodeId;
        Assert.IsNotNull(reg.Current.GetNode(foreignId));

        // Our own node alongside it, so disposal has both kinds to unwind.
        await reg.CreateVirtualNode("Ours").WithName("pwnet_nl_ours").ExecuteAsync(cts.Token);

        // Disposal releases only what we own. A second destroy of the foreign object would trip
        // the assertion in proxy.c and abort.
        await reg.DisposeAsync();

        // The foreign node must still be there: it was never ours to destroy.
        Assert.IsTrue(await ForeignNodeStillPresentAsync("input.pwnet_nl_foreign", cts.Token),
            "disposing our registry destroyed a node belonging to another process");
    }

    /// <summary>Re-reads the graph from a fresh connection to see whether a node survived.</summary>
    private static async Task<bool> ForeignNodeStillPresentAsync(string nodeName, CancellationToken ct)
    {
        await using var probe = new PipeWireContext("pwnet-nl-foreign-probe", ConsoleTestLoggerFactory.Instance);
        await probe.StartAsync(ct);
        await using var reg = new PipeWireRegistry(probe);
        await reg.WaitForInitialEnumerationAsync(ct);
        return reg.Current.Nodes.Any(n => n.NodeName == nodeName);
    }

    [TestMethod]
    public async Task DestroyingTheContextWhileAStreamIsConnected_DoesNotAbandonIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // pw_stream is the one PipeWire object still held as a raw pointer rather than a handle,
        // so this is the case where that divergence would show: the context torn down first, with
        // a live stream that has not been disposed.
        var ctx = new PipeWireContext("pwnet-nl-stream", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        var output = new PipeWireAudioOutput(ctx, "pwnet_nl_stream");
        output.FillSamples += (_, s, _, _, _) => { s.Clear(); return s.Length; };
        output.Connect(autoConnect: false);
        await Task.Delay(300, cts.Token);

        await ctx.DisposeAsync();
        await output.DisposeAsync();

        Assert.IsTrue(await CanStillConnectAsync(cts.Token),
            "tearing the context down under a live stream left the process unusable");
    }

    [TestMethod]
    public async Task ASnapshotOutlivingTheNativeObjectsItDescribes_IsStillReadable()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        // A snapshot is an immutable observation, not a handle. It must stay readable after every
        // native object it names has gone, because consumers hold them across changes by design.
        PipeWireGraphSnapshot held;
        uint nodeId;
        {
            var ctx = new PipeWireContext("pwnet-nl-outlive", ConsoleTestLoggerFactory.Instance);
            await ctx.StartAsync(cts.Token);
            var reg = new PipeWireRegistry(ctx);
            await reg.WaitForInitialEnumerationAsync(cts.Token);

            PipeWireNode node = await reg.CreateVirtualNode("Outlive")
                                         .WithName("pwnet_nl_outlive").ExecuteAsync(cts.Token);
            nodeId = node.NodeId;
            held = await WaitForAsync(reg, g => g.GetPortsForNode(nodeId).Length == 4, cts.Token);

            await reg.DisposeAsync();
            await ctx.DisposeAsync();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Every query still answers from the retained arrays, with no native access at all.
        Assert.IsNotNull(held.GetNode(nodeId));
        Assert.AreEqual(4, held.GetPortsForNode(nodeId).Length);
        Assert.IsTrue(held.Nodes.Length > 0);
        foreach (PipeWirePort port in held.GetPortsForNode(nodeId))
            Assert.IsNotNull(held.GetPort(port.PortId));
    }

    [TestMethod]
    public async Task EveryGraphChangedNotification_SeesTheSnapshotItDescribes()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-nl-ordering", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // The ordering the whole snapshot model rests on: publish, then notify. A handler that saw
        // Current still pointing at the previous graph would make every consumer race the registry.
        var violations = 0;
        var seen = 0;
        reg.GraphChanged += (sender, snapshot) =>
        {
            Interlocked.Increment(ref seen);
            PipeWireGraphSnapshot current = sender.Current;

            // Current is either this snapshot or a newer one, never an older one.
            if (current.Version < snapshot.Version) Interlocked.Increment(ref violations);
        };

        PipeWireNode node = await reg.CreateVirtualNode("Order")
                                     .WithName("pwnet_nl_order").ExecuteAsync(cts.Token);
        await WaitForAsync(reg, g => g.GetPortsForNode(node.NodeId).Length == 4, cts.Token);
        await reg.DestroyGlobalAsync(node.NodeId, cts.Token);
        await WaitForAsync(reg, g => g.GetNode(node.NodeId) is null, cts.Token);

        Assert.IsTrue(Volatile.Read(ref seen) > 0, "no GraphChanged fired, so nothing was checked");
        Assert.AreEqual(0, Volatile.Read(ref violations),
            "a handler observed Current older than the snapshot it was handed");
    }

    [TestMethod]
    public async Task CreatingWhileTheInitialEnumerationIsStillArriving_Succeeds()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-nl-burst", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        // Deliberately no WaitForInitialEnumerationAsync first. Creation has to work while the
        // registry is still receiving the opening burst of globals, because that is when an
        // application starts and immediately publishes its own node.
        await using var reg = new PipeWireRegistry(ctx);

        Task<PipeWireNode> creation = reg.CreateVirtualNode("Burst")
                                         .WithName("pwnet_nl_burst").ExecuteAsync(cts.Token);
        Task enumeration = reg.WaitForInitialEnumerationAsync(cts.Token);

        await Task.WhenAll(creation, enumeration);
        PipeWireNode node = await creation;

        // The object must be complete, not half-built by a snapshot published mid-burst.
        PipeWireNode? live = reg.Current.GetNode(node.NodeId);
        Assert.IsNotNull(live, "a node created during the burst is missing from the graph");
        Assert.AreEqual("pwnet_nl_burst", live!.NodeName, "the node arrived without its properties");
        Assert.AreEqual("Audio/Sink", live.MediaClass);

        await reg.DestroyGlobalAsync(node.NodeId, cts.Token);
    }

    [TestMethod]
    public async Task CreatingBeforeTheRegistryHasSeenAnything_Succeeds()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-nl-immediate", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        // Tighter still: create on the line after construction, before any global can have arrived.
        await using var reg = new PipeWireRegistry(ctx);
        PipeWireNode node = await reg.CreateVirtualNode("Immediate")
                                     .WithName("pwnet_nl_immediate").ExecuteAsync(cts.Token);

        Assert.IsNotNull(reg.Current.GetNode(node.NodeId),
            "creation must not depend on enumeration having finished");

        await reg.DestroyGlobalAsync(node.NodeId, cts.Token);
    }
}
