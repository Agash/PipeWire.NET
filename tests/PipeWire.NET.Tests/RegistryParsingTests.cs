using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Interop;
using PipeWire.NET.Spa;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Drives the global parser with property dictionaries a daemon would never send.
/// </summary>
/// <remarks>
/// A well-behaved daemon always supplies <c>node.id</c> and <c>port.direction</c>, so the skip paths
/// that handle their absence are unreachable from an integration test - which is exactly why they
/// are worth exercising directly. Parsing is pure, so none of this needs a daemon or a registry: a
/// malformed global must be dropped, never half-built, and never read past its buffer.
/// </remarks>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class RegistryParsingTests : PipeWireTestBase
{

    // ------------------------------------------------------------------ ports

    [TestMethod]
    public unsafe void APortWithNoNodeId_IsSkippedWithAReason()
    {
        using var dict = new NativeDict(("port.direction", "out"), ("port.name", "orphan"));
        fixed (spa_dict* d = &dict.Dict)
        {
            Assert.IsFalse(PipeWireGlobalParser.TryParsePort(
                42, PipeWirePermissions.Read, 3, PipeWireProperties.From(d),
                out PipeWirePort? port, out string reason, out _),
                "a port with no owning node cannot be filed anywhere");

            Assert.IsNull(port, "a refused parse must not produce a half-built port");
            StringAssert.Contains(reason, "node id", "the reason must name what was wrong");
        }
    }

    [TestMethod]
    [DataRow("not-a-number")]
    [DataRow("")]
    [DataRow("-1")]
    [DataRow("99999999999999999999")]   // overflows uint
    [DataRow("12abc")]
    public unsafe void APortWithAnUnparseableNodeId_IsSkipped(string nodeId)
    {
        using var dict = new NativeDict(("node.id", nodeId), ("port.direction", "out"));
        fixed (spa_dict* d = &dict.Dict)
            Assert.IsFalse(
                PipeWireGlobalParser.TryParsePort(42, PipeWirePermissions.Read, 3, PipeWireProperties.From(d), out _, out _, out _),
                $"node.id '{nodeId}' should not have parsed");
    }

    [TestMethod]
    [DataRow(" 7")]
    [DataRow("7 ")]
    [DataRow("+7")]
    public unsafe void ANodeIdWithSurroundingWhitespace_IsAcceptedDeliberately(string nodeId)
    {
        // uint.TryParse defaults to NumberStyles.Integer, which permits leading and trailing
        // whitespace and a leading sign. Being strict here would make a port vanish from the graph
        // over a stray space, which is a worse outcome than reading the id it plainly states.
        using var dict = new NativeDict(("node.id", nodeId), ("port.direction", "out"));
        PipeWirePort? port;
        fixed (spa_dict* d = &dict.Dict)
            Assert.IsTrue(PipeWireGlobalParser.TryParsePort(
                42, PipeWirePermissions.Read, 3, PipeWireProperties.From(d), out port, out _, out _),
                $"node.id '{nodeId}' states 7 clearly enough");

        Assert.AreEqual(7u, port!.NodeId);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("sideways")]
    [DataRow("In")]        // casing matters
    [DataRow("0")]         // Enum.TryParse would have taken this as In
    [DataRow("out ")]      // trailing space is a different value
    public unsafe void APortWithAnUnusableDirection_IsSkipped(string? direction)
    {
        (string, string?)[] pairs = direction is null
            ? [("node.id", "7")]
            : [("node.id", "7"), ("port.direction", direction)];

        using var dict = new NativeDict(pairs);
        fixed (spa_dict* d = &dict.Dict)
        {
            Assert.IsFalse(PipeWireGlobalParser.TryParsePort(
                42, PipeWirePermissions.Read, 3, PipeWireProperties.From(d), out _, out string reason, out _),
                $"direction '{direction}' should not have parsed");
            StringAssert.Contains(reason, "direction");
        }
    }

    [TestMethod]
    [DataRow("in", PipeWirePortDirection.In)]
    [DataRow("out", PipeWirePortDirection.Out)]
    [DataRow("control", PipeWirePortDirection.Control)]
    [DataRow("notify", PipeWirePortDirection.Notify)]
    public unsafe void AWellFormedPort_ParsesEveryField(string direction, PipeWirePortDirection expected)
    {
        using var dict = new NativeDict(
            ("node.id", "7"), ("port.direction", direction),
            ("port.name", "capture_FL"), ("port.monitor", "true"), ("port.id", "3"),
            ("port.control", "true"), ("format.dsp", "32 bit float mono audio"),
            ("audio.channel", "FL"), ("port.alias", "alsa:capture_FL"), ("port.group", "stream.0"));

        PipeWirePort? port;
        fixed (spa_dict* d = &dict.Dict)
            Assert.IsTrue(PipeWireGlobalParser.TryParsePort(
                42, PipeWirePermissions.Read | PipeWirePermissions.Write, 3, PipeWireProperties.From(d), out port, out _, out _));

        Assert.IsNotNull(port);
        Assert.AreEqual(42u, port!.PortId);
        Assert.AreEqual(7u, port.NodeId);
        Assert.AreEqual(expected, port.PortDirection);
        Assert.AreEqual("capture_FL", port.PortName);
        Assert.IsTrue(port.Monitor);
        Assert.AreEqual(3u, port.PortIndex);
        Assert.IsTrue(port.IsControl);
        Assert.AreEqual("32 bit float mono audio", port.DspFormat);
        Assert.AreEqual("FL", port.AudioChannel);
        Assert.AreEqual("alsa:capture_FL", port.Alias);
        Assert.AreEqual("stream.0", port.PortGroup);
        Assert.AreEqual(3u, port.InterfaceVersion);
        Assert.IsTrue(port.Permissions.HasFlag(PipeWirePermissions.Write));
    }

    [TestMethod]
    public unsafe void AControlPortSpeltEitherWay_ReadsAsAControlPort()
    {
        // PipeWire's own adapter control ports set port.control and keep the direction as in or
        // out, so reading the direction alone misses exactly the ports that matter. The JACK-style
        // direction is the other spelling and has to count too.
        using var flagged = new NativeDict(("node.id", "7"), ("port.direction", "in"), ("port.control", "true"));
        using var directed = new NativeDict(("node.id", "7"), ("port.direction", "control"));
        using var neither = new NativeDict(("node.id", "7"), ("port.direction", "in"));

        PipeWirePort? a, b, c;
        fixed (spa_dict* d = &flagged.Dict)
            PipeWireGlobalParser.TryParsePort(1, PipeWirePermissions.None, 3, PipeWireProperties.From(d), out a, out _, out _);
        fixed (spa_dict* d = &directed.Dict)
            PipeWireGlobalParser.TryParsePort(2, PipeWirePermissions.None, 3, PipeWireProperties.From(d), out b, out _, out _);
        fixed (spa_dict* d = &neither.Dict)
            PipeWireGlobalParser.TryParsePort(3, PipeWirePermissions.None, 3, PipeWireProperties.From(d), out c, out _, out _);

        Assert.IsTrue(a!.IsControl, "port.control must count");
        Assert.IsTrue(b!.IsControl, "port.direction=control must count");
        Assert.IsFalse(c!.IsControl);
        Assert.AreEqual(PipeWirePortDirection.In, a.PortDirection,
            "a control port keeps the direction it reported");
    }

    [TestMethod]
    public unsafe void EveryObjectCarriesItsSerialAndItsRawProperties()
    {
        // object.serial is on every global and is never reused, unlike the id. Anything the
        // library does not model has to stay reachable, or a caller is stuck until a release.
        using var dict = new NativeDict(
            ("node.name", "n"), ("object.serial", "8814"), ("some.module.key", "kept"));

        PipeWireNode node;
        fixed (spa_dict* d = &dict.Dict)
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.None, 3, PipeWireProperties.From(d));

        Assert.AreEqual(8814ul, node.ObjectSerial);
        Assert.AreEqual("kept", node.Properties["some.module.key"]);
        Assert.AreEqual("n", node.Properties[PipeWireNames.NodeName],
            "a modelled property stays readable through the dictionary too");
        Assert.IsNull(node.Properties.GetValueOrDefault("not.sent"));
    }

    [TestMethod]
    public unsafe void APortWithOnlyItsMandatoryProperties_StillParses()
    {
        using var dict = new NativeDict(("node.id", "7"), ("port.direction", "in"));
        PipeWirePort? port;
        fixed (spa_dict* d = &dict.Dict)
            Assert.IsTrue(PipeWireGlobalParser.TryParsePort(
                42, PipeWirePermissions.None, 3, PipeWireProperties.From(d), out port, out _, out _));

        Assert.IsNotNull(port);
        Assert.IsNull(port!.PortName);
        Assert.IsFalse(port.Monitor, "an absent port.monitor is false, matching spa_atob");
        Assert.IsNull(port.PortIndex);
        Assert.IsFalse(port.IsControl);
    }

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("1", true)]
    [DataRow("false", false)]
    [DataRow("0", false)]
    [DataRow("True", false)]      // spa_atob is ordinal
    [DataRow("yes", false)]
    public unsafe void PortBooleans_FollowSpaAtobExactly(string raw, bool expected)
    {
        using var dict = new NativeDict(
            ("node.id", "7"), ("port.direction", "in"), ("port.monitor", raw));

        PipeWirePort? port;
        fixed (spa_dict* d = &dict.Dict)
            Assert.IsTrue(PipeWireGlobalParser.TryParsePort(
                42, PipeWirePermissions.None, 3, PipeWireProperties.From(d), out port, out _, out _));

        Assert.AreEqual(expected, port!.Monitor, $"port.monitor '{raw}'");
    }

    // ------------------------------------------------------------------ links

    [TestMethod]
    [DataRow("link.output.node")]
    [DataRow("link.output.port")]
    [DataRow("link.input.node")]
    [DataRow("link.input.port")]
    public unsafe void ALinkMissingAnyEndpoint_IsSkippedNamingThatEndpoint(string omit)
    {
        var all = new Dictionary<string, string?>
        {
            ["link.output.node"] = "1",
            ["link.output.port"] = "2",
            ["link.input.node"] = "3",
            ["link.input.port"] = "4",
        };
        all.Remove(omit);

        using var dict = new NativeDict([.. all.Select(kv => (kv.Key, kv.Value))]);
        fixed (spa_dict* d = &dict.Dict)
        {
            Assert.IsFalse(PipeWireGlobalParser.TryParseLink(
                99, PipeWirePermissions.Read, 3, PipeWireProperties.From(d),
                out PipeWireLink? link, out string reason, out _),
                $"a link without {omit} describes no route");

            Assert.IsNull(link);
            StringAssert.Contains(reason, omit, "the reason must name the missing key");
        }
    }

    [TestMethod]
    public unsafe void ALinkWithAnUnparseableEndpoint_IsSkipped()
    {
        using var dict = new NativeDict(
            ("link.output.node", "1"), ("link.output.port", "not-a-number"),
            ("link.input.node", "3"), ("link.input.port", "4"));

        fixed (spa_dict* d = &dict.Dict)
            Assert.IsFalse(PipeWireGlobalParser.TryParseLink(
                99, PipeWirePermissions.Read, 3, PipeWireProperties.From(d), out _, out _, out string? offending));
    }

    [TestMethod]
    public unsafe void AWellFormedLink_KeepsItsEndpointsInTheRightSlots()
    {
        using var dict = new NativeDict(
            ("link.output.node", "1"), ("link.output.port", "2"),
            ("link.input.node", "3"), ("link.input.port", "4"));

        PipeWireLink? link;
        fixed (spa_dict* d = &dict.Dict)
            Assert.IsTrue(PipeWireGlobalParser.TryParseLink(
                99, PipeWirePermissions.Read, 3, PipeWireProperties.From(d), out link, out _, out _));

        Assert.IsNotNull(link);
        Assert.AreEqual(99u, link!.LinkId);
        // Deliberately asymmetric values: equal ones would hide a swapped pair.
        Assert.AreEqual(1u, link.LinkOutputNode);
        Assert.AreEqual(2u, link.LinkOutputPort);
        Assert.AreEqual(3u, link.LinkInputNode);
        Assert.AreEqual(4u, link.LinkInputPort);
    }

    // ------------------------------------------------------------------ nodes

    [TestMethod]
    public unsafe void ANodeWithNoPropertiesAtAll_StillParses()
    {
        // A node carries no mandatory properties, so an empty dict must still produce one: refusing
        // would hide the node from the graph entirely.
        using var dict = new NativeDict();
        PipeWireNode node;
        fixed (spa_dict* d = &dict.Dict)
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, PipeWireProperties.From(d));

        Assert.AreEqual(5u, node.NodeId);
        Assert.IsNull(node.NodeName);
        Assert.IsNull(node.MediaClass);
        Assert.AreEqual(PipeWireMediaKind.Unknown, node.Media);
        Assert.AreEqual(PipeWireMediaFlow.Unknown, node.Flow);
    }

    [TestMethod]
    public unsafe void ANodeCarriesEveryPropertyItWasGiven()
    {
        using var dict = new NativeDict(
            ("node.name", "alsa_output.pci"), ("node.description", "Speakers"),
            ("node.nick", "Spk"), ("media.class", "Audio/Sink"));

        PipeWireNode node;
        fixed (spa_dict* d = &dict.Dict)
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, PipeWireProperties.From(d));

        Assert.AreEqual("alsa_output.pci", node.NodeName);
        Assert.AreEqual("Speakers", node.Description);
        Assert.AreEqual("Spk", node.NodeNick);
        Assert.AreEqual("Audio/Sink", node.MediaClass);
        Assert.AreEqual(PipeWireMediaKind.Audio, node.Media);
        Assert.AreEqual(PipeWireMediaFlow.Sink, node.Flow);
    }

    [TestMethod]
    public unsafe void ANodeADeviceProvides_NamesThatDevice()
    {
        using var dict = new NativeDict(("device.id", "42"), ("media.class", "Audio/Sink"));
        PipeWireNode node;
        fixed (spa_dict* d = &dict.Dict)
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, PipeWireProperties.From(d));

        Assert.AreEqual(42u, node.DeviceId);
    }

    [TestMethod]
    public unsafe void ANodeNoDeviceProvides_ReportsNoDevice()
    {
        // An application stream has no device.id at all, and a malformed one is no better than a
        // missing one: guessing an owner would attribute a stream to a card that never made it.
        using var dict = new NativeDict(("media.class", "Stream/Output/Audio"));
        PipeWireNode stream;
        fixed (spa_dict* d = &dict.Dict)
            stream = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, PipeWireProperties.From(d));

        using var garbage = new NativeDict(("device.id", "not-a-number"));
        PipeWireNode unparsable;
        fixed (spa_dict* d = &garbage.Dict)
            unparsable = PipeWireGlobalParser.ParseNode(6, PipeWirePermissions.Read, 3, PipeWireProperties.From(d));

        Assert.IsNull(stream.DeviceId);
        Assert.IsNull(unparsable.DeviceId);
    }

    [TestMethod]
    public unsafe void APropertyWithANullValue_ReadsAsAbsentAndDoesNotStopTheRest()
    {
        // spa_dict_item.value may be null. That must not be dereferenced, and must not abandon the
        // remaining properties.
        using var dict = new NativeDict(("node.name", null), ("media.class", "Audio/Sink"));
        PipeWireNode node;
        fixed (spa_dict* d = &dict.Dict)
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, PipeWireProperties.From(d));

        Assert.IsNull(node.NodeName);
        Assert.AreEqual("Audio/Sink", node.MediaClass, "later properties must still be read");
    }

    [TestMethod]
    public unsafe void AKeyThatIsAPrefixOfAnother_DoesNotMatchIt()
    {
        // "node.name" must not be found by a lookup for "node.nick", nor satisfy a prefix compare.
        using var dict = new NativeDict(("node.n", "short"), ("node.name", "full"));
        PipeWireNode node;
        fixed (spa_dict* d = &dict.Dict)
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, PipeWireProperties.From(d));

        Assert.AreEqual("full", node.NodeName, "a prefix key must not satisfy the lookup");
        Assert.IsNull(node.NodeNick);
    }

    [TestMethod]
    public unsafe void DuplicateKeys_ResolveToTheFirstMatch()
    {
        // spa_dict does not forbid duplicates; whichever wins, it must be deterministic.
        using var dict = new NativeDict(("node.name", "first"), ("node.name", "second"));
        PipeWireNode node;
        fixed (spa_dict* d = &dict.Dict)
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, PipeWireProperties.From(d));

        Assert.AreEqual("first", node.NodeName);
    }

    // ------------------------------------------------------------------ hostile dictionaries

    [TestMethod]
    public unsafe void AnEmptyDictAndALyingItemCount_AreBothTolerated()
    {
        var empty = new spa_dict { flags = 0, n_items = 0, items = null };
        Assert.IsNull(PipeWireGlobalParser.ParseNode(1, PipeWirePermissions.None, 3, PipeWireProperties.From(&empty)).NodeName);

        // n_items claiming four entries behind a null pointer is the shape a corrupted message takes.
        var lying = new spa_dict { flags = 0, n_items = 4, items = null };
        Assert.IsNull(PipeWireGlobalParser.ParseNode(2, PipeWirePermissions.None, 3, PipeWireProperties.From(&lying)).NodeName,
            "a null items array must read as no properties, not be dereferenced");
    }

    [TestMethod]
    public unsafe void ANullPropertyDictionary_IsTolerated()
    {
        PipeWireNode node = PipeWireGlobalParser.ParseNode(1, PipeWirePermissions.None, 3, PipeWireProperties.From(null));
        Assert.AreEqual(1u, node.NodeId, "props may be null; the node still exists");
        Assert.IsNull(node.NodeName);
    }

    [TestMethod]
    public unsafe void AnItemWithANullKey_IsSkippedRatherThanDereferenced()
    {
        spa_dict_item* items = (spa_dict_item*)NativeMemory.AllocZeroed((nuint)(sizeof(spa_dict_item) * 2));
        try
        {
            byte[] key = Encoding.UTF8.GetBytes("node.name\0");
            byte[] val = Encoding.UTF8.GetBytes("kept\0");
            fixed (byte* pk = key)
            fixed (byte* pv = val)
            {
                items[0].key = null;                 // the hostile entry
                items[0].value = (sbyte*)pv;
                items[1].key = (sbyte*)pk;
                items[1].value = (sbyte*)pv;

                var dict = new spa_dict { flags = 0, n_items = 2, items = items };
                PipeWireNode node = PipeWireGlobalParser.ParseNode(1, PipeWirePermissions.None, 3, PipeWireProperties.From(&dict));
                Assert.AreEqual("kept", node.NodeName, "a null key must be skipped, not crash the walk");
            }
        }
        finally
        {
            NativeMemory.Free(items);
        }
    }

    [TestMethod]
    public unsafe void ParsingNeverThrows_ForAnyCombinationOfMissingProperties()
    {
        // Every callback runs inside a reverse P/Invoke, where an escaping exception aborts the
        // process. Nothing below may throw, whatever the dictionary looks like.
        string[] keys = ["node.id", "port.direction", "port.monitor", "link.output.node", "media.class"];
        string?[] values = [null, "", "x", "0", "true", "4294967296"];

        foreach (string k in keys)
            foreach (string? v in values)
            {
                using var dict = new NativeDict((k, v));
                fixed (spa_dict* d = &dict.Dict)
                {
                    _ = PipeWireGlobalParser.ParseNode(1, PipeWirePermissions.None, 3, PipeWireProperties.From(d));
                    _ = PipeWireGlobalParser.TryParsePort(1, PipeWirePermissions.None, 3, PipeWireProperties.From(d), out _, out _, out _);
                    _ = PipeWireGlobalParser.TryParseLink(1, PipeWirePermissions.None, 3, PipeWireProperties.From(d), out _, out _, out _);
                }
            }
    }
}
