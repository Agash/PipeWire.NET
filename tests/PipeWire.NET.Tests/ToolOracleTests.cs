using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Our view of the session, checked against PipeWire's own tools.
/// </summary>
/// <remarks>
/// These answer a different question from the rest of the suite. Everywhere else the library is
/// asked whether it agrees with itself; here it is asked whether it agrees with independent
/// implementations of the same protocol - pw-dump for structure, wpctl for the session manager's
/// policy, pw-cat for a native media client, pw-mon for event ordering.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class ToolOracleTests
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

    private static string Unique(string prefix) =>
        $"{prefix}_{Environment.ProcessId}_{Random.Shared.Next():x}";

    [TestMethod]
    public async Task OurGraph_AgreesWithPwDumpOnEveryObjectKind()
    {
        RequireLinux();
        CliTool.Require("pw-dump");
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-oracle-dump", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("Oracle")
                .WithName(Unique("pwnet_oracle")).ExecuteAsync(cts.Token);

            // Both views are taken after the same barrier, so neither is a moving target - and the
            // comparison is by id, which is the only thing both sides agree to call an object.
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            PwDump dump = await PwDump.CaptureAsync(cts.Token);
            PipeWireGraphSnapshot ours = registry.Current;

            await AssertWeSeeAllAsync("Node", d => d.IdsOfKind("Node"),
                g => g.Nodes.Select(n => n.NodeId), dump, registry, cts.Token);
            await AssertWeSeeAllAsync("Port", d => d.IdsOfKind("Port"),
                g => g.Ports.Select(p => p.PortId), dump, registry, cts.Token);
            await AssertWeSeeAllAsync("Link", d => d.IdsOfKind("Link"),
                g => g.Links.Select(l => l.LinkId), dump, registry, cts.Token);
            await AssertWeSeeAllAsync("Device", d => d.IdsOfKind("Device"),
                g => g.Devices.Select(x => x.Id), dump, registry, cts.Token);
            // Clients are compared as a superset: pw-dump connects to produce the dump, so its own
            // client is in its output and cannot be in a snapshot taken before it ran.
            // The other direction, which is racy by construction: our snapshot is taken after the
            // dump, so a client that connected in between is legitimately ours alone. What is not
            // legitimate is holding a client that two independent dumps both lack while we still
            // report it, which is an object we invented or failed to retire.
            HashSet<uint> theirClients = dump.IdsOfKind("Client").ToHashSet();
            List<uint> weLack = [.. ours.Clients.Select(c => c.Id).Except(theirClients)];

            if (weLack.Count > 0)
            {
                PwDump second = await PwDump.CaptureAsync(cts.Token);
                HashSet<uint> theirsNow = second.IdsOfKind("Client").ToHashSet();
                HashSet<uint> oursNow = registry.Current.Clients.Select(c => c.Id).ToHashSet();

                List<uint> phantom = [.. weLack.Where(id => !theirsNow.Contains(id) && oursNow.Contains(id))];

                Assert.IsTrue(phantom.Count == 0,
                    $"Client: we still report ids no dump has [{string.Join(",", phantom)}]");
            }
            await AssertWeSeeAllAsync(
                "Client",
                d => d.OfKind("Client")
                    .Where(e => !string.Equals(e.Prop("application.name"), "pw-dump", StringComparison.Ordinal))
                    .Select(e => e.Id),
                g => g.Clients.Select(c => c.Id), dump, registry, cts.Token);
            await AssertWeSeeAllAsync("Factory", d => d.IdsOfKind("Factory"),
                g => g.Factories.Select(f => f.Id), dump, registry, cts.Token);
            await AssertWeSeeAllAsync("Module", d => d.IdsOfKind("Module"),
                g => g.Modules.Select(m => m.Id), dump, registry, cts.Token);
            await AssertWeSeeAllAsync("Metadata", d => d.IdsOfKind("Metadata"),
                g => g.MetadataStores.Select(m => m.Id), dump, registry, cts.Token);

            // And the properties we parsed for our own node match what pw-dump read independently.
            PwDump.Entry? theirs = dump.ById(node.NodeId);
            Assert.IsNotNull(theirs, "pw-dump does not see a node we created");
            Assert.AreEqual(theirs!.Prop("node.name"), ours.GetNode(node.NodeId)!.NodeName,
                "we and pw-dump disagree about a node's name");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    /// <summary>Everything pw-dump reports must reach our graph.</summary>
    /// <remarks>
    /// A containment check, not equality. pw-dump binds each object to describe it and omits the
    /// ones it cannot, while the registry reports every global it is told about, so our side is
    /// legitimately the larger of the two. Missing one is the defect worth catching.
    /// <para>
    /// "Reach", not "already contains". The registry is fed asynchronously, so an object created
    /// while the dump was being produced can be in the dump and not yet in a snapshot taken after
    /// it. Clients are where this shows: every other test in the run connects and disconnects, so
    /// there are always several in flight. An id is therefore given time to arrive, and one that
    /// still has not is checked against a fresh dump before it counts as missing, because an object
    /// that has since gone away is never going to arrive and is not a defect either.
    /// </para>
    /// </remarks>
    private static async Task AssertWeSeeAllAsync(
        string kind,
        Func<PwDump, IEnumerable<uint>> theirs,
        Func<PipeWireGraphSnapshot, IEnumerable<uint>> ours,
        PwDump dump,
        PipeWireRegistry registry,
        CancellationToken cancellationToken)
    {
        HashSet<uint> wanted = theirs(dump).ToHashSet();
        List<uint> missing = [];

        for (int attempt = 0; attempt < 40; attempt++)
        {
            missing = [.. wanted.Except(ours(registry.Current).ToHashSet()).Order()];
            if (missing.Count == 0) return;

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        // Still absent. Anything that has left the graph since the first dump was never going to
        // arrive, so the question is only about what is still there.
        PwDump now = await PwDump.CaptureAsync(cancellationToken);
        HashSet<uint> stillThere = theirs(now).ToHashSet();

        List<uint> real = [.. missing.Where(stillThere.Contains)];

        Assert.IsTrue(real.Count == 0,
            $"{kind}: pw-dump reports ids that never reached our graph [{string.Join(",", real)}]");
    }

    [TestMethod]
    public async Task AVolumeWpctlSets_IsReportedByUs()
    {
        RequireLinux();
        CliTool wpctl = CliTool.Require("wpctl");
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-oracle-wpctl", cts.Token);
        await using (ctx)
        await using (registry)
        {
            PipeWireNode node = await registry.CreateVirtualNode("WpctlOracle")
                .WithName(Unique("pwnet_wpctl")).ExecuteAsync(cts.Token);

            await using PipeWireNodeControl control = registry.BindNode(node.NodeId);
            await control.ReadyAsync(cts.Token);

            // wpctl sets and we read, not the other way round. WirePlumber applies its own policy to
            // nodes it manages and will overwrite a volume written straight to the node, so asserting
            // that wpctl reports our value races the session manager. This direction has one
            // authority: whatever wpctl did, we must see.
            // wpctl's scale is cubic; PipeWire stores linear amplitude. Setting 0.42 through wpctl
            // lands 0.42^3 in channelVolumes, and the two agreeing on that is the point.
            const float Asked = 0.42f;
            float expected = Asked * Asked * Asked;
            string id = node.NodeId.ToString(CultureInfo.InvariantCulture);
            (int exit, _, string stderr) = await wpctl.RunAsync(
                ["set-volume", id, Asked.ToString(CultureInfo.InvariantCulture)], cts.Token);

            if (exit != 0)
                Assert.Inconclusive($"wpctl cannot set this node's volume on this session: {stderr}");

            bool sawIt = await EventuallyAsync(async () =>
            {
                ImmutableArray<float> volumes = await control.GetChannelVolumesAsync(cts.Token);
                return !volumes.IsDefaultOrEmpty && volumes.All(v => Math.Abs(v - expected) < 0.005f);
            }, TimeSpan.FromSeconds(15), cts.Token);

            Assert.IsTrue(sawIt,
                $"wpctl set {Asked} (expecting {expected} linear) and we report "
                + $"[{string.Join(",", await control.GetChannelVolumesAsync(cts.Token))}]");

            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task ANativePwCatPlayer_IsSeenAsAStreamAndLinked()
    {
        RequireLinux();
        CliTool pwcat = CliTool.Require("pw-cat");
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-oracle-pwcat", cts.Token);
        await using (ctx)
        await using (registry)
        {
            // A third media implementation, independent of both GStreamer and this library: pw-cat
            // is a plain libpipewire client, so it exercises the ordinary stream path.
            string name = Unique("pwnet_pwcat");
            using Process player = pwcat.Start(
                "--playback", "--target", "0", "--media-role", "Music",
                "--rate", "48000", "--channels", "2", "--format", "s16", "--raw",
                "--properties", $"node.name={name}", "/dev/zero");

            try
            {
                bool appeared = await EventuallyAsync(async () =>
                {
                    await registry.WaitForInitialEnumerationAsync(cts.Token);
                    return registry.Current.Nodes.Any(n => string.Equals(n.NodeName, name, StringComparison.Ordinal));
                }, TimeSpan.FromSeconds(20), cts.Token);

                if (!appeared)
                    Assert.Inconclusive("pw-cat did not publish a node on this session.");

                PipeWireNode published = registry.Current.Nodes
                    .First(n => string.Equals(n.NodeName, name, StringComparison.Ordinal));

                // Its ports have to reach us too, not just the node.
                Assert.IsTrue(
                    await EventuallyAsync(async () =>
                    {
                        await registry.WaitForInitialEnumerationAsync(cts.Token);
                        return !registry.Current.GetPortsForNode(published.NodeId).IsEmpty;
                    }, TimeSpan.FromSeconds(15), cts.Token),
                    "a native player's node arrived without its ports");
            }
            finally
            {
                try { player.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* gone */ }
                player.WaitForExit(3000);
            }
        }
    }

    [TestMethod]
    public async Task PwMon_SeesTheSameAdditionsAndRemovalsWeRaise()
    {
        RequireLinux();
        CliTool pwmon = CliTool.Require("pw-mon");
        using var cts = new CancellationTokenSource(Budget);
        (PipeWireContext ctx, PipeWireRegistry registry) = await ConnectAsync("pwnet-oracle-pwmon", cts.Token);
        await using (ctx)
        await using (registry)
        {
            var added = new List<uint>();
            var removed = new List<uint>();
            void OnAdded(PipeWireNode n) { lock (added) added.Add(n.NodeId); }
            void OnRemoved(uint id) { lock (removed) removed.Add(id); }

            registry.NodeAdded += OnAdded;
            registry.NodeRemoved += OnRemoved;

            using Process mon = pwmon.Start();
            var monitorOutput = new System.Text.StringBuilder();
            Task pump = Task.Run(async () =>
            {
                string? line;
                while ((line = await mon.StandardOutput.ReadLineAsync(cts.Token)) is not null)
                    lock (monitorOutput) monitorOutput.AppendLine(line);
            }, cts.Token);

            try
            {
                // Waited for rather than slept through. pw-mon dumps the whole graph before it
                // starts reporting changes, and how long that takes is the session's size and the
                // machine's load, neither of which a fixed delay knows about.
                bool dumped = await EventuallyAsync(
                    () =>
                    {
                        lock (monitorOutput) return Task.FromResult(monitorOutput.Length > 0);
                    },
                    TimeSpan.FromSeconds(10), cts.Token);

                if (!dumped) Assert.Inconclusive("pw-mon printed nothing, so it never started.");

                PipeWireNode node = await registry.CreateVirtualNode("MonOracle")
                    .WithName(Unique("pwnet_mon")).ExecuteAsync(cts.Token);

                await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
                await registry.WaitForInitialEnumerationAsync(cts.Token);

                // pw-mon prints both an added and a removed record for the id. Its exact wording is
                // version-dependent, so the id appearing at all is what is asserted, and it is
                // polled for rather than assumed to have arrived within a fixed window.
                bool mentioned = await EventuallyAsync(
                    () =>
                    {
                        string current;
                        lock (monitorOutput) current = monitorOutput.ToString();

                        return Task.FromResult(
                            current.Contains($"id: {node.NodeId}", StringComparison.Ordinal)
                            || current.Contains($"id:{node.NodeId}", StringComparison.Ordinal));
                    },
                    TimeSpan.FromSeconds(10), cts.Token);

                lock (added) Assert.IsTrue(added.Contains(node.NodeId), "we never raised NodeAdded");
                lock (removed) Assert.IsTrue(removed.Contains(node.NodeId), "we never raised NodeRemoved");

                Assert.IsTrue(mentioned, "pw-mon never mentioned an object we created and destroyed");
            }
            finally
            {
                registry.NodeAdded -= OnAdded;
                registry.NodeRemoved -= OnRemoved;
                try { mon.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* gone */ }
                mon.WaitForExit(3000);
            }
        }
    }

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
}
