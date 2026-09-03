using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// The kinds beyond node, port and link, against a real daemon. The unit tests prove the parsing;
/// these prove the daemon actually sends what the parsing expects.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class RegistryObjectKindLiveTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("PipeWire is a Linux daemon.");
    }

    [TestMethod]
    public async Task ARealSession_ReportsTheObjectKindsItsGraphIsBuiltFrom()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var context = new PipeWireContext("pwnet-kinds", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireGraphSnapshot graph = registry.Current;

        // Every daemon has these three, whatever else the session is running: it cannot start
        // without a core, cannot serve us without a client, and cannot create anything without a
        // factory. Devices and metadata stores depend on the hardware and the session manager.
        Assert.IsNotNull(graph.Core, "the daemon must report its core object");
        Assert.IsTrue(graph.Clients.Length > 0, "this connection is itself a client");
        Assert.IsTrue(graph.Factories.Length > 0, "the daemon must expose the factories it creates with");
        Assert.IsTrue(graph.Modules.Length > 0, "protocol-native alone is a module");

        // The factory names the library hardcodes when creating objects have to be among them, or
        // creation would fail at runtime on this daemon.
        string?[] factories = [.. graph.Factories.Select(static f => f.FactoryName)];
        CollectionAssert.Contains(factories, "adapter");
        CollectionAssert.Contains(factories, "link-factory");
    }

    [TestMethod]
    public async Task EveryObjectTheRegistryReports_ResolvesBackToItselfById()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var context = new PipeWireContext("pwnet-kinds-ids", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireGraphSnapshot graph = registry.Current;

        // Ids are unique across kinds, so a lookup must never answer with the wrong one. Doing this
        // over a live graph is what catches an index built from the wrong collection.
        foreach (IPipeWireObject expected in graph.Objects)
        {
            Assert.IsTrue(graph.TryGetObject(expected.Id, out IPipeWireObject? found),
                $"{expected.Kind} {expected.Id} is in the graph but does not resolve by id");
            Assert.AreSame(expected, found);
            Assert.IsNull(graph.GetNode(expected.Id), $"{expected.Kind} {expected.Id} resolved as a node");
            Assert.IsNull(graph.GetPort(expected.Id), $"{expected.Kind} {expected.Id} resolved as a port");
            Assert.IsNull(graph.GetLink(expected.Id), $"{expected.Kind} {expected.Id} resolved as a link");
        }

        foreach (PipeWireNode node in graph.Nodes)
            Assert.IsNull(graph.GetDevice(node.NodeId), $"node {node.NodeId} resolved as a device");
    }

    [TestMethod]
    public async Task ADeviceBackedNode_NamesADeviceThatIsInTheGraph()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var context = new PipeWireContext("pwnet-kinds-dev", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireGraphSnapshot graph = registry.Current;
        if (graph.Devices.Length == 0)
            Assert.Inconclusive("this session has no hardware devices to check against.");

        foreach (PipeWireDevice device in graph.Devices)
        {
            Assert.AreSame(device, graph.GetDevice(device.Id));
            Assert.IsNotNull(device.DeviceName, $"device {device.Id} arrived without a name");
            Assert.IsNotNull(device.Api, $"device {device.Id} arrived without an api");
        }
    }

    [TestMethod]
    public async Task TheProfiler_CanBeBoundAndReportsTheGraphsTimings()
    {
        // Binding is what makes the daemon start producing reports, so there is nothing to observe
        // until a client asks. Each one is a Profiler object carrying a cycle's timings, which is
        // what pw-top renders.
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var context = new PipeWireContext("pwnet-profiler", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireProfiler? profiler = registry.Current.Profiler;
        if (profiler is null) Assert.Inconclusive("this daemon was built without the profiler.");

        Assert.ThrowsExactly<ArgumentException>(() => registry.BindProfiler(uint.MaxValue),
            "an id that is not the profiler must be refused rather than bound");

        var reports = new System.Collections.Concurrent.ConcurrentQueue<Spa.SpaObject>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        PipeWireProfilerReader reader;
        try
        {
            reader = registry.BindProfiler(profiler!.Id);
        }
        catch (Exception e) when (e is InvalidOperationException or PipeWireException)
        {
            // Whether an ordinary client may bind the profiler is the daemon's policy, and it
            // refuses on a session that reserves it for something else. Its answer is not this
            // library's contract.
            Assert.Inconclusive(
                $"the daemon refused to bind its profiler; it reports permissions {profiler!.Permissions}.");
            return;
        }

        await using (reader)
        {
        reader.ProfileReceived += (_, report) =>
        {
            reports.Enqueue(report);
            arrived.TrySetResult();
        };

        Assert.AreEqual(profiler.Id, reader.Id);

        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        }
        catch (TimeoutException)
        {
            // A session with nothing running has no cycles to report on, and whether this one does
            // is the machine's business rather than this library's.
            Assert.Inconclusive("the daemon produced no profiler report within 10s.");
        }

        Assert.IsTrue(reports.TryDequeue(out Spa.SpaObject? first));
        Assert.AreEqual(Spa.SpaType.ObjectProfiler, first!.ObjectType,
            "a profiler report is a Profiler object");
        Assert.IsTrue(first.Properties.Length > 0, "a report with no properties says nothing");
        }
    }
}
