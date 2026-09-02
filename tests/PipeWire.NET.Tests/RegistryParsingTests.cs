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
public sealed class RegistryParsingTests
{

    // ------------------------------------------------------------------ ports

    [TestMethod]
    public unsafe void APortWithNoNodeId_IsSkippedWithAReason()
    {
        using var dict = new NativeDict(("port.direction", "out"), ("port.name", "orphan"));
        fixed (spa_dict* d = &dict.Dict)
        {
            Assert.IsFalse(PipeWireGlobalParser.TryParsePort(
                42, PipeWirePermissions.Read, 3, d,
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
                PipeWireGlobalParser.TryParsePort(42, PipeWirePermissions.Read, 3, d, out _, out _, out _),
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
                42, PipeWirePermissions.Read, 3, d, out port, out _, out _),
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
                42, PipeWirePermissions.Read, 3, d, out _, out string reason, out _),
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
            ("port.name", "capture_FL"), ("port.monitor", "true"), ("port.exclusive", "1"));

        PipeWirePort? port;
        fixed (spa_dict* d = &dict.Dict)
            Assert.IsTrue(PipeWireGlobalParser.TryParsePort(
                42, PipeWirePermissions.Read | PipeWirePermissions.Write, 3, d, out port, out _, out _));

        Assert.IsNotNull(port);
        Assert.AreEqual(42u, port!.PortId);
        Assert.AreEqual(7u, port.NodeId);
        Assert.AreEqual(expected, port.PortDirection);
        Assert.AreEqual("capture_FL", port.PortName);
        Assert.IsTrue(port.Monitor);
        Assert.IsTrue(port.Exclusive);
        Assert.AreEqual(3u, port.InterfaceVersion);
        Assert.IsTrue(port.Permissions.HasFlag(PipeWirePermissions.Write));
    }

    [TestMethod]
    public unsafe void APortWithOnlyItsMandatoryProperties_StillParses()
    {
        using var dict = new NativeDict(("node.id", "7"), ("port.direction", "in"));
        PipeWirePort? port;
        fixed (spa_dict* d = &dict.Dict)
            Assert.IsTrue(PipeWireGlobalParser.TryParsePort(
                42, PipeWirePermissions.None, 3, d, out port, out _, out _));

        Assert.IsNotNull(port);
        Assert.IsNull(port!.PortName);
        Assert.IsFalse(port.Monitor, "an absent port.monitor is false, matching spa_atob");
        Assert.IsFalse(port.Exclusive);
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
                42, PipeWirePermissions.None, 3, d, out port, out _, out _));

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
                99, PipeWirePermissions.Read, 3, d,
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
                99, PipeWirePermissions.Read, 3, d, out _, out _, out string? offending));
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
                99, PipeWirePermissions.Read, 3, d, out link, out _, out _));

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
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, d);

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
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, d);

        Assert.AreEqual("alsa_output.pci", node.NodeName);
        Assert.AreEqual("Speakers", node.Description);
        Assert.AreEqual("Spk", node.NodeNick);
        Assert.AreEqual("Audio/Sink", node.MediaClass);
        Assert.AreEqual(PipeWireMediaKind.Audio, node.Media);
        Assert.AreEqual(PipeWireMediaFlow.Sink, node.Flow);
    }

    [TestMethod]
    public unsafe void APropertyWithANullValue_ReadsAsAbsentAndDoesNotStopTheRest()
    {
        // spa_dict_item.value may be null. That must not be dereferenced, and must not abandon the
        // remaining properties.
        using var dict = new NativeDict(("node.name", null), ("media.class", "Audio/Sink"));
        PipeWireNode node;
        fixed (spa_dict* d = &dict.Dict)
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, d);

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
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, d);

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
            node = PipeWireGlobalParser.ParseNode(5, PipeWirePermissions.Read, 3, d);

        Assert.AreEqual("first", node.NodeName);
    }

    // ------------------------------------------------------------------ hostile dictionaries

    [TestMethod]
    public unsafe void AnEmptyDictAndALyingItemCount_AreBothTolerated()
    {
        var empty = new spa_dict { flags = 0, n_items = 0, items = null };
        Assert.IsNull(PipeWireGlobalParser.ParseNode(1, PipeWirePermissions.None, 3, &empty).NodeName);

        // n_items claiming four entries behind a null pointer is the shape a corrupted message takes.
        var lying = new spa_dict { flags = 0, n_items = 4, items = null };
        Assert.IsNull(PipeWireGlobalParser.ParseNode(2, PipeWirePermissions.None, 3, &lying).NodeName,
            "a null items array must read as no properties, not be dereferenced");
    }

    [TestMethod]
    public unsafe void ANullPropertyDictionary_IsTolerated()
    {
        PipeWireNode node = PipeWireGlobalParser.ParseNode(1, PipeWirePermissions.None, 3, null);
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
                PipeWireNode node = PipeWireGlobalParser.ParseNode(1, PipeWirePermissions.None, 3, &dict);
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
                    _ = PipeWireGlobalParser.ParseNode(1, PipeWirePermissions.None, 3, d);
                    _ = PipeWireGlobalParser.TryParsePort(1, PipeWirePermissions.None, 3, d, out _, out _, out _);
                    _ = PipeWireGlobalParser.TryParseLink(1, PipeWirePermissions.None, 3, d, out _, out _, out _);
                }
            }
    }
}
