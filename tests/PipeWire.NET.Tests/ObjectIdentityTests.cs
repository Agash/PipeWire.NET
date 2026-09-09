using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PipeWire.NET.Graph;

namespace PipeWire.NET.Tests;

/// <summary>
/// Telling "the same object" from "something else with that id".
/// </summary>
[TestClass]
[SupportedOSPlatform("linux")]
public sealed class ObjectIdentityTests : PipeWireTestBase
{
    private static PipeWireNode Node(uint id, ulong? serial, string? name = "n") =>
        new(id, name, null, null, null, PipeWirePermissions.None, 3,
            Properties: serial is null
                ? PipeWireProperties.FromItems(new Dictionary<string, string> { ["node.name"] = name ?? "n" })
                : PipeWireProperties.FromItems(new Dictionary<string, string>
                {
                    ["node.name"] = name ?? "n",
                    [PipeWireNames.ObjectSerial] = serial.Value.ToString(CultureInfo.InvariantCulture),
                }));

    private static PipeWireGraphSnapshot Graph(params PipeWireNode[] nodes) =>
        new(1, nodes, [], []);

    [TestMethod]
    public void AnObjectWhoseIdWasReused_IsNotStillInTheGraph()
    {
        PipeWireNode held = Node(42, serial: 900);
        PipeWireGraphSnapshot after = Graph(Node(42, serial: 1500, name: "somebody else"));

        Assert.IsFalse(held.IsStillIn(after),
            "the id came back attached to a different object, so the held one is gone");
        Assert.IsTrue(Node(42, serial: 900).IsStillIn(Graph(Node(42, serial: 900))));
    }

    [TestMethod]
    public void AnObjectThatLeftTheGraph_IsNotStillInIt()
    {
        Assert.IsFalse(Node(42, serial: 900).IsStillIn(Graph()));
    }

    [TestMethod]
    public void WithNoSerialToCompare_TheKindIsAllThereIsToGoOn()
    {
        // Weaker, and deliberately so: a daemon that sends no serial leaves nothing better. It
        // still catches the id coming back as a different kind of object, which is the loud case.
        PipeWireNode held = Node(42, serial: null);

        Assert.IsTrue(held.IsStillIn(Graph(Node(42, serial: null, name: "renamed"))));
        Assert.IsFalse(held.IsStillIn(Graph()));
    }

    [TestMethod]
    public void ASerialOnOneSideOnly_DoesNotCountAsAMismatch()
    {
        // Enrichment fills properties in, so a record read before it has no serial and the one
        // after does. That is the same object, and must not read as a reuse.
        Assert.IsTrue(Node(42, serial: null).IsStillIn(Graph(Node(42, serial: 900))));
        Assert.IsTrue(Node(42, serial: 900).IsStillIn(Graph(Node(42, serial: null))));
    }
}
