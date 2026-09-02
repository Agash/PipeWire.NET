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

        // The point of modelling devices: a card in the graph should be reachable, named, and
        // describable, because that is what a profile switcher shows.
        foreach (PipeWireDevice device in graph.Devices)
        {
            Assert.AreSame(device, graph.GetDevice(device.Id));
            Assert.IsNotNull(device.DeviceName, $"device {device.Id} arrived without a name");
            Assert.IsNotNull(device.Api, $"device {device.Id} arrived without an api");
        }
    }
}
