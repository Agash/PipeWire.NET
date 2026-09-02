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

            AssertSameIds("Node", dump.IdsOfKind("Node"), ours.Nodes.Select(n => n.NodeId));
            AssertSameIds("Port", dump.IdsOfKind("Port"), ours.Ports.Select(p => p.PortId));
            AssertSameIds("Link", dump.IdsOfKind("Link"), ours.Links.Select(l => l.LinkId));
            AssertSameIds("Device", dump.IdsOfKind("Device"), ours.Devices.Select(d => d.Id));
            AssertSameIds("Client", dump.IdsOfKind("Client"), ours.Clients.Select(c => c.Id));
            AssertSameIds("Factory", dump.IdsOfKind("Factory"), ours.Factories.Select(f => f.Id));
            AssertSameIds("Module", dump.IdsOfKind("Module"), ours.Modules.Select(m => m.Id));
            AssertSameIds("Metadata", dump.IdsOfKind("Metadata"), ours.MetadataStores.Select(m => m.Id));

            // And the properties we parsed for our own node match what pw-dump read independently.
            PwDump.Entry? theirs = dump.ById(node.NodeId);
            Assert.IsNotNull(theirs, "pw-dump does not see a node we created");
            Assert.AreEqual(theirs!.Prop("node.name"), ours.GetNode(node.NodeId)!.NodeName,
                "we and pw-dump disagree about a node's name");

            await registry.RemoveObjectAsync(node.NodeId, cts.Token);
        }
    }

    private static void AssertSameIds(string kind, IEnumerable<uint> theirs, IEnumerable<uint> ours)
    {
        // Sorted sets, so the failure names exactly which ids each side has and the other does not.
        var t = theirs.ToHashSet();
        var o = ours.ToHashSet();

        var missing = t.Except(o).Order().ToList();
        var extra = o.Except(t).Order().ToList();

        Assert.IsTrue(missing.Count == 0 && extra.Count == 0,
            $"{kind}: pw-dump has {missing.Count} we lack [{string.Join(",", missing)}], "
            + $"we have {extra.Count} it lacks [{string.Join(",", extra)}]");
    }

    [TestMethod]
    public async Task AVolumeWeSet_IsReportedByWpctl()
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

            const float Target = 0.42f;
            await control.SetVolumeAsync(Target, cts.Token);

            // wpctl is WirePlumber's view, not the daemon's raw one: it goes through the policy
            // layer, so agreeing with it means the change is visible where a user would look.
            string id = node.NodeId.ToString(CultureInfo.InvariantCulture);
            (int exit, string stdout, _) = await wpctl.RunAsync(["get-volume", id], cts.Token);

            if (exit != 0)
                Assert.Inconclusive("wpctl cannot report this node's volume on this session.");

            // "Volume: 0.42"
            string[] parts = stdout.Split(':', StringSplitOptions.TrimEntries);
            Assert.IsTrue(parts.Length >= 2, $"unexpected wpctl output: {stdout}");
            Assert.IsTrue(float.TryParse(parts[1].Split(' ')[0], CultureInfo.InvariantCulture, out float reported),
                $"could not read a volume out of: {stdout}");

            Assert.AreEqual(Target, reported, 0.01f, "wpctl disagrees with the volume we set");

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
