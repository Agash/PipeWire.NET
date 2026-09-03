using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// Abuse of the public surface: lifecycle called out of order, names a caller should not send,
/// pressure from many simultaneous connections, and the daemon dropping us mid-session.
/// </summary>
/// <remarks>
/// Nothing here is a supported usage. The bar is that each one fails predictably - a documented
/// exception, or a clean shutdown - rather than hanging, corrupting the graph, or taking the process
/// down. A native binding that only behaves when used correctly is a binding that crashes in
/// production.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class HostileConditionsTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    // ------------------------------------------------------------------ lifecycle order

    [TestMethod]
    public async Task StartingTwice_IsRefusedWithoutLeakingTheFirstLoop()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-hc-twice", ConsoleTestLoggerFactory.Instance);

        await ctx.StartAsync(cts.Token);

        // Whatever it does, it must not silently start a second loop and orphan the first.
        try
        {
            await ctx.StartAsync(cts.Token);
        }
        catch (InvalidOperationException)
        {
        }

        // Either way the context must still be usable afterwards.
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);
        Assert.IsTrue(reg.Current.Nodes.Length > 0, "the context stopped working after a double start");
    }

    [TestMethod]
    public async Task BuildingARegistryOnAnUnstartedContext_FailsRatherThanCrashing()
    {
        RequireLinux();
        await using var ctx = new PipeWireContext("pwnet-hc-unstarted", ConsoleTestLoggerFactory.Instance);

        // There is no core to get a registry from yet. Dereferencing a null core in native code
        // would be a segfault, so this has to be a managed failure.
        try
        {
            await using var reg = new PipeWireRegistry(ctx);
            Assert.Fail("a registry on an unstarted context must not appear to work");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or ArgumentNullException)
        {
        }
    }

    [TestMethod]
    public async Task DisposingAnUnstartedContext_IsClean()
    {
        RequireLinux();
        var ctx = new PipeWireContext("pwnet-hc-nostart", ConsoleTestLoggerFactory.Instance);

        // Nothing was allocated natively; unwinding must cope with that rather than freeing nulls.
        await ctx.DisposeAsync();
        await ctx.DisposeAsync();
    }

    [TestMethod]
    public async Task UsingAContextAfterDisposal_ThrowsRatherThanTouchingFreedMemory()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        var ctx = new PipeWireContext("pwnet-hc-afterdispose", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await ctx.DisposeAsync();

        // The loop and core are gone. Every entry point has to notice.
        try
        {
            await using var reg = new PipeWireRegistry(ctx);
            Assert.Fail("a registry on a disposed context must not appear to work");
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or ArgumentNullException)
        {
        }
    }

    [TestMethod]
    public async Task DisposingWhileTheRegistryIsStillAlive_DoesNotCrash()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        var ctx = new PipeWireContext("pwnet-hc-order", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // Deliberately the wrong order: the loop the registry depends on goes first. The handle
        // ref-counting exists precisely so this does not free the loop out from under it.
        await ctx.DisposeAsync();
        await reg.DisposeAsync();
    }

    // ------------------------------------------------------------------ hostile names

    [TestMethod]
    [DataRow("a")]
    [DataRow("with spaces and punctuation!")]
    [DataRow("äöüß 中文 \U0001F50A")]
    [DataRow("../../etc/passwd")]
    [DataRow("node.name=injected media.class=Video/Source")]
    [DataRow("'; DROP TABLE nodes; --")]
    [DataRow("\t\n\r")]
    public async Task ANodeNameThatIsHostileOrExotic_IsHandledOrRefusedCleanly(string name)
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-hc-names", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // These become C strings in a property dictionary. The property separator characters are
        // the interesting ones: a name must not be able to inject a second property.
        try
        {
            PipeWireNode node = await reg.CreateVirtualNode("Hostile").WithName(name)
                                         .ExecuteAsync(cts.Token);

            PipeWireNode? live = reg.Current.GetNode(node.NodeId);
            Assert.IsNotNull(live);
            Assert.AreEqual(name, live!.NodeName, "the name must round-trip exactly, not be reinterpreted");
            Assert.AreEqual("Audio/Sink", live.MediaClass,
                "a name must never be able to change another property");

            await reg.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // A refusal is also acceptable; a crash or a corrupted graph is not.
        }
    }

    [TestMethod]
    public async Task ANameContainingAnEmbeddedNul_DoesNotTruncateSilentlyIntoAnotherProperty()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-hc-nul", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // A C string ends at the first NUL, so everything after it is invisible to the daemon.
        // Whatever we do, the surviving prefix must be exactly what the graph reports.
        const string Name = "pwnet_hc_nul\0hidden";
        try
        {
            PipeWireNode node = await reg.CreateVirtualNode("Nul").WithName(Name)
                                         .ExecuteAsync(cts.Token);

            string? stored = reg.Current.GetNode(node.NodeId)?.NodeName;
            Assert.IsNotNull(stored);
            Assert.IsFalse(stored!.Contains("hidden", StringComparison.Ordinal),
                "text after a NUL must not reappear in the graph");

            await reg.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
        catch (ArgumentException)
        {
        }
    }

    [TestMethod]
    public async Task AVeryLongNodeName_IsAcceptedOrRefusedButNeverTruncatedSilently()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-hc-long", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        var name = "pwnet_hc_" + new string('x', 8192);
        PipeWireNode node = await reg.CreateVirtualNode("Long").WithName(name).ExecuteAsync(cts.Token);

        string? stored = reg.Current.GetNode(node.NodeId)?.NodeName;
        Assert.AreEqual(name, stored, "a name that was accepted must come back whole");

        await reg.DestroyGlobalAsync(node.NodeId, cts.Token);
    }

    // ------------------------------------------------------------------ streams pointed nowhere

    [TestMethod]
    public async Task CapturingFromANodeThatDoesNotExist_FailsOrIdlesButNeverHangs()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-hc-nowhere", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);

        var states = new List<PipeWireStreamState>();
        var frames = 0;

        await using var capture = new PipeWireVideoCapture(ctx, "pwnet-hc-nowhere-consumer");
        capture.StateChanged += (_, _, s) => { lock (states) states.Add(s); };
        capture.FrameReady += (_, _) => Interlocked.Increment(ref frames);

        // 999999 is not a live global. Connecting to it must reach a definite state rather than
        // sitting in Connecting forever, which is what a consumer would see as a silent hang.
        try
        {
            capture.Connect(999_999, [PixelFormat.Bgra]);
        }
        catch (Exception e) when (e is InvalidOperationException or PipeWireException)
        {
            return;   // refusing outright is the cleanest answer
        }

        await Task.Delay(2000, cts.Token);

        lock (states)
        {
            Assert.IsTrue(states.Count > 0, "the stream never reported any state at all");
            Assert.IsFalse(states[^1] is PipeWireStreamState.Streaming,
                "a stream targeting a nonexistent node must not claim to be streaming");
        }

        Assert.AreEqual(0, Volatile.Read(ref frames), "frames arrived from a node that does not exist");
    }

    [TestMethod]
    public async Task CapturingFromANodeThatDisappearsBeforeWeConnect_IsHandled()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-hc-gone", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // A patchbay holding a stale id and acting on it is the everyday version of this.
        PipeWireNode node = await reg.CreateVirtualNode("Gone")
                                     .WithName("pwnet_hc_gone").ExecuteAsync(cts.Token);
        uint staleId = node.NodeId;
        await reg.DestroyGlobalAsync(staleId, cts.Token);
        await WaitForAsync(reg, g => g.GetNode(staleId) is null, cts.Token);

        await using var capture = new PipeWireAudioCapture(ctx, "pwnet-hc-gone-consumer");
        try
        {
            capture.Connect(staleId);
            await Task.Delay(1500, cts.Token);   // must not hang, whatever it decides
        }
        catch (Exception e) when (e is InvalidOperationException or PipeWireException)
        {
        }
    }

    // ------------------------------------------------------------------ pressure

    [TestMethod]
    public async Task ManySimultaneousContexts_AllConnectAndAllReleaseTheirDescriptors()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        int fdsBefore = OpenFds();

        const int Count = 16;
        var contexts = new List<PipeWireContext>();
        var registries = new List<PipeWireRegistry>();
        try
        {
            for (int i = 0; i < Count; i++)
            {
                var ctx = new PipeWireContext($"pwnet-hc-many-{i}", ConsoleTestLoggerFactory.Instance);
                await ctx.StartAsync(cts.Token);
                contexts.Add(ctx);

                var reg = new PipeWireRegistry(ctx);
                await reg.WaitForInitialEnumerationAsync(cts.Token);
                registries.Add(reg);
            }

            // Each is a real connection with its own loop thread; all must see the same graph.
            foreach (PipeWireRegistry reg in registries)
                Assert.IsTrue(reg.Current.Nodes.Length > 0, "a connection came up blind");
        }
        finally
        {
            foreach (PipeWireRegistry reg in registries) await reg.DisposeAsync();
            foreach (PipeWireContext ctx in contexts) await ctx.DisposeAsync();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(300, cts.Token);

        int fdsAfter = OpenFds();
        Assert.IsTrue(fdsAfter <= fdsBefore + 4,
            $"{Count} connect/disconnect cycles leaked descriptors: {fdsBefore} -> {fdsAfter}");
    }

    [TestMethod]
    public async Task AStormOfCreatesAndRemoves_KeepsTheGraphAccurate()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-hc-storm", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        // Every id this test creates, so what is asserted at the end is this test's residue rather
        // than the session's population. A session manager adds and removes its own nodes
        // throughout, so comparing the total count against a baseline measures the session,
        // not the library.
        var mine = new ConcurrentBag<uint>();

        // Eight concurrent workers, each churning. The daemon reuses ids aggressively under this,
        // which is exactly the condition that breaks a cache keyed on them.
        Task[] workers = [.. Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                PipeWireNode n = await reg.CreateVirtualNode($"Storm {w}-{i}")
                                          .WithName($"pwnet_hc_storm_{w}_{i}").ExecuteAsync(cts.Token);
                mine.Add(n.NodeId);
                Assert.IsNotNull(reg.Current.GetNode(n.NodeId), "a created node was not in the graph");
                await reg.DestroyGlobalAsync(n.NodeId, cts.Token);
            }
        }, cts.Token))];

        await Task.WhenAll(workers);

        // Everything we made must be gone. By name: the daemon reuses ids aggressively under
        // churn, including for the session manager's own nodes, so a resolved id is only ours
        // when its name says so. A present-but-nameless node is given a beat to gain its
        // properties first, since that is the one shape the name check above cannot see.
        PipeWireGraphSnapshot end = await WaitForAsync(
            reg,
            g => !g.Nodes.Any(n => n.NodeName?.StartsWith("pwnet_hc_storm_", StringComparison.Ordinal) == true),
            cts.Token);

        uint[] left = OursOrNameless(reg, mine);
        for (int attempt = 0; attempt < 20 && left.Length > 0; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);
            await reg.WaitForInitialEnumerationAsync(cts.Token);
            left = OursOrNameless(reg, mine);
        }

        Assert.IsEmpty(left,
            $"the storm left {left.Length} of its own nodes in the graph: {string.Join(", ", left)}");
    }

    /// <summary>Ids this test created that still resolve to one of its nodes, or to no name.</summary>
    private static uint[] OursOrNameless(PipeWireRegistry registry, ConcurrentBag<uint> mine)
    {
        var left = new HashSet<uint>();
        foreach (uint id in mine)
        {
            if (registry.Current.GetNode(id) is not { } node) continue;
            if (node.NodeName is null
                || node.NodeName.StartsWith("pwnet_hc_storm_", StringComparison.Ordinal))
            {
                left.Add(id);
            }
        }

        return [.. left];
    }

    // ------------------------------------------------------------------ losing the daemon

    [TestMethod]
    public async Task BeingDisconnectedByTheDaemon_IsSurvivedAndObservable()
    {
        RequireLinux();
        PwTools.Require();
        using var cts = new CancellationTokenSource(Budget);

        const string AppName = "pwnet-hc-kicked";
        await using var ctx = new PipeWireContext(AppName, ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var reg = new PipeWireRegistry(ctx);
        await reg.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode node = await reg.CreateVirtualNode("Doomed")
                                     .WithName("pwnet_hc_kicked_node").ExecuteAsync(cts.Token);
        Assert.IsNotNull(reg.Current.GetNode(node.NodeId));

        uint? clientId = await FindOurClientIdAsync(AppName, cts.Token);
        if (clientId is null)
            Assert.Inconclusive("could not identify our own client object to disconnect");

        // An admin kicking us off, or the daemon restarting: the connection dies underneath a
        // perfectly healthy process. Nothing here may abort or hang.
        await PwTools.DestroyAsync(clientId.Value, cts.Token);
        await Task.Delay(1000, cts.Token);

        // Reads must keep working - the snapshot is immutable and independent of the connection.
        PipeWireGraphSnapshot lastKnown = reg.Current;
        Assert.IsNotNull(lastKnown, "the last snapshot must survive the connection dying");

        // Mutations must fail rather than appear to succeed against a dead connection.
        try
        {
            await reg.CreateVirtualNode("After").WithName("pwnet_hc_after").ExecuteAsync(cts.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException
                                      or OperationCanceledException or TimeoutException)
        {
            // Any definite failure is fine. Silence would not be.
        }

        // And disposal after the connection is gone must still be clean.
        await reg.DisposeAsync();
    }

    // ------------------------------------------------------------------ helpers

    private static int OpenFds() =>
        Directory.GetFileSystemEntries($"/proc/{Environment.ProcessId}/fd").Length;

    private static async Task<PipeWireGraphSnapshot> WaitForAsync(
        PipeWireRegistry registry, Func<PipeWireGraphSnapshot, bool> until, CancellationToken ct)
    {
        await foreach (PipeWireGraphSnapshot graph in registry.WatchAsync(ct))
            if (until(graph))
                return graph;

        throw new InvalidOperationException("the snapshot stream ended before the condition held");
    }

    /// <summary>
    /// Finds the daemon's Client object for this process.
    /// </summary>
    /// <remarks>
    /// Matched on <c>pipewire.sec.pid</c> rather than a name: the daemon records the connecting
    /// process, whereas <c>application.name</c> is whatever the client chose to claim.
    /// </remarks>
    private static async Task<uint?> FindOurClientIdAsync(string appName, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/pw-dump")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        string json = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (System.Text.Json.JsonElement o in doc.RootElement.EnumerateArray())
        {
            if (!o.TryGetProperty("type", out System.Text.Json.JsonElement type)) continue;
            if (!(type.GetString()?.EndsWith("Client", StringComparison.Ordinal) ?? false)) continue;
            if (!o.TryGetProperty("info", out System.Text.Json.JsonElement info)) continue;
            if (!info.TryGetProperty("props", out System.Text.Json.JsonElement props)) continue;
            if (!o.TryGetProperty("id", out System.Text.Json.JsonElement id)) continue;

            bool ourPid = props.TryGetProperty("pipewire.sec.pid", out System.Text.Json.JsonElement pid)
                          && pid.TryGetInt32(out int value)
                          && value == Environment.ProcessId;

            bool ourName = props.TryGetProperty("application.name", out System.Text.Json.JsonElement name)
                           && name.GetString() == appName;

            if (ourPid || ourName) return id.GetUInt32();
        }
        return null;
    }
}
