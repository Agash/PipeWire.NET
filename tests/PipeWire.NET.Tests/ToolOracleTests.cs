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
            PipeWireNode node = await registry.CreateVirtualStereoNode("Oracle")
                .WithName(Unique("pwnet_oracle")).ExecuteAsync(cts.Token);

            // Both views are taken after the same barrier, so neither is a moving target - and the
            // comparison is by id, which is the only thing both sides agree to call an object.
            await registry.WaitForInitialEnumerationAsync(cts.Token);
            PwDump dump = await PwDump.CaptureAsync(cts.Token);
            PipeWireGraphSnapshot ours = registry.Current;

            AssertWeSeeAll("Node", dump.IdsOfKind("Node"), ours.Nodes.Select(n => n.NodeId));
            AssertWeSeeAll("Port", dump.IdsOfKind("Port"), ours.Ports.Select(p => p.PortId));
            AssertWeSeeAll("Link", dump.IdsOfKind("Link"), ours.Links.Select(l => l.LinkId));
            AssertWeSeeAll("Device", dump.IdsOfKind("Device"), ours.Devices.Select(d => d.Id));
            // Clients are compared as a superset: pw-dump connects to produce the dump, so its own
            // client is in its output and cannot be in a snapshot taken before it ran.
            var theirClients = dump.IdsOfKind("Client").ToHashSet();
            var ourClients = ours.Clients.Select(c => c.Id).ToHashSet();
            var weLack = ourClients.Except(theirClients).ToList();
            Assert.IsTrue(weLack.Count == 0,
                $"Client: we report ids pw-dump does not have [{string.Join(",", weLack)}]");
            // pw-dump connects in order to produce the dump, so its own client is in its output and
            // cannot be in a snapshot taken before it ran.
            AssertWeSeeAll(
                "Client",
                dump.OfKind("Client")
                    .Where(e => !string.Equals(e.Prop("application.name"), "pw-dump", StringComparison.Ordinal))
                    .Select(e => e.Id),
                ours.Clients.Select(c => c.Id));
            AssertWeSeeAll("Factory", dump.IdsOfKind("Factory"), ours.Factories.Select(f => f.Id));
            AssertWeSeeAll("Module", dump.IdsOfKind("Module"), ours.Modules.Select(m => m.Id));
            AssertWeSeeAll("Metadata", dump.IdsOfKind("Metadata"), ours.MetadataStores.Select(m => m.Id));

            // And the properties we parsed for our own node match what pw-dump read independently.
            PwDump.Entry? theirs = dump.ById(node.NodeId);
            Assert.IsNotNull(theirs, "pw-dump does not see a node we created");
            Assert.AreEqual(theirs!.Prop("node.name"), ours.GetNode(node.NodeId)!.NodeName,
                "we and pw-dump disagree about a node's name");

            await registry.RemoveObjectAsync(node.NodeId, cts.Token);
        }
    }

    /// <summary>Everything pw-dump reports must be in our graph.</summary>
    /// <remarks>
    /// A containment check, not equality. pw-dump binds each object to describe it and omits the
    /// ones it cannot, while the registry reports every global it is told about, so our side is
    /// legitimately the larger of the two. Missing one is the defect worth catching.
    /// </remarks>
    private static void AssertWeSeeAll(string kind, IEnumerable<uint> theirs, IEnumerable<uint> ours)
    {
        var missing = theirs.ToHashSet().Except(ours.ToHashSet()).Order().ToList();

        Assert.IsTrue(missing.Count == 0,
            $"{kind}: pw-dump reports ids we never saw [{string.Join(",", missing)}]");
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
            PipeWireNode node = await registry.CreateVirtualStereoNode("WpctlOracle")
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

            await registry.RemoveObjectAsync(node.NodeId, cts.Token);
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
                await Task.Delay(500, cts.Token);   // let pw-mon finish its initial dump

                PipeWireNode node = await registry.CreateVirtualStereoNode("MonOracle")
                    .WithName(Unique("pwnet_mon")).ExecuteAsync(cts.Token);

                await registry.RemoveObjectAsync(node.NodeId, cts.Token);
                await registry.WaitForInitialEnumerationAsync(cts.Token);
                await Task.Delay(500, cts.Token);   // and to report the removal

                lock (added) Assert.IsTrue(added.Contains(node.NodeId), "we never raised NodeAdded");
                lock (removed) Assert.IsTrue(removed.Contains(node.NodeId), "we never raised NodeRemoved");

                // pw-mon prints both an added and a removed record for the id. Its exact wording is
                // version-dependent, so the id appearing at all is what is asserted.
                string text;
                lock (monitorOutput) text = monitorOutput.ToString();

                Assert.IsTrue(
                    text.Contains($"id: {node.NodeId}", StringComparison.Ordinal)
                    || text.Contains($"id:{node.NodeId}", StringComparison.Ordinal),
                    "pw-mon never mentioned an object we created and destroyed");
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
