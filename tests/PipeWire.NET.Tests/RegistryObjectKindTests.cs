using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;
using PipeWire.NET.Interop;

namespace PipeWire.NET.Tests;

/// <summary>
/// The kinds beyond node, port and link: device, client, factory, module, metadata and the daemon
/// singletons. Parsing is pure, so all of this runs without a daemon.
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed unsafe class RegistryObjectKindTests
{
    // Property sets taken from a live session (pw-cli ls), not invented: a Device global really does
    // carry device.name and media.class, and a Module global really does carry only module.name.
    private static readonly (string Key, string? Value)[] DeviceProps =
    [
        ("object.serial", "50"), ("factory.id", "15"), ("client.id", "49"),
        ("device.api", "alsa"), ("device.description", "Radeon High Definition Audio Controller"),
        ("device.name", "alsa_card.pci-0000_e4_00.1"), ("device.nick", "HD-Audio Generic"),
        ("media.class", "Audio/Device"), ("object.path", "alsa:acp:Generic"),
    ];

    private static readonly (string Key, string? Value)[] ClientProps =
    [
        ("object.serial", "32"), ("module.id", "2"), ("pipewire.protocol", "protocol-native"),
        ("pipewire.sec.pid", "3046"), ("pipewire.sec.uid", "1000"), ("pipewire.sec.gid", "1000"),
        ("application.name", "xdg-desktop-portal"), ("pipewire.access", "portal"),
    ];

    [TestMethod]
    public void ADeviceGlobal_KeepsEveryPropertyAUiNeedsToNameIt()
    {
        using var dict = new NativeDict(DeviceProps);
        PipeWireDevice device = ParseDevice(dict, id: 50);

        Assert.AreEqual(50u, device.Id);
        Assert.AreEqual(PipeWireObjectKind.Device, device.Kind);
        Assert.AreEqual("alsa_card.pci-0000_e4_00.1", device.DeviceName);
        Assert.AreEqual("Radeon High Definition Audio Controller", device.Description);
        Assert.AreEqual("HD-Audio Generic", device.Nick);
        Assert.AreEqual("alsa", device.Api);
        Assert.AreEqual("Audio/Device", device.MediaClass);
        Assert.AreEqual("alsa:acp:Generic", device.ObjectPath);
        Assert.AreEqual(15u, device.FactoryId);
        Assert.AreEqual(49u, device.ClientId);
    }

    [TestMethod]
    public void AClientGlobal_SeparatesWhatTheDaemonMeasuredFromWhatTheAppClaimed()
    {
        using var dict = new NativeDict(ClientProps);
        PipeWireClient client = ParseClient(dict, id: 32);

        // Self-reported.
        Assert.AreEqual("xdg-desktop-portal", client.ApplicationName);
        // Read off the socket by the daemon, which is why these are the trustworthy ones.
        Assert.AreEqual(3046, client.ProcessId);
        Assert.AreEqual(1000u, client.UserId);
        Assert.AreEqual(1000u, client.GroupId);
        Assert.AreEqual("portal", client.Access);
        Assert.AreEqual("protocol-native", client.Protocol);
        Assert.AreEqual(2u, client.ModuleId);
    }

    [TestMethod]
    public void AGlobalWithNoPropertiesAtAll_StillParses()
    {
        // Metadata and the daemon singletons routinely arrive with an empty dictionary, and a
        // registry that dropped them would report a graph missing its default sink store.
        using var empty = new NativeDict();
        PipeWireDevice device = ParseDevice(empty, id: 1);
        PipeWireClient client = ParseClient(empty, id: 2);

        Assert.AreEqual(1u, device.Id);
        Assert.IsNull(device.DeviceName);
        Assert.IsNull(device.FactoryId);
        Assert.AreEqual(2u, client.Id);
        Assert.IsNull(client.ProcessId);
    }

    [TestMethod]
    public void ANumericPropertyThatIsNotANumber_CostsThatFieldAndNothingElse()
    {
        // The daemon is not trusted. A junk factory.id must not take the device with it.
        using var dict = new NativeDict(
            ("device.name", "alsa_card.junk"),
            ("factory.id", "not-a-number"),
            ("client.id", "-1"));

        PipeWireDevice device = ParseDevice(dict, id: 7);

        Assert.AreEqual("alsa_card.junk", device.DeviceName, "the readable fields must survive");
        Assert.IsNull(device.FactoryId);
        Assert.IsNull(device.ClientId, "a negative id is not a uint and must not wrap around");
    }

    [TestMethod]
    public void TheSnapshotSortsObjectsByKind_AndFindsAStoreByName()
    {
        var device = new PipeWireDevice(10, PipeWirePermissions.None, 3,
            "card", null, null, "alsa", "Audio/Device", null, null, null);
        var client = new PipeWireClient(11, PipeWirePermissions.None, 3,
            "firefox", 99, null, null, null, null, null);
        var settings = new PipeWireMetadataObject(12, PipeWirePermissions.None, 3, "settings");
        var defaults = new PipeWireMetadataObject(13, PipeWirePermissions.None, 3, "default");
        var core = new PipeWireCoreObject(0, PipeWirePermissions.None, 4, "pipewire-0", "1.6.8", null, null);

        var graph = new PipeWireGraphSnapshot(1, [], [], [], [device, client, settings, defaults, core]);

        CollectionAssert.AreEquivalent(new uint[] { 10 }, graph.Devices.Select(d => d.Id).ToArray());
        CollectionAssert.AreEquivalent(new uint[] { 11 }, graph.Clients.Select(c => c.Id).ToArray());
        CollectionAssert.AreEquivalent(new uint[] { 12, 13 }, graph.MetadataStores.Select(m => m.Id).ToArray());
        Assert.AreSame(core, graph.Core);
        Assert.IsNull(graph.Profiler, "the daemon in this graph has no profiler");

        Assert.AreSame(defaults, graph.GetMetadataStore("default"));
        Assert.IsNull(graph.GetMetadataStore("Default"), "store names are compared exactly");

        Assert.AreSame(device, graph.GetDevice(10));
        Assert.IsNull(graph.GetDevice(11), "a client id must not resolve as a device");
        Assert.IsTrue(graph.TryGetObject(11, out IPipeWireObject? found));
        Assert.AreSame(client, found);
    }

    [TestMethod]
    public void TheseObjectsDoNotDisplaceNodesPortsOrLinks_InLookups()
    {
        // Ids are unique across all kinds, so one index must not shadow another.
        var device = new PipeWireDevice(1, PipeWirePermissions.None, 3,
            "card", null, null, null, null, null, null, null);
        var graph = new PipeWireGraphSnapshot(
            1, [new(2, "node", null, null)], [], [], [device]);

        Assert.IsNull(graph.GetNode(1), "id 1 is a device, not a node");
        Assert.IsNotNull(graph.GetDevice(1));
        Assert.IsNotNull(graph.GetNode(2));
        Assert.IsNull(graph.GetDevice(2));
    }

    private static PipeWireDevice ParseDevice(NativeDict dict, uint id)
    {
        fixed (spa_dict* d = &dict.Dict)
            return PipeWireGlobalParser.ParseDevice(id, PipeWirePermissions.None, 3, d);
    }

    private static PipeWireClient ParseClient(NativeDict dict, uint id)
    {
        fixed (spa_dict* d = &dict.Dict)
            return PipeWireGlobalParser.ParseClient(id, PipeWirePermissions.None, 3, d);
    }
}
