using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Media.Streams;

namespace PipeWire.NET.Tests;

/// <summary>
/// The kinds beyond node, port and link, against a real daemon. The unit tests prove the parsing;
/// these prove the daemon actually sends what the parsing expects.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("RequiresDaemon")]
[SupportedOSPlatform("linux")]
public sealed class RegistryObjectKindLiveTests : PipeWireTestBase
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
    public async Task AModulesDetails_ArriveOnlyOnceItIsBound()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var context = new PipeWireContext("pwnet-module-details", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        // A module's registry global carries module.name and object.serial, and nothing else. The
        // rest is on the info event of a bound proxy, which is why these read as null until asked
        // for; a session always has modules, so there is nothing conditional about this.
        PipeWireModule bare = registry.Current.Modules.First(m => m.ModuleName is not null);
        Assert.IsNull(bare.Description, "a module's description is not in its registry global");
        Assert.IsNull(bare.Author);
        Assert.IsNull(bare.ModuleVersion);

        PipeWireModule full = await registry.ReadModuleDetailsAsync(bare.Id, cts.Token);

        Assert.AreEqual(bare.ModuleName, full.ModuleName, "it must still be the same module");
        Assert.IsNotNull(full.Description, "binding must have filled the description in");
        Assert.IsNotNull(full.Properties.GetValueOrDefault(PipeWireNames.ModuleFilename),
            "the module's filename is on the info event and nowhere else");

        Assert.AreSame(full, registry.Current.GetModule(bare.Id),
            "the graph must hold the enriched module, not just the caller");
        Assert.IsTrue(bare.IsStillIn(registry.Current),
            "enriching an object must not read as the id having been reused");
    }

    [TestMethod]
    public async Task EveryObjectInTheGraph_CarriesASerialThatIsUniqueToIt()
    {
        RequireLinux();
        using var cts = new CancellationTokenSource(Budget);

        await using var context = new PipeWireContext("pwnet-serials", ConsoleTestLoggerFactory.Instance);
        await context.StartAsync(cts.Token);
        await using var registry = new PipeWireRegistry(context);
        await registry.WaitForInitialEnumerationAsync(cts.Token);

        PipeWireGraphSnapshot graph = registry.Current;
        IPipeWireObject[] all =
        [
            .. graph.Nodes, .. graph.Ports, .. graph.Links,
            .. graph.Devices, .. graph.Clients, .. graph.Modules, .. graph.Factories,
        ];

        Assert.IsGreaterThan(0, all.Length, "an empty graph proves nothing");

        List<IPipeWireObject> without = [.. all.Where(o => o.ObjectSerial is null)];
        Assert.AreEqual(0, without.Count,
            "every global carries object.serial: "
            + string.Join(", ", without.Select(o => $"{o.Kind} {o.Id}")));

        // The point of the serial. Ids are unique among live objects too, so this only shows the
        // serial is usable as an identity; that it is not reused is what the id cannot promise.
        Assert.AreEqual(all.Length, all.Select(o => o.ObjectSerial).Distinct().Count(),
            "two live objects reported the same serial");
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
                $"the daemon refused to bind its profiler (permissions {profiler!.Permissions}): "
                + $"{e.GetType().Name}: {e.Message}");
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

        // The daemon only profiles a graph that is running, and an idle session drives nothing -
        // which is why waiting here used to end in a skip rather than an answer. Give it something
        // to drive: a producer and a consumer linked to each other keep the graph cycling for as
        // long as they are connected.
        await using var producer = new PipeWireAudioOutput(context, $"pwnet_profiler_drive_{Environment.ProcessId}");
        producer.FillSamples += (_, _, _, _, _) => 0;
        producer.Connect(autoConnect: false);

        for (int i = 0; i < 100 && producer.NodeId is null; i++)
        {
            await Task.Delay(50, cts.Token);
            await registry.WaitForInitialEnumerationAsync(cts.Token);
        }

        Assert.IsNotNull(producer.NodeId, "the driving producer never reached the graph");

        await using var consumer = new PipeWireAudioCapture(context, $"pwnet_profiler_sink_{Environment.ProcessId}");
        consumer.FrameReady += (_, _) => { };
        consumer.Connect(producer.NodeId!.Value);

        // Shorter than the class budget, so running out of patience is reported as such rather
        // than arriving as the budget's own cancellation.
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        Assert.IsTrue(reports.TryDequeue(out Spa.SpaObject? first));
        Assert.AreEqual(Spa.SpaType.ObjectProfiler, first!.ObjectType,
            "a profiler report is a Profiler object");
        Assert.IsTrue(first.Properties.Length > 0, "a report with no properties says nothing");
        }
    }
}
