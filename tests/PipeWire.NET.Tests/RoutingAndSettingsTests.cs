using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// The routing keys a node is created with, and the settings store's clock accessors.
/// </summary>
/// <remarks>
/// The key names are the whole risk here. They are strings the daemon either recognises or silently
/// ignores, and they have moved between releases - <c>node.target</c> was replaced by
/// <c>target.object</c> in 0.3.64. So these check the properties that actually reach the daemon
/// rather than only that a builder method exists.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class RoutingAndSettingsTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    private static string Unique(string prefix) =>
        $"{prefix}_{Environment.ProcessId}_{Random.Shared.Next():x}";

    [TestMethod]
    public async Task ANodeCreatedWithATarget_CarriesTargetObjectAndNotTheDeprecatedKey()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-target", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        string sinkName = Unique("pwnet_target_sink");
        PipeWireNode sink = await registry.CreateVirtualNode("Target sink")
            .WithName(sinkName).ExecuteAsync(cts.Token);

        PipeWireNode source = await registry.CreateVirtualSource("Targeting source")
            .WithName(Unique("pwnet_target_src"))
            .WithTarget(sink)
            .WithAutoConnect(false)
            .ExecuteAsync(cts.Token);

        try
        {
            // Read back through pw-dump rather than through our own model: the question is what
            // the daemon received, and PipeWireNode carries a fixed set of properties rather than
            // the whole dictionary, so it cannot answer it.
            PwDump dump = await PwDump.CaptureAsync(cts.Token);
            PwDump.Entry? seen = dump.OfKind("Node")
                .FirstOrDefault(e => e.Id == source.NodeId);

            Assert.IsNotNull(seen, "the node this test made is not in pw-dump's graph");
            Assert.AreEqual(sinkName, seen!.Prop("target.object"),
                "target.object did not reach the daemon");
            Assert.AreEqual("false", seen.Prop("node.autoconnect"),
                "node.autoconnect did not reach the daemon");

            // Deprecated since 0.3.64. Sending both leaves which one wins to the session manager.
            Assert.IsNull(seen.Prop("node.target"),
                "the deprecated node.target key was sent as well");
        }
        finally
        {
            await registry.DestroyGlobalAsync(source.NodeId, cts.Token);
            await registry.DestroyGlobalAsync(sink.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task AVirtualSource_IsCreatedAsSomethingToCaptureFrom()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-vsource", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireNode node = await registry.CreateVirtualSource("A virtual microphone")
            .WithName(Unique("pwnet_vsource")).ExecuteAsync(cts.Token);

        try
        {
            PipeWireNode? seen = registry.Current.GetNode(node.NodeId);
            Assert.IsNotNull(seen);
            Assert.AreEqual("Audio/Source", seen!.MediaClass);

            // The distinction the preset exists for: a source is read from, a sink is played into,
            // and the flow is what a caller routes on.
            Assert.AreEqual(PipeWireMediaFlow.Source, seen.Flow);
        }
        finally
        {
            await registry.DestroyGlobalAsync(node.NodeId, cts.Token);
        }
    }

    [TestMethod]
    public async Task TheSettingsStore_ReportsTheClockAsIntegersRatherThanJson()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-settings", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireMetadataStore? settings = registry.BindMetadataStore("settings");
        if (settings is null)
            Assert.Inconclusive("this session has no settings store.");

        await using (settings)
        {
            await settings!.ReadyAsync(cts.Token);

            // Unlike the default-device keys these are bare integers with no type, so a reader that
            // assumed JSON would return null for every one of them.
            Assert.IsNotNull(settings.ClockRate, "clock.rate did not parse as an integer");
            Assert.IsTrue(settings.ClockRate > 0, $"clock.rate reads {settings.ClockRate}");

            Assert.IsNotNull(settings.ClockQuantum);
            Assert.IsTrue(settings.ClockQuantum > 0);

            Assert.IsNotNull(settings.ClockMinQuantum);
            Assert.IsNotNull(settings.ClockMaxQuantum);
            Assert.IsTrue(settings.ClockMinQuantum <= settings.ClockMaxQuantum,
                "the quantum range is inverted");

            // Present and readable even when nothing is pinned, where they read 0 rather than being
            // absent - which is why they are int? for absence and 0 for "not pinned".
            Assert.IsNotNull(settings.ClockForcedRate);
            Assert.IsNotNull(settings.ClockForcedQuantum);

            // Negative is not a value the daemon has a meaning for, and atoi would take it and
            // store it, so it is refused here rather than written.
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => _ = settings.SetForcedQuantumAsync(-1, cts.Token));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => _ = settings.SetForcedRateAsync(-1, cts.Token));
        }
    }

    [TestMethod]
    public async Task PinningTheQuantumAndReleasingIt_LeavesTheGraphAsItWasFound()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-quantum", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireMetadataStore? settings = registry.BindMetadataStore("settings");
        if (settings is null)
            Assert.Inconclusive("this session has no settings store.");

        await using (settings)
        {
            await settings!.ReadyAsync(cts.Token);

            int before = settings.ClockForcedQuantum ?? 0;
            if (before != 0)
                Assert.Inconclusive($"this session already pins its quantum at {before}.");

            // Inside the daemon's own range, so the write is one it accepts. Affects every client on
            // the machine, so it is released again immediately.
            int min = settings.ClockMinQuantum ?? 32;
            int max = settings.ClockMaxQuantum ?? 8192;
            int want = Math.Clamp(1024, min, max);

            try
            {
                await settings.SetForcedQuantumAsync(want, cts.Token);
                Assert.AreEqual(want, settings.ClockForcedQuantum, "the quantum did not take");
            }
            finally
            {
                await settings.SetForcedQuantumAsync(0, CancellationToken.None);
            }

            Assert.AreEqual(0, settings.ClockForcedQuantum, "the quantum was left pinned");
        }
    }

    [TestMethod]
    public async Task AQuantumTheDaemonRejects_IsAcceptedAndIgnoredRatherThanRefused()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);
        await using var ctx = new PipeWireContext("pwnet-badquantum", ConsoleTestLoggerFactory.Instance);
        await ctx.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(ctx);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireMetadataStore? settings = registry.BindMetadataStore("settings");
        if (settings is null)
            Assert.Inconclusive("this session has no settings store.");

        await using (settings)
        {
            await settings!.ReadyAsync(cts.Token);

            if ((settings.ClockForcedQuantum ?? 0) != 0)
                Assert.Inconclusive("this session already pins its quantum.");

            int max = settings.ClockMaxQuantum ?? 8192;

            // The behaviour the accessor's remarks warn about, pinned as a test so it cannot change
            // quietly: out of range, the write succeeds and the daemon logs at info. There is no
            // error to surface, which is why reading it back is the only way to know.
            try
            {
                await settings.SetForcedQuantumAsync(max * 100, cts.Token);
                Assert.AreEqual(0, settings.ClockForcedQuantum,
                    "an out-of-range quantum was applied, so the daemon's check has changed");
            }
            finally
            {
                await settings.SetForcedQuantumAsync(0, CancellationToken.None);
            }
        }
    }

}
